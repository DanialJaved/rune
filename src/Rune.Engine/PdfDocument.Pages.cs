using System.Runtime.InteropServices;
using Rune.PdfiumInterop;

namespace Rune.Engine;

// Page organization: delete / move / export / insert. Every operation runs
// under the global PDFium lock and is expected to be scheduled on the render
// thread (RenderScheduler.RunAsync) by the app layer. After any mutation the
// caller must treat ALL cached page-derived state (tiles, text maps, links,
// thumbnails, annotations indices, outline) as stale.
public sealed partial class PdfDocument
{
    private static bool _movePagesUnavailable;

    /// <summary>
    /// Deletes the given pages. Refuses to delete every page — a PDF with
    /// zero pages is unopenable, which would brick the file on save.
    /// </summary>
    public void DeletePages(IReadOnlyList<int> pageIndices)
    {
        var indices = NormalizeIndices(pageIndices);
        if (indices.Count == 0)
        {
            return;
        }
        if (indices.Count >= PageCount)
        {
            throw new InvalidOperationException("Cannot delete every page of a document.");
        }

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            // Descending order so earlier deletions don't shift later indices.
            for (int i = indices.Count - 1; i >= 0; i--)
            {
                PdfiumNative.DeletePage(_handle, indices[i]);
            }
            ReadPageMetricsLocked();
        }
        IsDirty = true;
    }

    /// <summary>
    /// Moves pages so the block starts at <paramref name="destIndex"/> in the
    /// final ordering (FPDF_MovePages semantics, mirrored by
    /// <see cref="BookmarkRemap.MovePermutation"/>).
    /// </summary>
    public void MovePages(IReadOnlyList<int> pageIndices, int destIndex)
    {
        var indices = NormalizeIndices(pageIndices);
        if (indices.Count == 0)
        {
            return;
        }
        destIndex = Math.Clamp(destIndex, 0, PageCount - indices.Count);

        if (!_movePagesUnavailable)
        {
            try
            {
                lock (PdfiumLibrary.Lock)
                {
                    ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
                    if (!PdfiumNative.MovePages(_handle, [.. indices], destIndex))
                    {
                        throw new PdfiumException("Moving pages failed.", 1);
                    }
                    ReadPageMetricsLocked();
                }
                IsDirty = true;
                return;
            }
            catch (EntryPointNotFoundException)
            {
                _movePagesUnavailable = true; // older pdfium build: fall through
            }
        }

        // Fallback: export the block, delete the originals, re-import at the
        // destination. destIndex already counts positions among the remaining
        // pages, which is exactly where InsertPages places the block.
        var bytes = ExportPages(indices);
        DeletePages(indices);
        InsertPages(bytes, destIndex);
    }

    /// <summary>Serializes the given pages as a standalone PDF (clipboard / undo snapshots).</summary>
    public byte[] ExportPages(IReadOnlyList<int> pageIndices)
    {
        var indices = NormalizeIndices(pageIndices);
        if (indices.Count == 0)
        {
            return [];
        }

        using var stream = new MemoryStream();
        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr export = PdfiumNative.CreateNewDocument();
            if (export == IntPtr.Zero)
            {
                throw new PdfiumException("Could not create the export document.", 1);
            }
            try
            {
                if (!PdfiumNative.ImportPagesByIndex(export, _handle, [.. indices], 0))
                {
                    throw new PdfiumException("Copying the pages failed.", 1);
                }
                if (!PdfiumNative.SaveCopy(export, stream))
                {
                    throw new PdfiumException("Serializing the copied pages failed.", 1);
                }
            }
            finally
            {
                PdfiumNative.CloseDocument(export);
            }
        }
        return stream.ToArray();
    }

    /// <summary>Inserts all pages of a serialized PDF at <paramref name="destIndex"/>. Returns the count inserted.</summary>
    public int InsertPages(byte[] pdfBytes, int destIndex)
    {
        if (pdfBytes.Length == 0)
        {
            return 0;
        }
        destIndex = Math.Clamp(destIndex, 0, PageCount);

        // The buffer must stay pinned for the whole life of the memory doc.
        var pin = GCHandle.Alloc(pdfBytes, GCHandleType.Pinned);
        try
        {
            int inserted;
            lock (PdfiumLibrary.Lock)
            {
                ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
                IntPtr source = PdfiumNative.LoadMemDocument(pin.AddrOfPinnedObject(), pdfBytes.Length, null);
                if (source == IntPtr.Zero)
                {
                    throw PdfiumNative.LastError();
                }
                try
                {
                    inserted = PdfiumNative.GetPageCount(source);
                    if (!PdfiumNative.ImportPagesByIndex(_handle, source, null, destIndex))
                    {
                        throw new PdfiumException("Inserting the pages failed.", 1);
                    }
                }
                finally
                {
                    PdfiumNative.CloseDocument(source);
                }
                ReadPageMetricsLocked();
            }
            IsDirty = true;
            return inserted;
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>Inserts every page of another PDF file at <paramref name="destIndex"/>. Returns the count inserted.</summary>
    public int InsertPagesFromFile(string path, int destIndex)
    {
        destIndex = Math.Clamp(destIndex, 0, PageCount);

        var fileAccess = new FileAccessAdapter(path);
        try
        {
            int inserted;
            lock (PdfiumLibrary.Lock)
            {
                ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
                IntPtr source = PdfiumNative.LoadCustomDocument(fileAccess, null);
                if (source == IntPtr.Zero)
                {
                    throw PdfiumNative.LastError();
                }
                try
                {
                    inserted = PdfiumNative.GetPageCount(source);
                    if (!PdfiumNative.ImportPagesByIndex(_handle, source, null, destIndex))
                    {
                        throw new PdfiumException("Inserting the pages failed.", 1);
                    }
                }
                finally
                {
                    PdfiumNative.CloseDocument(source);
                }
                ReadPageMetricsLocked();
            }
            IsDirty = true;
            return inserted;
        }
        finally
        {
            fileAccess.Dispose();
        }
    }

    /// <summary>
    /// Undoes a <see cref="MovePages"/>: scatters the (now contiguous) block
    /// back to the pages' original, possibly non-contiguous positions. A
    /// scatter is not expressible as one FPDF_MovePages call, so it runs as a
    /// series of single-page moves guided by the permutation model.
    /// </summary>
    public void RestoreMovedPages(IReadOnlyList<int> movedIndices, int destIndex)
    {
        var sorted = NormalizeIndices(movedIndices);
        if (sorted.Count == 0)
        {
            return;
        }
        destIndex = Math.Clamp(destIndex, 0, PageCount - sorted.Count);

        // order[newIdx] = originalIdx describes the CURRENT (post-move) order.
        var map = BookmarkRemap.MovePermutation(PageCount, sorted, destIndex);
        var order = new int[PageCount];
        for (int oldIndex = 0; oldIndex < map.Length; oldIndex++)
        {
            order[map[oldIndex]] = oldIndex;
        }

        var current = new List<int>(order);
        foreach (int original in sorted)
        {
            int position = current.IndexOf(original);
            if (position != original)
            {
                MovePages([position], original);
                current.RemoveAt(position);
                current.Insert(original, original);
            }
        }
    }

    /// <summary>Distinct, sorted, in-range page indices.</summary>
    private List<int> NormalizeIndices(IReadOnlyList<int> pageIndices)
    {
        var result = pageIndices.Distinct().Where(i => i >= 0 && i < PageCount).ToList();
        result.Sort();
        return result;
    }
}
