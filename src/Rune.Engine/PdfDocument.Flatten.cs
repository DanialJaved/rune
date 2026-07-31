using Rune.PdfiumInterop;

namespace Rune.Engine;

/// <summary>Outcome of flattening one page.</summary>
public enum FlattenResult
{
    /// <summary>PDFium could not flatten the page; its content is unchanged.</summary>
    Failed,
    /// <summary>Annotations and widgets were baked into the page content.</summary>
    Flattened,
    /// <summary>The page had nothing to flatten.</summary>
    NothingToDo,
}

// Flatten bakes annotations and form widgets into the page's own content
// stream, so highlights, ink, filled fields and signature stamps become
// ordinary page graphics that every viewer draws identically and nobody can
// edit back out.
//
// This is destructive and has no undo — the annotation objects are gone
// afterwards, not hidden. The app layer must confirm before calling it.
public sealed partial class PdfDocument
{
    /// <summary>
    /// Flattens one page. <paramref name="forPrint"/> selects the /Print
    /// appearance instead of the on-screen one (annotations may define both).
    /// </summary>
    public FlattenResult FlattenPage(int pageIndex, bool forPrint = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);

            // Commit any in-progress field edit first, or it is flattened at
            // its previous value and the user's last keystrokes vanish into an
            // irreversible operation.
            if (_formEnv is { Handle: not 0, IsXfa: false } env)
            {
                PdfiumNative.FormKillFocus(env.Handle);
            }

            IntPtr page = AcquirePageLocked(pageIndex);
            if (page == IntPtr.Zero)
            {
                return FlattenResult.Failed;
            }

            int code;
            try
            {
                code = PdfiumNative.FlattenPage(page, forPrint);
            }
            finally
            {
                ReleasePageLocked(pageIndex);
            }

            if (code == PdfiumNative.FlattenSuccess)
            {
                // Flattening rewrites the page's content and annotation list.
                // Drop every cached handle so nothing keeps serving the old
                // structure, and mark the document dirty so it can be saved.
                ReleaseAllPagesLocked();
                IsDirty = true;
                return FlattenResult.Flattened;
            }

            return code == PdfiumNative.FlattenNothingToDo ? FlattenResult.NothingToDo : FlattenResult.Failed;
        }
    }

    /// <summary>
    /// Flattens every page, reporting progress as (pagesDone, pageCount).
    /// Returns the number of pages actually changed.
    /// </summary>
    public int FlattenAllPages(bool forPrint = false, Action<int, int>? onProgress = null, CancellationToken cancellationToken = default)
    {
        int changed = 0;
        int total = PageCount;
        for (int i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FlattenPage(i, forPrint) == FlattenResult.Flattened)
            {
                changed++;
            }
            onProgress?.Invoke(i + 1, total);
        }
        return changed;
    }

    /// <summary>True when there is anything on any page that flattening would bake in.</summary>
    public bool HasFlattenableContent()
    {
        if (HasFillableForm)
        {
            return true;
        }
        for (int i = 0; i < PageCount; i++)
        {
            if (GetAnnotations(i).Count > 0)
            {
                return true;
            }
        }
        return false;
    }
}
