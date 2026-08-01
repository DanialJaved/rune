using Rune.PdfiumInterop;

namespace Rune.Engine;

// Image stamps — the mechanism behind visible signatures.
//
// A stamp is a subtype-13 annotation carrying an image object, rather than a
// raw page-content image. That choice matters: as an annotation it stays a
// discrete object the user can erase, it round-trips through the existing undo
// machinery via RemoveLastAnnotation, and it survives Flatten by being baked in
// like every other annotation. A bare page-content image would need
// FPDFPage_RemoveObject to undo, which isn't bound.
public sealed partial class PdfDocument
{
    /// <summary>
    /// Stamps a BGRA image onto a page.
    ///
    /// Takes raw pixels rather than a file path deliberately: image decoding
    /// lives in WinUI, which the engine and its tests cannot reference, so a
    /// path-taking overload would be untestable.
    /// </summary>
    /// <param name="x">Left edge, page-local points, top-left origin.</param>
    /// <param name="y">Top edge, page-local points, top-left origin.</param>
    /// <param name="widthPt">Placed width in points.</param>
    /// <param name="heightPt">Placed height in points.</param>
    /// <param name="bgra">Straight (non-premultiplied) BGRA, 4 bytes each, stride = width * 4.</param>
    /// <returns>
    /// The spec needed to re-create this stamp, so the caller can make it
    /// undoable — or null if nothing was placed.
    /// </returns>
    public AnnotationSpec? AddStamp(
        int pageIndex, double x, double y, double widthPt, double heightPt,
        byte[] bgra, int pixelWidth, int pixelHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);

        if (bgra.Length < pixelWidth * pixelHeight * 4 || widthPt <= 0 || heightPt <= 0)
        {
            return null; // nothing sensible to place; leave IsDirty alone
        }

        AnnotationSpec spec;
        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = AcquirePageLocked(pageIndex);
            if (page == IntPtr.Zero)
            {
                throw PdfiumNative.LastError();
            }

