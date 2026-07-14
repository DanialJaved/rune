using Rune.PdfiumInterop;

namespace Rune.Engine;

public enum MarkupKind
{
    Highlight = PdfiumNative.AnnotHighlight,
    Underline = PdfiumNative.AnnotUnderline,
    Strikeout = PdfiumNative.AnnotStrikeout,
}

/// <summary>An annotation summary for hit-testing and listing. Rect is in page points, top-left origin.</summary>
public sealed record AnnotationInfo(int Index, int Subtype, double X, double Y, double Width, double Height, string Contents)
{
    public bool IsNote => Subtype == PdfiumNative.AnnotText;
}

public sealed partial class PdfDocument
{
    /// <summary>True when annotations were added/removed since open or last save.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>
    /// Adds a text-markup annotation (highlight/underline/strikeout) covering
    /// the given rects, which are in page points with a TOP-LEFT origin (the
    /// same space <see cref="TextSelection"/> rects use).
    /// </summary>
    public void AddMarkup(int pageIndex, MarkupKind kind, IReadOnlyList<TextRect> rects, byte r, byte g, byte b, byte a)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);
        if (rects.Count == 0)
        {
            return;
        }

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = PdfiumNative.LoadPage(_handle, pageIndex);
            if (page == IntPtr.Zero)
            {
                throw PdfiumNative.LastError();
            }

            try
            {
                IntPtr annot = PdfiumNative.CreateAnnot(page, (int)kind);
                if (annot == IntPtr.Zero)
                {
                    throw new PdfiumException("Could not create the annotation.", 1);
                }

                try
                {
                    // Convert every top-left rect to PDF page space (bottom-left
                    // origin) via DeviceToPage so pages with /Rotate map correctly.
                    var (ptW, ptH) = _pageSizes[pageIndex];
                    int sizeX = Math.Max(1, (int)MathF.Round(ptW));
                    int sizeY = Math.Max(1, (int)MathF.Round(ptH));

                    float minL = float.MaxValue, minB = float.MaxValue, maxR = float.MinValue, maxT = float.MinValue;
                    foreach (var rect in rects)
                    {
                        var (x1, y1) = PdfiumNative.DeviceToPage(page, sizeX, sizeY, (int)Math.Round(rect.X), (int)Math.Round(rect.Y));
                        var (x2, y2) = PdfiumNative.DeviceToPage(page, sizeX, sizeY, (int)Math.Round(rect.X + rect.Width), (int)Math.Round(rect.Y + rect.Height));
                        float left = (float)Math.Min(x1, x2);
                        float right = (float)Math.Max(x1, x2);
                        float bottom = (float)Math.Min(y1, y2);
                        float top = (float)Math.Max(y1, y2);

                        PdfiumNative.AppendQuad(annot, left, bottom, right, top);
                        minL = Math.Min(minL, left);
                        minB = Math.Min(minB, bottom);
                        maxR = Math.Max(maxR, right);
                        maxT = Math.Max(maxT, top);
                    }

                    PdfiumNative.SetAnnotRect(annot, minL, minB, maxR, maxT);
                    PdfiumNative.SetAnnotColor(annot, r, g, b, a);
                    PdfiumNative.SetAnnotPrintFlag(annot);
                }
                finally
                {
                    PdfiumNative.CloseAnnot(annot);
                }
            }
            finally
            {
                PdfiumNative.ClosePage(page);
            }
        }
        IsDirty = true;
    }

    /// <summary>Adds a sticky-note annotation at (x, y) in page points, top-left origin.</summary>
    public void AddNote(int pageIndex, double x, double y, string text)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = PdfiumNative.LoadPage(_handle, pageIndex);
            if (page == IntPtr.Zero)
            {
                throw PdfiumNative.LastError();
            }

            try
            {
                IntPtr annot = PdfiumNative.CreateAnnot(page, PdfiumNative.AnnotText);
                if (annot == IntPtr.Zero)
                {
                    throw new PdfiumException("Could not create the note.", 1);
                }

                try
                {
                    var (ptW, ptH) = _pageSizes[pageIndex];
                    var (px, py) = PdfiumNative.DeviceToPage(
                        page,
                        Math.Max(1, (int)MathF.Round(ptW)), Math.Max(1, (int)MathF.Round(ptH)),
                        (int)Math.Round(x), (int)Math.Round(y));

                    // Standard note icon size ~20pt, anchored at the click point.
                    const float size = 20f;
                    PdfiumNative.SetAnnotRect(annot, (float)px, (float)py - size, (float)px + size, (float)py);
                    PdfiumNative.SetAnnotString(annot, "Contents", text);
                    PdfiumNative.SetAnnotColor(annot, 255, 200, 0, 255);
                    PdfiumNative.SetAnnotPrintFlag(annot);
                }
                finally
                {
                    PdfiumNative.CloseAnnot(annot);
                }
            }
            finally
            {
                PdfiumNative.ClosePage(page);
            }
        }
        IsDirty = true;
    }

    /// <summary>All annotations on a page, rects in page points with top-left origin.</summary>
    public IReadOnlyList<AnnotationInfo> GetAnnotations(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

        var result = new List<AnnotationInfo>();
        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = PdfiumNative.LoadPage(_handle, pageIndex);
            if (page == IntPtr.Zero)
            {
                return result;
            }

            try
            {
                var (ptW, ptH) = _pageSizes[pageIndex];
                int sizeX = Math.Max(1, (int)MathF.Round(ptW));
                int sizeY = Math.Max(1, (int)MathF.Round(ptH));

                int count = PdfiumNative.GetAnnotCount(page);
                for (int i = 0; i < count; i++)
                {
                    IntPtr annot = PdfiumNative.GetAnnot(page, i);
                    if (annot == IntPtr.Zero)
                    {
                        continue;
                    }
                    try
                    {
                        if (!PdfiumNative.GetAnnotRect(annot, out float l, out float t, out float rr, out float bb))
                        {
                            continue;
                        }
                        // Page rect (bottom-left origin) → top-left points via PageToDevice.
                        var (dx1, dy1) = PdfiumNative.PageToDevice(page, sizeX, sizeY, 0, l, t);
                        var (dx2, dy2) = PdfiumNative.PageToDevice(page, sizeX, sizeY, 0, rr, bb);
                        double x = Math.Min(dx1, dx2), y = Math.Min(dy1, dy2);
                        result.Add(new AnnotationInfo(
                            i, PdfiumNative.GetAnnotSubtype(annot),
                            x, y, Math.Abs(dx2 - dx1), Math.Abs(dy2 - dy1),
                            PdfiumNative.GetAnnotString(annot, "Contents")));
                    }
                    finally
                    {
                        PdfiumNative.CloseAnnot(annot);
                    }
                }
            }
            finally
            {
                PdfiumNative.ClosePage(page);
            }
        }
        return result;
    }

    public bool RemoveAnnotation(int pageIndex, int annotIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

        bool removed;
        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = PdfiumNative.LoadPage(_handle, pageIndex);
            if (page == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                removed = PdfiumNative.RemoveAnnot(page, annotIndex);
            }
            finally
            {
                PdfiumNative.ClosePage(page);
            }
        }
        IsDirty |= removed;
        return removed;
    }

    /// <summary>
    /// Writes a full copy of the document (with all annotation edits) to
    /// <paramref name="path"/>. The source file stays open; saving in place
    /// requires close → swap → reopen, which the app layer orchestrates.
    /// </summary>
    public void SaveAs(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            if (!PdfiumNative.SaveCopy(_handle, stream))
            {
                throw new PdfiumException("Saving the PDF failed.", 1);
            }
        }
    }

    /// <summary>Marks the document clean after a successful save-in-place swap.</summary>
    public void MarkSaved() => IsDirty = false;
}
