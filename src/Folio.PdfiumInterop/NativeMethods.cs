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

    // ---- Outline / bookmarks (fpdf_doc.h) ----

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFBookmark_GetFirstChild(IntPtr document, IntPtr bookmark);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFBookmark_GetNextSibling(IntPtr document, IntPtr bookmark);

    /// <summary>Writes the title as UTF-16LE (incl. terminator) into buffer; returns byte length needed.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint FPDFBookmark_GetTitle(IntPtr bookmark, byte[]? buffer, uint buflen);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFBookmark_GetDest(IntPtr document, IntPtr bookmark);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFBookmark_GetAction(IntPtr bookmark);

    // ---- Actions & destinations ----

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint FPDFAction_GetType(IntPtr action);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);

    /// <summary>Writes the URI as ASCII (incl. terminator) into buffer; returns byte length needed.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint FPDFAction_GetURIPath(IntPtr document, IntPtr action, byte[]? buffer, uint buflength);

    /// <summary>Zero-based target page of a destination, or -1.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr dest);

    // ---- Links on a page (fpdf_doc.h) ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct FS_RECTF
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
    }

    /// <summary>Iterates link annotations. Pass startPos=0 initially; it is advanced each call.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFLink_Enumerate(IntPtr page, ref int startPos, out IntPtr linkAnnot);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFLink_GetAnnotRect(IntPtr linkAnnot, out FS_RECTF rect);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFLink_GetDest(IntPtr document, IntPtr link);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFLink_GetAction(IntPtr link);

    /// <summary>Maps a page-space point to device (bitmap) pixels, honoring rotation.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDF_PageToDevice(
        IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate,
        double pageX, double pageY, out int deviceX, out int deviceY);

    // ---- Constants ----

    // Action types (FPDFAction_GetType)
    internal const uint PDFACTION_UNSUPPORTED = 0;
    internal const uint PDFACTION_GOTO = 1;
    internal const uint PDFACTION_REMOTEGOTO = 2;
    internal const uint PDFACTION_URI = 3;
    internal const uint PDFACTION_LAUNCH = 4;


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