            try
            {
                // Top-left page-local points → PDF page space (bottom-left).
                // Shared with MoveAnnotation so a placed stamp and a moved one
                // can never disagree about where a given point lands.
                var (l, b, r, t) = ToPageRectLocked(page, pageIndex, x, y, widthPt, heightPt);

                IntPtr annot = PdfiumNative.CreateAnnot(page, PdfiumNative.AnnotStamp);
                if (annot == IntPtr.Zero)
                {
                    throw new PdfiumException("Could not create the stamp annotation.", 1);
                }
                try
                {
                    PdfiumNative.SetAnnotRect(annot, l, b, r, t);
                    var stamp = new StampImage(bgra, pixelWidth, pixelHeight);
                    AttachStampImageLocked(page, annot, stamp, l, b, r, t);
                    PdfiumNative.SetAnnotPrintFlag(annot);

                    spec = new AnnotationSpec(
                        pageIndex,
                        PdfiumNative.AnnotStamp,
                        Quads: [],
                        InkStrokes: [],
                        Rect: (l, b, r, t),
                        Color: (0, 0, 0, 0),
                        BorderWidth: 0,
                        Contents: string.Empty,
                        Stamp: stamp);
                }
                finally
                {
                    PdfiumNative.CloseAnnot(annot);
                }
            }
            finally
            {
                ReleasePageLocked(pageIndex);
            }
        }

        IsDirty = true;
        return spec;
    }

    /// <summary>
    /// Moves an existing annotation so its top-left sits at (<paramref name="x"/>,
    /// <paramref name="y"/>) in page-local points. Returns the rect it
    /// previously occupied (PDF page space, bottom-left origin) so the caller
    /// can make the move undoable, or null if there was no such annotation.
    ///
    /// **Size is preserved, not taken as a parameter.** PDFium translates a
    /// stamp's appearance to the annotation rect but does not scale it to fit,
    /// so a rect of a different size would report one thing and draw another —
    /// see <see cref="ApplyAnnotationRectLocked"/> for the measurements.
    ///
    /// A move is a rect change rather than a delete-and-re-create because
    /// re-creating needs the stamp's pixels, and <see cref="StampImage"/> exists
    /// precisely because PDFium will not hand them back out of a live
    /// annotation. Rune only holds them for stamps it placed this session, so a
    /// re-create would refuse to move a signature that was already in the file
    /// when it was opened.
    /// </summary>
    public (float L, float B, float R, float T)? MoveAnnotation(
        int pageIndex, int annotIndex, double x, double y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);
        ArgumentOutOfRangeException.ThrowIfNegative(annotIndex);

        (float, float, float, float) previous;
        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = AcquirePageLocked(pageIndex);
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
                    if (!PdfiumNative.GetAnnotRect(annot, out float ol, out float ot, out float or, out float ob))
                    {
                        return null;
                    }
                    // GetAnnotRect reports top/bottom by name, not by order.
                    float oldL = Math.Min(ol, or), oldR = Math.Max(ol, or);
                    float oldB = Math.Min(ot, ob), oldT = Math.Max(ot, ob);
                    previous = (oldL, oldB, oldR, oldT);

                    // Keep the size the appearance is actually drawn at.
                    var (newL, newB, newR, newT) =
                        ToPageRectLocked(page, pageIndex, x, y, oldR - oldL, oldT - oldB);
                    ApplyAnnotationRectLocked(annot, newL, newB, newR, newT);
                }
                finally
                {
                    PdfiumNative.CloseAnnot(annot);
                }

                // Without this the move is visible in the live document but is
                // not serialized, so it vanishes on save.
                PdfiumNative.GenerateContent(page);
            }
            finally
            {
                ReleasePageLocked(pageIndex);
            }
        }

        IsDirty = true;
        return previous;
    }

    /// <summary>
    /// Restores an annotation to a rect already expressed in PDF page space —
    /// the undo counterpart of <see cref="MoveAnnotation"/>, which hands its
    /// caller exactly this shape.
    /// </summary>
    public bool RestoreAnnotationRect(int pageIndex, int annotIndex, (float L, float B, float R, float T) rect)
    {
        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = AcquirePageLocked(pageIndex);
            if (page == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                IntPtr annot = PdfiumNative.GetAnnot(page, annotIndex);
                if (annot == IntPtr.Zero)
                {
                    return false;
                }
                try
                {
                    ApplyAnnotationRectLocked(annot, rect.L, rect.B, rect.R, rect.T);
                }
                finally
                {
                    PdfiumNative.CloseAnnot(annot);
                }
                PdfiumNative.GenerateContent(page);
            }
            finally
            {
                ReleasePageLocked(pageIndex);
            }
        }

        IsDirty = true;
        return true;
    }

    /// <summary>Page-local top-left points → a normalized PDF page-space rect.</summary>
    private (float L, float B, float R, float T) ToPageRectLocked(
        IntPtr page, int pageIndex, double x, double y, double widthPt, double heightPt)
    {
        var (ptW, ptH) = _pageSizes[pageIndex];
        int sizeX = Math.Max(1, (int)MathF.Round(ptW));
        int sizeY = Math.Max(1, (int)MathF.Round(ptH));

        var (left, top) = PdfiumNative.DeviceToPage(page, sizeX, sizeY, (int)Math.Round(x), (int)Math.Round(y));
        var (right, bottom) = PdfiumNative.DeviceToPage(
            page, sizeX, sizeY, (int)Math.Round(x + widthPt), (int)Math.Round(y + heightPt));

        return ((float)Math.Min(left, right), (float)Math.Min(top, bottom),
                (float)Math.Max(left, right), (float)Math.Max(top, bottom));
    }

    /// <summary>
    /// Repositions an annotation by setting its rect.
    ///
    /// PDFium draws a stamp's appearance stream translated to the annotation
    /// rect's origin, so setting the rect is all a move needs — and it works
    /// for any stamp, including one that was already in the file when it was
    /// opened, which is what makes this the move mechanism rather than
    /// delete-and-re-create (see <see cref="MoveAnnotation"/>).
    ///
    /// It does NOT scale the appearance to the rect. Measured on PDFium
    /// 152.0.7961: after setting a 100×40 stamp's rect to 300×120 the
    /// annotation reports 300×120 while the image still renders at 100×40.
    /// Transforming the annotation's image object with FPDFPageObj_Transform
    /// does not help either — the object changes, but the appearance stream it
    /// was parsed from is not regenerated, and PDFium exposes no call that
    /// forces regeneration. **Resizing therefore has to go through
    /// delete-and-re-create**, which needs the pixels — see
    /// <see cref="StampImage"/> for why those are only available for a stamp
    /// Rune placed in this session.
    ///
    /// Caller must hold <see cref="PdfiumLibrary.Lock"/> and a page lease.
    /// </summary>
    private static void ApplyAnnotationRectLocked(
        IntPtr annot, float newL, float newB, float newR, float newT)
        => PdfiumNative.SetAnnotRect(annot, newL, newB, newR, newT);

    /// <summary>
    /// Attaches an image object to a stamp annotation, scaled into the given
    /// page-space rect. Shared by <see cref="AddStamp"/> and the undo path in
    /// <see cref="AddAnnotationFromSpec"/> so a re-created stamp is built
    /// exactly the same way as the original.
    /// Caller must hold <see cref="PdfiumLibrary.Lock"/> and a page lease.
    /// </summary>
    private void AttachStampImageLocked(
        IntPtr page, IntPtr annot, StampImage stamp, float l, float b, float r, float t)
    {
        IntPtr image = PdfiumNative.NewImageObject(_handle);
        if (image == IntPtr.Zero)
        {
            throw new PdfiumException("Could not create the stamp image.", 1);
        }

        bool placed = false;
        try
        {
            if (!PdfiumNative.SetImageObjectBitmap(page, image, stamp.Bgra, stamp.Width, stamp.Height))
            {
                throw new PdfiumException("Could not attach the stamp image.", 1);
            }

            // A fresh image object is a 1x1 unit square at the origin, so the
            // matrix IS the placement: scale to the target size, then translate
            // to the target corner.
            PdfiumNative.TransformPageObject(image, r - l, 0, 0, t - b, l, b);

            PdfiumNative.AppendAnnotObject(annot, image);
            placed = true;
        }
        finally
        {
            if (!placed)
            {
                // Never inserted, so nothing else will free it.
                PdfiumNative.DestroyPageObject(image);
            }
        }
    }
}
