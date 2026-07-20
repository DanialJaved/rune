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

/// <summary>
/// Everything needed to faithfully re-create one of Rune's annotation
/// subtypes (markup/ink/note) — captured before a deletion so undo can
/// rebuild it. All geometry is in PDF page space (bottom-left origin).
/// </summary>
public sealed record AnnotationSpec(
    int PageIndex,
    int Subtype,
    IReadOnlyList<(float L, float B, float R, float T)> Quads,
    IReadOnlyList<IReadOnlyList<(float X, float Y)>> InkStrokes,
    (float L, float B, float R, float T) Rect,
    (byte R, byte G, byte B, byte A) Color,
    float BorderWidth,
    string Contents);

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

    /// <summary>
    /// Adds a freehand ink annotation. <paramref name="stroke"/> is a polyline
    /// in page points with a top-left origin (canvas space). One annotation per
    /// stroke, so each is individually deletable. <paramref name="width"/> is in
    /// points.
    /// </summary>
    public void AddInk(int pageIndex, IReadOnlyList<(double X, double Y)> stroke, byte r, byte g, byte b, byte a, float width)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);
        if (stroke.Count < 2)
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
                IntPtr annot = PdfiumNative.CreateAnnot(page, PdfiumNative.AnnotInk);
                if (annot == IntPtr.Zero)
                {
                    throw new PdfiumException("Could not create the ink annotation.", 1);
                }

                try
                {
                    var (ptW, ptH) = _pageSizes[pageIndex];
                    int sizeX = Math.Max(1, (int)MathF.Round(ptW));
                    int sizeY = Math.Max(1, (int)MathF.Round(ptH));

                    var points = new (float X, float Y)[stroke.Count];
                    float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                    for (int i = 0; i < stroke.Count; i++)
                    {
                        var (px, py) = PdfiumNative.DeviceToPage(page, sizeX, sizeY, (int)Math.Round(stroke[i].X), (int)Math.Round(stroke[i].Y));
                        points[i] = ((float)px, (float)py);
                        minX = Math.Min(minX, (float)px);
                        minY = Math.Min(minY, (float)py);
                        maxX = Math.Max(maxX, (float)px);
                        maxY = Math.Max(maxY, (float)py);
                    }

                    float pad = width / 2 + 1;
                    PdfiumNative.SetAnnotRect(annot, minX - pad, minY - pad, maxX + pad, maxY + pad);
                    PdfiumNative.SetAnnotColor(annot, r, g, b, a);
                    PdfiumNative.SetAnnotBorderWidth(annot, width);
                    PdfiumNative.AddInkStroke(annot, points);
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

    /// <summary>Removes the newest annotation on a page (undo of an add — ours are always appended).</summary>
    public bool RemoveLastAnnotation(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

        bool removed = false;
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
                int count = PdfiumNative.GetAnnotCount(page);
                if (count > 0)
                {
                    removed = PdfiumNative.RemoveAnnot(page, count - 1);
                }
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
    /// Reads everything needed to re-create one of OUR annotation subtypes
    /// (markup/ink/note) so a deletion can be undone. Returns null for
    /// subtypes we can't faithfully rebuild (callers fall back to a page
    /// snapshot). All geometry is in page space (bottom-left origin).
    /// </summary>
    public AnnotationSpec? CaptureAnnotation(int pageIndex, int annotIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = PdfiumNative.LoadPage(_handle, pageIndex);
            if (page == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                IntPtr annot = PdfiumNative.GetAnnot(page, annotIndex);
                if (annot == IntPtr.Zero)
                {
                    return null;
                }
                try
                {
                    int subtype = PdfiumNative.GetAnnotSubtype(annot);
                    bool isMarkup = subtype is PdfiumNative.AnnotHighlight
                        or PdfiumNative.AnnotUnderline or PdfiumNative.AnnotStrikeout;
                    if (!isMarkup && subtype != PdfiumNative.AnnotInk && subtype != PdfiumNative.AnnotText)
                    {
                        return null;
                    }

                    PdfiumNative.GetAnnotRect(annot, out float l, out float t, out float r, out float b);
                    PdfiumNative.GetAnnotColor(annot, out byte cr, out byte cg, out byte cb, out byte ca);

                    var quads = new List<(float L, float B, float R, float T)>();
                    if (isMarkup)
                    {
                        int quadCount = PdfiumNative.CountAnnotQuads(annot);
                        for (int i = 0; i < quadCount; i++)
                        {
                            if (PdfiumNative.GetAnnotQuad(annot, i, out float ql, out float qb, out float qr, out float qt))
                            {
                                quads.Add((ql, qb, qr, qt));
                            }
                        }
                    }

                    var strokes = new List<IReadOnlyList<(float X, float Y)>>();
                    if (subtype == PdfiumNative.AnnotInk)
                    {
                        int strokeCount = PdfiumNative.GetInkStrokeCount(annot);
                        for (int i = 0; i < strokeCount; i++)
                        {
                            var points = PdfiumNative.GetInkStroke(annot, i);
                            if (points.Length > 0)
                            {
                                strokes.Add(points);
                            }
                        }
                    }

                    return new AnnotationSpec(
                        pageIndex, subtype, quads, strokes,
                        (Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b)),
                        (cr, cg, cb, ca == 0 ? (byte)255 : ca),
                        PdfiumNative.GetAnnotBorderWidth(annot),
                        PdfiumNative.GetAnnotString(annot, "Contents"));
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
    }

    /// <summary>Captures the newest annotation on a page (undo of an add — ours are always appended). Null if none/exotic.</summary>
    public AnnotationSpec? CaptureLastAnnotation(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

        int lastIndex;
        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = PdfiumNative.LoadPage(_handle, pageIndex);
            if (page == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                lastIndex = PdfiumNative.GetAnnotCount(page) - 1;
            }
            finally
            {
                PdfiumNative.ClosePage(page);
            }
        }
        return lastIndex < 0 ? null : CaptureAnnotation(pageIndex, lastIndex);
    }

    /// <summary>Re-creates an annotation from a captured spec (appended — becomes the page's last annotation).</summary>
    public void AddAnnotationFromSpec(AnnotationSpec spec)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(spec.PageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(spec.PageIndex, PageCount);

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = PdfiumNative.LoadPage(_handle, spec.PageIndex);
            if (page == IntPtr.Zero)
            {
                throw PdfiumNative.LastError();
            }
            try
            {
                IntPtr annot = PdfiumNative.CreateAnnot(page, spec.Subtype);
                if (annot == IntPtr.Zero)
                {
                    throw new PdfiumException("Could not re-create the annotation.", 1);
                }
                try
                {
                    foreach (var (ql, qb, qr, qt) in spec.Quads)
                    {
                        PdfiumNative.AppendQuad(annot, ql, qb, qr, qt);
                    }
                    foreach (var stroke in spec.InkStrokes)
                    {
                        PdfiumNative.AddInkStroke(annot, [.. stroke]);
                    }
                    var (rl, rb, rr, rt) = spec.Rect;
                    PdfiumNative.SetAnnotRect(annot, rl, rb, rr, rt);
                    PdfiumNative.SetAnnotColor(annot, spec.Color.R, spec.Color.G, spec.Color.B, spec.Color.A);
                    if (spec.BorderWidth > 0)
                    {
                        PdfiumNative.SetAnnotBorderWidth(annot, spec.BorderWidth);
                    }
                    if (!string.IsNullOrEmpty(spec.Contents))
                    {
                        PdfiumNative.SetAnnotString(annot, "Contents", spec.Contents);
                    }
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
