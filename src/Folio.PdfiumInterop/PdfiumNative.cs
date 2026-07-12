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

    /// <summary>
    /// Renders a full page into a caller-owned BGRA pixel buffer.
    /// The buffer must be at least stride * height bytes and pinned for the call.
    /// </summary>
    public static unsafe void RenderPageToBuffer(IntPtr page, byte[] pixels, int width, int height, int stride)
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
                NativeMethods.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, rotate: 0, flags: NativeMethods.FPDF_ANNOT);
            }
            finally
            {
                NativeMethods.FPDFBitmap_Destroy(bitmap);
            }
        }
    }
}
