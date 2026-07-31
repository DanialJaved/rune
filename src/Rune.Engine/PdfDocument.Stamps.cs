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
                var (ptW, ptH) = _pageSizes[pageIndex];
                int sizeX = Math.Max(1, (int)MathF.Round(ptW));
                int sizeY = Math.Max(1, (int)MathF.Round(ptH));

                // Top-left page-local points → PDF page space (bottom-left).
                var (left, top) = PdfiumNative.DeviceToPage(page, sizeX, sizeY, (int)Math.Round(x), (int)Math.Round(y));
                var (right, bottom) = PdfiumNative.DeviceToPage(
                    page, sizeX, sizeY, (int)Math.Round(x + widthPt), (int)Math.Round(y + heightPt));

                float l = (float)Math.Min(left, right);
                float r = (float)Math.Max(left, right);
                float b = (float)Math.Min(top, bottom);
                float t = (float)Math.Max(top, bottom);

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
