using System.Runtime.InteropServices;

namespace Folio.PdfiumInterop;

/// <summary>
/// Raw P/Invoke bindings over pdfium.dll. Signatures mirror fpdfview.h.
/// PDFium is NOT thread-safe: never call these directly — go through
/// <see cref="PdfiumLibrary.Lock"/> (see PdfDocument in Folio.Engine).
/// </summary>
internal static partial class NativeMethods
{
    private const string Dll = "pdfium";

    // ---- Library lifetime ----

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDF_InitLibrary();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDF_DestroyLibrary();

    // ---- Document ----

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDF_LoadCustomDocument(IntPtr fileAccess, [MarshalAs(UnmanagedType.LPStr)] string? password);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint FPDF_GetLastError();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDF_GetPageCount(IntPtr document);

    // ---- Page ----

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDF_ClosePage(IntPtr page);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern float FPDF_GetPageWidthF(IntPtr page);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern float FPDF_GetPageHeightF(IntPtr page);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FS_SIZEF
    {
        public float Width;
        public float Height;
    }

    /// <summary>Reads a page's size from the page tree WITHOUT loading the page — much cheaper than FPDF_LoadPage.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDF_GetPageSizeByIndexF(IntPtr document, int pageIndex, out FS_SIZEF size);

    // ---- Bitmap / rendering ----

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    // ---- Constants ----

    internal const int FPDFBitmap_BGRA = 4;

    internal const int FPDF_ANNOT = 0x01;      // render annotations
    internal const int FPDF_LCD_TEXT = 0x02;   // subpixel text (LCD)

    // FPDF_GetLastError codes
    internal const uint FPDF_ERR_SUCCESS = 0;
    internal const uint FPDF_ERR_UNKNOWN = 1;
    internal const uint FPDF_ERR_FILE = 2;
    internal const uint FPDF_ERR_FORMAT = 3;
    internal const uint FPDF_ERR_PASSWORD = 4;
    internal const uint FPDF_ERR_SECURITY = 5;
    internal const uint FPDF_ERR_PAGE = 6;
}
