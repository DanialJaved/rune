using System.Runtime.InteropServices;

namespace Folio.PdfiumInterop;

/// <summary>
/// Thin public facade over <see cref="NativeMethods"/> so that Folio.Engine
/// never touches raw DllImports. Every method asserts nothing about threading:
/// callers must hold <see cref="PdfiumLibrary.Lock"/>.
/// </summary>
public static class PdfiumNative
{
    public static IntPtr LoadCustomDocument(FileAccessAdapter fileAccess, string? password)
        => NativeMethods.FPDF_LoadCustomDocument(fileAccess.NativePointer, password);

    public static void CloseDocument(IntPtr document) => NativeMethods.FPDF_CloseDocument(document);

    public static PdfiumException LastError() => PdfiumException.FromLastError();

    public static bool LastErrorIsPassword() => NativeMethods.FPDF_GetLastError() == NativeMethods.FPDF_ERR_PASSWORD;

    public static int GetPageCount(IntPtr document) => NativeMethods.FPDF_GetPageCount(document);

    public static IntPtr LoadPage(IntPtr document, int pageIndex) => NativeMethods.FPDF_LoadPage(document, pageIndex);

    public static void ClosePage(IntPtr page) => NativeMethods.FPDF_ClosePage(page);

    public static float GetPageWidth(IntPtr page) => NativeMethods.FPDF_GetPageWidthF(page);

    public static float GetPageHeight(IntPtr page) => NativeMethods.FPDF_GetPageHeightF(page);

    /// <summary>Page size in points without loading the page. Returns false for a broken page entry.</summary>
    public static bool TryGetPageSize(IntPtr document, int pageIndex, out float width, out float height)
    {
        if (NativeMethods.FPDF_GetPageSizeByIndexF(document, pageIndex, out var size) != 0)
        {
            width = size.Width;
            height = size.Height;
            return true;
        }
        width = 0;
        height = 0;
        return false;
    }

    /// <summary>
    /// Renders a region of a page into a caller-owned BGRA pixel buffer.
    /// The page is laid out at (fullWidth × fullHeight) pixels after rotation,
    /// and the (srcX, srcY, width, height) window of that layout is written to
    /// the buffer — this is how tiles are rendered (negative start offsets).
    /// </summary>
    public static unsafe void RenderRegionToBuffer(
        IntPtr page, byte[] pixels,
        int srcX, int srcY, int width, int height,
        int fullWidth, int fullHeight, int rotation, int stride)
    {
        fixed (byte* p = pixels)
        {
            IntPtr bitmap = NativeMethods.FPDFBitmap_CreateEx(width, height, NativeMethods.FPDFBitmap_BGRA, (IntPtr)p, stride);
            if (bitmap == IntPtr.Zero)
            {
                throw new PdfiumException("Failed to create render bitmap.", NativeMethods.FPDF_ERR_UNKNOWN);
            }

            try
            {
                // Opaque white page background, then the page content on top.
                NativeMethods.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                NativeMethods.FPDF_RenderPageBitmap(bitmap, page, -srcX, -srcY, fullWidth, fullHeight, rotation, NativeMethods.FPDF_ANNOT);
            }
            finally
            {
                NativeMethods.FPDFBitmap_Destroy(bitmap);
            }
        }
    }
}
