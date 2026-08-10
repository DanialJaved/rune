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

    // Read-back APIs (undo/redo captures annotations before deletion)

    /// <summary>colorType: 0 = fill/stroke color, 1 = interior color.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFAnnot_GetColor(IntPtr annot, int colorType, out uint r, out uint g, out uint b, out uint a);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern UIntPtr FPDFAnnot_CountAttachmentPoints(IntPtr annot);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFAnnot_GetAttachmentPoints(IntPtr annot, UIntPtr quadIndex, out FS_QUADPOINTSF quadPoints);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint FPDFAnnot_GetInkListCount(IntPtr annot);

    /// <summary>Returns the point count of the path; fills buffer when large enough (or pass null/0 to size).</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint FPDFAnnot_GetInkListPath(IntPtr annot, uint pathIndex, [Out] FS_POINTF[]? buffer, uint length);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFAnnot_GetBorder(IntPtr annot, out float horizontalRadius, out float verticalRadius, out float borderWidth);

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

    // ---- Interactive forms (fpdf_formfill.h) ----

    /// <summary>
    /// The host callbacks PDFium calls back into while driving form widgets.
    ///
    /// This is the **version 1** layout: `version` through `m_pJsPlatform`, and
    /// nothing after it. Version 2 appends XFA-only members, and the shipped
    /// build has no XFA — declaring version 2 against a non-XFA binary makes
    /// PDFium read past the end of this struct.
    ///
    /// Every member is an IntPtr rather than a delegate type so unused slots
    /// can be left as Zero. PDFium null-checks each one before calling it, so
    /// "not implemented" is a legitimate, supported choice — see the notes on
    /// the individual fields.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FPDF_FORMFILLINFO
    {
        public int Version;                  // must be 1 on a non-XFA build

        public IntPtr Release;
        public IntPtr FFI_Invalidate;        // our re-render signal — must be set
        public IntPtr FFI_OutputSelectedRect;
        public IntPtr FFI_SetCursor;

        // Deliberately left null. A caret needs a ~500ms repeating timer, and
        // every tick would re-rasterize the field's tiles. Rune's whole claim
        // is that it does not busy-render; a blinking caret is not worth it.
        public IntPtr FFI_SetTimer;
        public IntPtr FFI_KillTimer;

        // Deliberately left null. It returns FPDF_SYSTEMTIME (16 bytes) BY
        // VALUE, whose calling convention differs across ABIs — the most
        // likely source of a silent memory bug in the whole form feature.
        // PDFium only uses it for JS Date, which this non-V8 build cannot run.
        public IntPtr FFI_GetLocalTime;

        public IntPtr FFI_OnChange;          // field edited → mark document dirty
        public IntPtr FFI_GetPage;           // must be set: PDFium resolves indices through it
        public IntPtr FFI_GetCurrentPage;
        public IntPtr FFI_GetRotation;       // must be set: PDFium calls it unconditionally
        public IntPtr FFI_ExecuteNamedAction;
        public IntPtr FFI_SetTextFieldFocus;
        public IntPtr FFI_DoURIAction;
        public IntPtr FFI_DoGoToAction;

        public IntPtr m_pJsPlatform;         // IPDF_JSPLATFORM* — null, no V8
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void FfiInvalidateDelegate(IntPtr pThis, IntPtr page, double left, double top, double right, double bottom);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void FfiOnChangeDelegate(IntPtr pThis);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr FfiGetPageDelegate(IntPtr pThis, IntPtr document, int pageIndex);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr FfiGetCurrentPageDelegate(IntPtr pThis, IntPtr document);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int FfiGetRotationDelegate(IntPtr pThis, IntPtr page);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void FfiSetCursorDelegate(IntPtr pThis, int cursorType);

    /// <summary>
    /// PDFium stores the FPDF_FORMFILLINFO *pointer*, not a copy, and calls
    /// back through it for the environment's whole life. The struct therefore
    /// has to sit at a fixed address — pass unmanaged memory, never a managed
    /// struct the GC can relocate.
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr document, IntPtr formInfo);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDFDOC_ExitFormFillEnvironment(IntPtr formHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDF_GetFormType(IntPtr document);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FORM_OnAfterLoadPage(IntPtr page, IntPtr formHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FORM_OnBeforeClosePage(IntPtr page, IntPtr formHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FORM_DoDocumentOpenAction(IntPtr formHandle);

    // pageX/pageY are in PDF page space (bottom-left origin), not device pixels.
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FORM_OnLButtonDown(IntPtr formHandle, IntPtr page, int modifier, double pageX, double pageY);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FORM_OnLButtonUp(IntPtr formHandle, IntPtr page, int modifier, double pageX, double pageY);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FORM_OnMouseMove(IntPtr formHandle, IntPtr page, int modifier, double pageX, double pageY);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FORM_OnChar(IntPtr formHandle, IntPtr page, int charCode, int modifier);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FORM_OnKeyDown(IntPtr formHandle, IntPtr page, int keyCode, int modifier);

    /// <summary>Commits the focused field's edit. Must be called before saving.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FORM_ForceToKillFocus(IntPtr formHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FORM_SetIndexSelected(IntPtr formHandle, IntPtr page, int index, int selected);

    /// <summary>Draws form widgets over an already-rendered page bitmap.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDF_FFLDraw(
        IntPtr formHandle, IntPtr bitmap, IntPtr page,
        int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    /// <summary>fieldType 0 = all types. color is 0xRRGGBB.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDF_SetFormFieldHighlightColor(IntPtr formHandle, int fieldType, uint color);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDF_SetFormFieldHighlightAlpha(IntPtr formHandle, byte alpha);

    // ---- Form field queries (fpdf_annot.h, form-aware half) ----
    //
    // NOTE: there is no FPDFAnnot_SetFormFieldValue in PDFium. The only way to
    // change a field's value is to drive the form-fill event API (click, then
    // FORM_OnChar). Do not go looking for a programmatic setter.

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFAnnot_GetFormFieldAtPoint(IntPtr formHandle, IntPtr page, ref FS_POINTF point);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFAnnot_GetFormFieldType(IntPtr formHandle, IntPtr annot);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint FPDFAnnot_GetFormFieldName(IntPtr formHandle, IntPtr annot, [Out] byte[]? buffer, uint bufLen);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint FPDFAnnot_GetFormFieldValue(IntPtr formHandle, IntPtr annot, [Out] byte[]? buffer, uint bufLen);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFAnnot_GetFormFieldFlags(IntPtr formHandle, IntPtr annot);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFAnnot_GetOptionCount(IntPtr formHandle, IntPtr annot);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint FPDFAnnot_GetOptionLabel(IntPtr formHandle, IntPtr annot, int index, [Out] byte[]? buffer, uint bufLen);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFAnnot_IsChecked(IntPtr formHandle, IntPtr annot);

    // FPDF_GetFormType
    internal const int FORMTYPE_NONE = 0;
    internal const int FORMTYPE_ACRO_FORM = 1;
    internal const int FORMTYPE_XFA_FULL = 2;      // PDFium cannot fill these
    internal const int FORMTYPE_XFA_FOREGROUND = 3;

    // FPDFAnnot_GetFormFieldType
    internal const int FPDF_FORMFIELD_UNKNOWN = 0;
    internal const int FPDF_FORMFIELD_PUSHBUTTON = 1;
    internal const int FPDF_FORMFIELD_CHECKBOX = 2;
    internal const int FPDF_FORMFIELD_RADIOBUTTON = 3;
    internal const int FPDF_FORMFIELD_COMBOBOX = 4;
    internal const int FPDF_FORMFIELD_LISTBOX = 5;
    internal const int FPDF_FORMFIELD_TEXTFIELD = 6;
    internal const int FPDF_FORMFIELD_SIGNATURE = 7;

    // Field flags (FPDFAnnot_GetFormFieldFlags)
    internal const int FPDF_FORMFLAG_READONLY = 1 << 0;
    internal const int FPDF_FORMFLAG_REQUIRED = 1 << 1;

    // ---- Flatten (fpdf_flatten.h) ----

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFPage_Flatten(IntPtr page, int flag);

    internal const int FLAT_NORMALDISPLAY = 0;
    internal const int FLAT_PRINT = 1;

    internal const int FLATTEN_FAIL = 0;
    internal const int FLATTEN_SUCCESS = 1;
    internal const int FLATTEN_NOTHINGTODO = 2;

    // ---- Page objects & appearance streams (fpdf_edit.h) ----

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFPageObj_NewImageObj(IntPtr document);

    /// <summary>
    /// Takes an ARRAY of pages, not a single page — the image may be shared by
    /// several. Passing a bare page handle here compiles fine and corrupts memory.
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFImageObj_SetBitmap(IntPtr[] pages, int count, IntPtr imageObject, IntPtr bitmap);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDFPageObj_Transform(IntPtr pageObject, double a, double b, double c, double d, double e, double f);

    // ---- Text objects (fpdf_edit.h) ----
    //
    // Real text on a page, as opposed to a picture of text. The 14 standard
    // fonts need no embedding, which is why they are the ones Rune writes: the
    // file gains a few hundred bytes rather than a font program.

    /// <summary>
    /// One of the standard 14 by PostScript name — "Helvetica", "Helvetica-Bold",
    /// "Times-Roman", "Courier-Oblique" and so on. ASCII (FPDF_BYTESTRING), not
    /// UTF-16. Must be closed with <see cref="FPDFFont_Close"/>.
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFText_LoadStandardFont(
        IntPtr document, [MarshalAs(UnmanagedType.LPStr)] string font);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFPageObj_CreateTextObj(IntPtr document, IntPtr font, float fontSize);

    /// <summary>Sets the run's string. UTF-16LE (FPDF_WIDESTRING).</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFText_SetText(
        IntPtr textObject, [MarshalAs(UnmanagedType.LPWStr)] string text);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFPageObj_SetFillColor(
        IntPtr pageObject, uint r, uint g, uint b, uint a);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDFFont_Close(IntPtr font);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDFPage_InsertObject(IntPtr page, IntPtr pageObject);

    /// <summary>Required after any page-content edit, or the change is not serialized.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFPage_GenerateContent(IntPtr page);

    /// <summary>alpha: 1 for a transparent bitmap (needed for signature stamps).</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);

    /// <summary>Frees a page object that was never inserted into a page (failure paths).</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FPDFPageObj_Destroy(IntPtr pageObject);

    // ---- Reading an image back out (fpdf_edit.h) ----

    /// <summary>FPDF_PAGEOBJ_IMAGE.</summary>
    internal const int FPDF_PAGEOBJ_IMAGE = 3;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFPageObj_GetType(IntPtr pageObject);

    /// <summary>
    /// The image's own decoded bitmap, at its native pixel size and ignoring the
    /// object's matrix. Caller destroys it with FPDFBitmap_Destroy.
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFImageObj_GetBitmap(IntPtr imageObject);

    /// <summary>
    /// Like <see cref="FPDFImageObj_GetBitmap"/> but composited: transparency
    /// from an /SMask is applied, which a plain GetBitmap can drop. Needs the
    /// owning document and page.
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFImageObj_GetRenderedBitmap(IntPtr document, IntPtr page, IntPtr imageObject);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFBitmap_GetWidth(IntPtr bitmap);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFBitmap_GetHeight(IntPtr bitmap);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFBitmap_GetStride(IntPtr bitmap);

    /// <summary>1 Gray, 2 BGR, 3 BGRx, 4 BGRA.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFBitmap_GetFormat(IntPtr bitmap);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDFAnnot_GetObject(IntPtr annot, int index);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFAnnot_GetObjectCount(IntPtr annot);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFAnnot_AppendObject(IntPtr annot, IntPtr pageObject);

    /// <summary>value is UTF-16LE; pass null to clear the appearance stream.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDFAnnot_SetAP(
        IntPtr annot, int appearanceMode,
        [MarshalAs(UnmanagedType.LPWStr)] string? value);

    internal const int FPDF_ANNOT_APPEARANCEMODE_NORMAL = 0;
    internal const int FPDF_ANNOT_SUBTYPE_STAMP = 13;
    internal const int FPDF_ANNOT_SUBTYPE_WIDGET = 20;

    // ---- Digital signatures (fpdf_signature.h) ----
    //
    // Read-only reporting. PDFium hands back the raw PKCS#7 blob and byte
    // range; it does NOT validate the signature, the certificate chain, or
    // revocation. Nothing built on these may claim a signature is "valid".

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FPDF_GetSignatureCount(IntPtr document);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr FPDF_GetSignatureObject(IntPtr document, int index);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint FPDFSignatureObj_GetContents(IntPtr signature, [Out] byte[]? buffer, uint length);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint FPDFSignatureObj_GetByteRange(IntPtr signature, [Out] int[]? buffer, uint length);

    /// <summary>ASCII, unlike GetReason. Do not decode as UTF-16.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint FPDFSignatureObj_GetSubFilter(IntPtr signature, [Out] byte[]? buffer, uint length);

    /// <summary>UTF-16LE, unlike GetSubFilter and GetTime.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint FPDFSignatureObj_GetReason(IntPtr signature, [Out] byte[]? buffer, uint length);

    /// <summary>ASCII PDF date string, e.g. D:20260730120000+01'00'.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint FPDFSignatureObj_GetTime(IntPtr signature, [Out] byte[]? buffer, uint length);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint FPDFSignatureObj_GetDocMDPPermission(IntPtr signature);
}
