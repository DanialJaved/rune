using System.Runtime.InteropServices;

namespace Rune.PdfiumInterop;

/// <summary>
/// Raw P/Invoke bindings over pdfium.dll. Signatures mirror fpdfview.h.
/// PDFium is NOT thread-safe: never call these directly — go through
/// <see cref="PdfiumLibrary.Lock"/> (see PdfDocument in Rune.Engine).
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

    // ---- Metadata (fpdf_doc.h) ----

    /// <summary>Writes the metadata value as UTF-16LE (incl. terminator); returns byte length needed. Tags: Title, Author, Subject, Keywords, Creator, Producer, CreationDate, ModDate.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint FPDF_GetMetaText(IntPtr document, [MarshalAs(UnmanagedType.LPStr)] string tag, byte[]? buffer, uint buflen);

    /// <summary>PDF version ×10 (e.g. 17 for 1.7). Returns 0 on failure.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDF_GetFileVersion(IntPtr document, out int fileVersion);

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

    // ---- Annotations (fpdf_annot.h) ----

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFPage_GetAnnotCount(IntPtr page);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFPage_RemoveAnnot(IntPtr page, int index);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDFPage_CloseAnnot(IntPtr annot);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFAnnot_GetSubtype(IntPtr annot);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFAnnot_SetRect(IntPtr annot, ref FS_RECTF rect);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFAnnot_GetRect(IntPtr annot, out FS_RECTF rect);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FS_QUADPOINTSF
    {
        public float X1, Y1;   // upper-left
        public float X2, Y2;   // upper-right
        public float X3, Y3;   // lower-left
        public float X4, Y4;   // lower-right
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFAnnot_AppendAttachmentPoints(IntPtr annot, ref FS_QUADPOINTSF quadPoints);

    /// <summary>colorType: 0 = fill/stroke color, 1 = interior color.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFAnnot_SetColor(IntPtr annot, int colorType, uint r, uint g, uint b, uint a);

    /// <summary>key is a narrow FPDF_BYTESTRING; value is UTF-16LE (FPDF_WIDESTRING).</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFAnnot_SetStringValue(
        IntPtr annot,
        [MarshalAs(UnmanagedType.LPStr)] string key,
        [MarshalAs(UnmanagedType.LPWStr)] string value);

    /// <summary>Returns byte length needed (UTF-16LE incl. terminator); fills buffer when large enough.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint FPDFAnnot_GetStringValue(
        IntPtr annot,
        [MarshalAs(UnmanagedType.LPStr)] string key,
        byte[]? buffer,
        uint buflen);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFAnnot_SetFlags(IntPtr annot, int flags);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FS_POINTF
    {
        public float X;
        public float Y;
    }

    /// <summary>Adds a freehand stroke (page-space points). Returns the stroke index, or -1.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFAnnot_AddInkStroke(IntPtr annot, [In] FS_POINTF[] points, UIntPtr pointCount);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFAnnot_SetBorder(IntPtr annot, float horizontalRadius, float verticalRadius, float borderWidth);

    // Annotation subtypes (fpdf_annot.h)
    internal const int FPDF_ANNOT_SUBTYPE_TEXT = 1;
    internal const int FPDF_ANNOT_SUBTYPE_HIGHLIGHT = 9;
    internal const int FPDF_ANNOT_SUBTYPE_INK = 15;
    internal const int FPDF_ANNOT_SUBTYPE_UNDERLINE = 10;
    internal const int FPDF_ANNOT_SUBTYPE_STRIKEOUT = 12;

    internal const int FPDF_ANNOT_FLAG_PRINT = 1 << 2;

    // ---- Page organization (fpdf_edit.h / fpdf_ppo.h) ----

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDFPage_Delete(IntPtr document, int pageIndex);

    /// <summary>Copies pages (by 0-based index array) from src into dest at destIndex. Pass null indices to copy all.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDF_ImportPagesByIndex(IntPtr destDoc, IntPtr srcDoc, [In] int[]? pageIndices, uint length, int destIndex);

    /// <summary>Experimental: moves pages so the block starts at destPageIndex in the final ordering.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDF_MovePages(IntPtr document, [In] int[] pageIndices, uint pageIndicesLen, int destPageIndex);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDF_CreateNewDocument();

    /// <summary>The buffer must stay valid (pinned) for the whole life of the returned document.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDF_LoadMemDocument64(IntPtr dataBuf, UIntPtr size, [MarshalAs(UnmanagedType.LPStr)] string? password);

    // ---- Saving (fpdf_save.h) ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct FPDF_FILEWRITE
    {
        public int Version;       // must be 1
        public IntPtr WriteBlock; // FPDF_BOOL (*)(FPDF_FILEWRITE*, const void*, unsigned long)
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int WriteBlockDelegate(IntPtr pThis, IntPtr data, uint size);

    internal const uint FPDF_SAVE_NO_INCREMENTAL = 2;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDF_SaveAsCopy(IntPtr document, ref FPDF_FILEWRITE fileWrite, uint flags);

    // ---- Text extraction & search (fpdf_text.h) ----

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFText_LoadPage(IntPtr page);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDFText_ClosePage(IntPtr textPage);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFText_CountChars(IntPtr textPage);

    /// <summary>Writes up to count chars (UTF-16LE, plus terminator) into result; returns chars written.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFText_GetText(IntPtr textPage, int startIndex, int count, [Out] ushort[] result);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFText_GetCharIndexAtPos(IntPtr textPage, double x, double y, double xTolerance, double yTolerance);

    /// <summary>Bounding box of one character in page space. NOTE the parameter order: left, right, bottom, top.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFText_GetCharBox(IntPtr textPage, int index, out double left, out double right, out double bottom, out double top);

    /// <summary>Number of distinct rectangles covering the given char range (multi-line selections span several).</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFText_CountRects(IntPtr textPage, int startIndex, int count);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFText_GetRect(IntPtr textPage, int rectIndex, out double left, out double top, out double right, out double bottom);

    // Search
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFText_FindStart(IntPtr textPage, ushort[] findWhat, uint flags, int startIndex);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFText_FindNext(IntPtr handle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFText_GetSchResultIndex(IntPtr handle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFText_GetSchCount(IntPtr handle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDFText_FindClose(IntPtr handle);

    /// <summary>Maps device (bitmap) pixels back to a page-space point, honoring rotation.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDF_DeviceToPage(
        IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate,
        int deviceX, int deviceY, out double pageX, out double pageY);

    // Search flags
    internal const uint FPDF_MATCHCASE = 0x00000001;
    internal const uint FPDF_MATCHWHOLEWORD = 0x00000002;

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
