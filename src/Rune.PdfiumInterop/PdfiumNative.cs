using System.Runtime.InteropServices;

namespace Rune.PdfiumInterop;

/// <summary>
/// Thin public facade over <see cref="NativeMethods"/> so that Rune.Engine
/// never touches raw DllImports. Every method asserts nothing about threading:
/// callers must hold <see cref="PdfiumLibrary.Lock"/>.
/// </summary>
public static class PdfiumNative
{
    public static IntPtr LoadCustomDocument(FileAccessAdapter fileAccess, string? password)
        => NativeMethods.FPDF_LoadCustomDocument(fileAccess.NativePointer, password);

    // ---- Annotations ----

    public const int AnnotText = NativeMethods.FPDF_ANNOT_SUBTYPE_TEXT;
    public const int AnnotHighlight = NativeMethods.FPDF_ANNOT_SUBTYPE_HIGHLIGHT;
    public const int AnnotUnderline = NativeMethods.FPDF_ANNOT_SUBTYPE_UNDERLINE;
    public const int AnnotStrikeout = NativeMethods.FPDF_ANNOT_SUBTYPE_STRIKEOUT;
    public const int AnnotInk = NativeMethods.FPDF_ANNOT_SUBTYPE_INK;
    public const int AnnotStamp = NativeMethods.FPDF_ANNOT_SUBTYPE_STAMP;
    public const int AnnotWidget = NativeMethods.FPDF_ANNOT_SUBTYPE_WIDGET;

    public const int FormFlagReadOnly = NativeMethods.FPDF_FORMFLAG_READONLY;
    public const int FormFlagRequired = NativeMethods.FPDF_FORMFLAG_REQUIRED;

    /// <summary>Adds one freehand stroke to an ink annotation. Points are in PDF page space (bottom-left origin).</summary>
    public static bool AddInkStroke(IntPtr annot, (float X, float Y)[] points)
    {
        if (points.Length == 0)
        {
            return false;
        }
        var native = new NativeMethods.FS_POINTF[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            native[i] = new NativeMethods.FS_POINTF { X = points[i].X, Y = points[i].Y };
        }
        return NativeMethods.FPDFAnnot_AddInkStroke(annot, native, (UIntPtr)native.Length) >= 0;
    }

    public static bool SetAnnotBorderWidth(IntPtr annot, float width)
        => NativeMethods.FPDFAnnot_SetBorder(annot, 0, 0, width) != 0;

    // ---- Annotation read-back (undo/redo capture) ----

    public static bool GetAnnotColor(IntPtr annot, out byte r, out byte g, out byte b, out byte a)
    {
        bool ok = NativeMethods.FPDFAnnot_GetColor(annot, 0, out uint ur, out uint ug, out uint ub, out uint ua) != 0;
        (r, g, b, a) = ((byte)ur, (byte)ug, (byte)ub, (byte)ua);
        return ok;
    }

    public static int CountAnnotQuads(IntPtr annot)
        => (int)NativeMethods.FPDFAnnot_CountAttachmentPoints(annot);

    /// <summary>One markup quad in page space: (left, bottom, right, top).</summary>
    public static bool GetAnnotQuad(IntPtr annot, int quadIndex, out float left, out float bottom, out float right, out float top)
    {
        if (NativeMethods.FPDFAnnot_GetAttachmentPoints(annot, (UIntPtr)quadIndex, out var q) != 0)
        {
            left = Math.Min(Math.Min(q.X1, q.X2), Math.Min(q.X3, q.X4));
            right = Math.Max(Math.Max(q.X1, q.X2), Math.Max(q.X3, q.X4));
            bottom = Math.Min(Math.Min(q.Y1, q.Y2), Math.Min(q.Y3, q.Y4));
            top = Math.Max(Math.Max(q.Y1, q.Y2), Math.Max(q.Y3, q.Y4));
            return true;
        }
        left = bottom = right = top = 0;
        return false;
    }

    public static int GetInkStrokeCount(IntPtr annot)
        => (int)NativeMethods.FPDFAnnot_GetInkListCount(annot);

    /// <summary>Points of one ink stroke, in page space. Empty on failure.</summary>
    public static (float X, float Y)[] GetInkStroke(IntPtr annot, int strokeIndex)
    {
        uint count = NativeMethods.FPDFAnnot_GetInkListPath(annot, (uint)strokeIndex, null, 0);
        if (count == 0)
        {
            return [];
        }
        var buffer = new NativeMethods.FS_POINTF[count];
        NativeMethods.FPDFAnnot_GetInkListPath(annot, (uint)strokeIndex, buffer, count);
        var points = new (float, float)[count];
        for (int i = 0; i < count; i++)
        {
            points[i] = (buffer[i].X, buffer[i].Y);
        }
        return points;
    }

    public static float GetAnnotBorderWidth(IntPtr annot)
        => NativeMethods.FPDFAnnot_GetBorder(annot, out _, out _, out float width) != 0 ? width : 0f;

    public static IntPtr CreateAnnot(IntPtr page, int subtype) => NativeMethods.FPDFPage_CreateAnnot(page, subtype);

    public static int GetAnnotCount(IntPtr page) => NativeMethods.FPDFPage_GetAnnotCount(page);

    public static IntPtr GetAnnot(IntPtr page, int index) => NativeMethods.FPDFPage_GetAnnot(page, index);

    public static bool RemoveAnnot(IntPtr page, int index) => NativeMethods.FPDFPage_RemoveAnnot(page, index) != 0;

    public static void CloseAnnot(IntPtr annot) => NativeMethods.FPDFPage_CloseAnnot(annot);

    public static int GetAnnotSubtype(IntPtr annot) => NativeMethods.FPDFAnnot_GetSubtype(annot);

    /// <summary>Rect in PDF page coordinates (bottom-left origin).</summary>
    public static bool SetAnnotRect(IntPtr annot, float left, float bottom, float right, float top)
    {
        var rect = new NativeMethods.FS_RECTF { Left = left, Bottom = bottom, Right = right, Top = top };
        return NativeMethods.FPDFAnnot_SetRect(annot, ref rect) != 0;
    }

    public static bool GetAnnotRect(IntPtr annot, out float left, out float top, out float right, out float bottom)
    {
        if (NativeMethods.FPDFAnnot_GetRect(annot, out var rect) != 0)
        {
            left = rect.Left;
            top = rect.Top;
            right = rect.Right;
            bottom = rect.Bottom;
            return true;
        }
        left = top = right = bottom = 0;
        return false;
    }

    /// <summary>Adds one markup quad (PDF page coords): corners UL, UR, LL, LR.</summary>
    public static bool AppendQuad(IntPtr annot, float left, float bottom, float right, float top)
    {
        var quad = new NativeMethods.FS_QUADPOINTSF
        {
            X1 = left,  Y1 = top,
            X2 = right, Y2 = top,
            X3 = left,  Y3 = bottom,
            X4 = right, Y4 = bottom,
        };
        return NativeMethods.FPDFAnnot_AppendAttachmentPoints(annot, ref quad) != 0;
    }

    public static bool SetAnnotColor(IntPtr annot, byte r, byte g, byte b, byte a)
        => NativeMethods.FPDFAnnot_SetColor(annot, 0, r, g, b, a) != 0;

    public static bool SetAnnotString(IntPtr annot, string key, string value)
        => NativeMethods.FPDFAnnot_SetStringValue(annot, key, value) != 0;

    public static string GetAnnotString(IntPtr annot, string key)
    {
        uint bytes = NativeMethods.FPDFAnnot_GetStringValue(annot, key, null, 0);
        return ReadUtf16(bytes, buf => NativeMethods.FPDFAnnot_GetStringValue(annot, key, buf, (uint)buf.Length));
    }

    public static void SetAnnotPrintFlag(IntPtr annot)
        => NativeMethods.FPDFAnnot_SetFlags(annot, NativeMethods.FPDF_ANNOT_FLAG_PRINT);

    // ---- Page organization ----

    public static void DeletePage(IntPtr document, int pageIndex)
        => NativeMethods.FPDFPage_Delete(document, pageIndex);

    /// <summary>Copies the given pages of src into dest at destIndex (all pages when indices is null).</summary>
    public static bool ImportPagesByIndex(IntPtr destDoc, IntPtr srcDoc, int[]? pageIndices, int destIndex)
        => NativeMethods.FPDF_ImportPagesByIndex(
            destDoc, srcDoc, pageIndices, (uint)(pageIndices?.Length ?? 0), destIndex) != 0;

    /// <summary>
    /// Moves pages so the block starts at destIndex in the final ordering.
    /// Throws EntryPointNotFoundException on pdfium builds without the
    /// experimental export — callers fall back to export+delete+import.
    /// </summary>
    public static bool MovePages(IntPtr document, int[] pageIndices, int destIndex)
        => NativeMethods.FPDF_MovePages(document, pageIndices, (uint)pageIndices.Length, destIndex) != 0;

    public static IntPtr CreateNewDocument() => NativeMethods.FPDF_CreateNewDocument();

    /// <summary>Opens a document over a caller-pinned buffer (pin for the document's whole life).</summary>
    public static IntPtr LoadMemDocument(IntPtr pinnedData, long size, string? password)
        => NativeMethods.FPDF_LoadMemDocument64(pinnedData, (UIntPtr)size, password);

    // ---- Saving ----

    /// <summary>Writes a full (non-incremental) copy of the document to the stream.</summary>
    public static bool SaveCopy(IntPtr document, Stream output)
    {
        int WriteBlock(IntPtr pThis, IntPtr data, uint size)
        {
            try
            {
                if (size > 0)
                {
                    var buffer = new byte[size];
                    Marshal.Copy(data, buffer, 0, (int)size);
                    output.Write(buffer, 0, (int)size);
                }
                return 1;
            }
            catch
            {
                return 0; // never let an exception cross the native boundary
            }
        }

        // Delegate + struct only need to live for the duration of this call —
        // FPDF_SaveAsCopy is synchronous.
        NativeMethods.WriteBlockDelegate callback = WriteBlock;
        var fileWrite = new NativeMethods.FPDF_FILEWRITE
        {
            Version = 1,
            WriteBlock = Marshal.GetFunctionPointerForDelegate(callback),
        };
        bool ok = NativeMethods.FPDF_SaveAsCopy(document, ref fileWrite, NativeMethods.FPDF_SAVE_NO_INCREMENTAL) != 0;
        GC.KeepAlive(callback);
        return ok;
    }

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
    ///
    /// <paramref name="formHandle"/>, when non-zero, adds a second pass that
    /// draws interactive form widgets over the page. This is the single choke
    /// point every consumer funnels through — viewer tiles, sidebar thumbnails,
    /// the homepage cache, presentation mode and print — so form fields either
    /// appear everywhere or nowhere.
    /// </summary>
    public static unsafe void RenderRegionToBuffer(
        IntPtr page, byte[] pixels,
        int srcX, int srcY, int width, int height,
        int fullWidth, int fullHeight, int rotation, int stride,
        IntPtr formHandle = default)
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

                // Widgets are drawn by the form environment, not by the page
                // render: FPDF_ANNOT only paints appearance streams the file
                // already carries, so a freshly typed value would be invisible.
                if (formHandle != IntPtr.Zero)
                {
                    NativeMethods.FPDF_FFLDraw(formHandle, bitmap, page, -srcX, -srcY, fullWidth, fullHeight, rotation, NativeMethods.FPDF_ANNOT);
                }
            }
            finally
            {
                NativeMethods.FPDFBitmap_Destroy(bitmap);
            }
        }
    }

    // ---- Metadata ----

    public static string GetMetaText(IntPtr document, string tag)
    {
        uint bytes = NativeMethods.FPDF_GetMetaText(document, tag, null, 0);
        return ReadUtf16(bytes, buf => NativeMethods.FPDF_GetMetaText(document, tag, buf, (uint)buf.Length));
    }

    /// <summary>PDF version ×10 (17 = PDF 1.7), or 0 if unknown.</summary>
    public static int GetFileVersion(IntPtr document)
        => NativeMethods.FPDF_GetFileVersion(document, out int version) != 0 ? version : 0;

    // ---- Outline / bookmarks ----

    public static IntPtr BookmarkGetFirstChild(IntPtr document, IntPtr bookmark)
        => NativeMethods.FPDFBookmark_GetFirstChild(document, bookmark);

    public static IntPtr BookmarkGetNextSibling(IntPtr document, IntPtr bookmark)
        => NativeMethods.FPDFBookmark_GetNextSibling(document, bookmark);

    public static string BookmarkGetTitle(IntPtr bookmark)
    {
        uint bytes = NativeMethods.FPDFBookmark_GetTitle(bookmark, null, 0);
        return ReadUtf16(bytes, buf => NativeMethods.FPDFBookmark_GetTitle(bookmark, buf, (uint)buf.Length));
    }

    public static IntPtr BookmarkGetAction(IntPtr bookmark) => NativeMethods.FPDFBookmark_GetAction(bookmark);

    public static IntPtr BookmarkGetDest(IntPtr document, IntPtr bookmark)
        => NativeMethods.FPDFBookmark_GetDest(document, bookmark);

    // ---- Actions & destinations ----

    public static uint ActionGetType(IntPtr action) => NativeMethods.FPDFAction_GetType(action);

    public static IntPtr ActionGetDest(IntPtr document, IntPtr action) => NativeMethods.FPDFAction_GetDest(document, action);

    public static string ActionGetUri(IntPtr document, IntPtr action)
    {
        uint bytes = NativeMethods.FPDFAction_GetURIPath(document, action, null, 0);
        if (bytes <= 1)
        {
            return string.Empty;
        }
        var buffer = new byte[bytes];
        NativeMethods.FPDFAction_GetURIPath(document, action, buffer, bytes);
        // ASCII bytes, minus the trailing NUL.
        return System.Text.Encoding.ASCII.GetString(buffer, 0, (int)bytes - 1);
    }

    public static int DestGetPageIndex(IntPtr document, IntPtr dest) => NativeMethods.FPDFDest_GetDestPageIndex(document, dest);

    public const uint ActionGoto = NativeMethods.PDFACTION_GOTO;
    public const uint ActionUri = NativeMethods.PDFACTION_URI;

    // ---- Links ----

    /// <summary>Enumerates link annotations on a loaded page. Returns false when exhausted.</summary>
    public static bool LinkEnumerate(IntPtr page, ref int startPos, out IntPtr linkAnnot)
        => NativeMethods.FPDFLink_Enumerate(page, ref startPos, out linkAnnot) != 0;

    public static bool LinkGetRect(IntPtr linkAnnot, out float left, out float top, out float right, out float bottom)
    {
        if (NativeMethods.FPDFLink_GetAnnotRect(linkAnnot, out var rect) != 0)
        {
            left = rect.Left;
            top = rect.Top;
            right = rect.Right;
            bottom = rect.Bottom;
            return true;
        }
        left = top = right = bottom = 0;
        return false;
    }

    public static IntPtr LinkGetDest(IntPtr document, IntPtr link) => NativeMethods.FPDFLink_GetDest(document, link);

    public static IntPtr LinkGetAction(IntPtr link) => NativeMethods.FPDFLink_GetAction(link);

    /// <summary>Maps a page-space point to pixels within a (sizeX × sizeY) render at the given rotation.</summary>
    public static (int X, int Y) PageToDevice(IntPtr page, int sizeX, int sizeY, int rotation, double pageX, double pageY)
    {
        NativeMethods.FPDF_PageToDevice(page, 0, 0, sizeX, sizeY, rotation, pageX, pageY, out int dx, out int dy);
        return (dx, dy);
    }

    // ---- Text extraction & search ----

    public static IntPtr TextLoadPage(IntPtr page) => NativeMethods.FPDFText_LoadPage(page);
    public static void TextClosePage(IntPtr textPage) => NativeMethods.FPDFText_ClosePage(textPage);
    public static int TextCountChars(IntPtr textPage) => NativeMethods.FPDFText_CountChars(textPage);

    /// <summary>Extracts a run of characters as a string.</summary>
    public static string TextGetText(IntPtr textPage, int startIndex, int count)
    {
        if (count <= 0)
        {
            return string.Empty;
        }
        var buffer = new ushort[count + 1]; // room for the NUL terminator
        int written = NativeMethods.FPDFText_GetText(textPage, startIndex, count, buffer);
        int chars = Math.Max(0, Math.Min(written - 1, count)); // drop terminator
        if (chars == 0)
        {
            return string.Empty;
        }
        return new string(MemoryMarshal.Cast<ushort, char>(buffer.AsSpan(0, chars)));
    }

    public static int TextCharIndexAtPos(IntPtr textPage, double x, double y, double tolerance)
        => NativeMethods.FPDFText_GetCharIndexAtPos(textPage, x, y, tolerance, tolerance);

    /// <summary>Bounding box of one character, in page space (bottom-left origin). False for chars with no box.</summary>
    public static bool TextGetCharBox(IntPtr textPage, int index, out double left, out double right, out double bottom, out double top)
        => NativeMethods.FPDFText_GetCharBox(textPage, index, out left, out right, out bottom, out top) != 0;

    public static int TextCountRects(IntPtr textPage, int startIndex, int count)
        => NativeMethods.FPDFText_CountRects(textPage, startIndex, count);

    public static bool TextGetRect(IntPtr textPage, int rectIndex, out double left, out double top, out double right, out double bottom)
        => NativeMethods.FPDFText_GetRect(textPage, rectIndex, out left, out top, out right, out bottom) != 0;

    public static IntPtr TextFindStart(IntPtr textPage, string query, bool matchCase, bool wholeWord, int startIndex)
    {
        uint flags = 0;
        if (matchCase) flags |= NativeMethods.FPDF_MATCHCASE;
        if (wholeWord) flags |= NativeMethods.FPDF_MATCHWHOLEWORD;

        // UTF-16LE, NUL-terminated.
        var chars = query.ToCharArray();
        var findWhat = new ushort[chars.Length + 1];
        for (int i = 0; i < chars.Length; i++)
        {
            findWhat[i] = chars[i];
        }
        return NativeMethods.FPDFText_FindStart(textPage, findWhat, flags, startIndex);
    }

    public static bool TextFindNext(IntPtr handle) => NativeMethods.FPDFText_FindNext(handle) != 0;
    public static int TextSchResultIndex(IntPtr handle) => NativeMethods.FPDFText_GetSchResultIndex(handle);
    public static int TextSchCount(IntPtr handle) => NativeMethods.FPDFText_GetSchCount(handle);
    public static void TextFindClose(IntPtr handle) => NativeMethods.FPDFText_FindClose(handle);

    /// <summary>
    /// Maps a top-left-origin page-point (1 unit = 1 pt) to page space
    /// (bottom-left origin) — the inverse of <see cref="PageToDevice"/>.
    ///
    /// <paramref name="rotation"/> is quarter-turns clockwise and must match
    /// the one used on the way in, or the result is off by a whole page
    /// dimension. Note that <paramref name="sizeX"/>/<paramref name="sizeY"/>
    /// are the dimensions of the *rotated* surface, so callers passing a
    /// rotation of 1 or 3 must swap them.
    /// </summary>
    public static (double X, double Y) DeviceToPage(IntPtr page, int sizeX, int sizeY, int deviceX, int deviceY, int rotation = 0)
    {
        NativeMethods.FPDF_DeviceToPage(page, 0, 0, sizeX, sizeY, rotation, deviceX, deviceY, out double px, out double py);
        return (px, py);
    }

    // ---- Interactive forms ----

    // The form-fill environment itself is owned by PdfiumFormEnvironment, which
    // handles the struct and delegate lifetimes PDFium requires.

    public static int GetFormType(IntPtr document) => NativeMethods.FPDF_GetFormType(document);

    public static void FormOnAfterLoadPage(IntPtr page, IntPtr formHandle) => NativeMethods.FORM_OnAfterLoadPage(page, formHandle);

    public static void FormOnBeforeClosePage(IntPtr page, IntPtr formHandle) => NativeMethods.FORM_OnBeforeClosePage(page, formHandle);

    public static void FormDoDocumentOpenAction(IntPtr formHandle) => NativeMethods.FORM_DoDocumentOpenAction(formHandle);

    public static bool FormOnLButtonDown(IntPtr formHandle, IntPtr page, double pageX, double pageY, int modifier = 0)
        => NativeMethods.FORM_OnLButtonDown(formHandle, page, modifier, pageX, pageY) != 0;

    public static bool FormOnLButtonUp(IntPtr formHandle, IntPtr page, double pageX, double pageY, int modifier = 0)
        => NativeMethods.FORM_OnLButtonUp(formHandle, page, modifier, pageX, pageY) != 0;

    public static bool FormOnMouseMove(IntPtr formHandle, IntPtr page, double pageX, double pageY, int modifier = 0)
        => NativeMethods.FORM_OnMouseMove(formHandle, page, modifier, pageX, pageY) != 0;

    public static bool FormOnChar(IntPtr formHandle, IntPtr page, int charCode, int modifier = 0)
        => NativeMethods.FORM_OnChar(formHandle, page, charCode, modifier) != 0;

    public static bool FormOnKeyDown(IntPtr formHandle, IntPtr page, int keyCode, int modifier = 0)
        => NativeMethods.FORM_OnKeyDown(formHandle, page, keyCode, modifier) != 0;

    /// <summary>Commits the focused field's pending edit. Required before saving.</summary>
    public static bool FormKillFocus(IntPtr formHandle) => NativeMethods.FORM_ForceToKillFocus(formHandle) != 0;

    public static bool FormSetIndexSelected(IntPtr formHandle, IntPtr page, int index, bool selected)
        => NativeMethods.FORM_SetIndexSelected(formHandle, page, index, selected ? 1 : 0) != 0;

    public static void SetFormFieldHighlight(IntPtr formHandle, uint rgb, byte alpha)
    {
        NativeMethods.FPDF_SetFormFieldHighlightColor(formHandle, 0, rgb);
        NativeMethods.FPDF_SetFormFieldHighlightAlpha(formHandle, alpha);
    }

    /// <summary>The widget annotation at a page-space point, or Zero. Caller must CloseAnnot it.</summary>
    public static IntPtr GetFormFieldAtPoint(IntPtr formHandle, IntPtr page, float pageX, float pageY)
    {
        var point = new NativeMethods.FS_POINTF { X = pageX, Y = pageY };
        return NativeMethods.FPDFAnnot_GetFormFieldAtPoint(formHandle, page, ref point);
    }

    public static int GetFormFieldType(IntPtr formHandle, IntPtr annot) => NativeMethods.FPDFAnnot_GetFormFieldType(formHandle, annot);

    public static int GetFormFieldFlags(IntPtr formHandle, IntPtr annot) => NativeMethods.FPDFAnnot_GetFormFieldFlags(formHandle, annot);

    public static bool IsFormFieldChecked(IntPtr formHandle, IntPtr annot) => NativeMethods.FPDFAnnot_IsChecked(formHandle, annot) != 0;

    public static string GetFormFieldName(IntPtr formHandle, IntPtr annot)
        => ReadUtf16(NativeMethods.FPDFAnnot_GetFormFieldName(formHandle, annot, null, 0),
                     buffer => NativeMethods.FPDFAnnot_GetFormFieldName(formHandle, annot, buffer, (uint)buffer.Length));

    public static string GetFormFieldValue(IntPtr formHandle, IntPtr annot)
        => ReadUtf16(NativeMethods.FPDFAnnot_GetFormFieldValue(formHandle, annot, null, 0),
                     buffer => NativeMethods.FPDFAnnot_GetFormFieldValue(formHandle, annot, buffer, (uint)buffer.Length));

    public static int GetFormOptionCount(IntPtr formHandle, IntPtr annot) => NativeMethods.FPDFAnnot_GetOptionCount(formHandle, annot);

    public static string GetFormOptionLabel(IntPtr formHandle, IntPtr annot, int index)
        => ReadUtf16(NativeMethods.FPDFAnnot_GetOptionLabel(formHandle, annot, index, null, 0),
                     buffer => NativeMethods.FPDFAnnot_GetOptionLabel(formHandle, annot, index, buffer, (uint)buffer.Length));

    // ---- Image stamping (fpdf_edit.h) ----

    public static IntPtr NewImageObject(IntPtr document) => NativeMethods.FPDFPageObj_NewImageObj(document);

    public static void DestroyPageObject(IntPtr pageObject) => NativeMethods.FPDFPageObj_Destroy(pageObject);

    /// <summary>An annotation's appearance objects.</summary>
    public static int GetAnnotObjectCount(IntPtr annot) => NativeMethods.FPDFAnnot_GetObjectCount(annot);

    public static IntPtr GetAnnotObject(IntPtr annot, int index) => NativeMethods.FPDFAnnot_GetObject(annot, index);

    public static bool IsImageObject(IntPtr pageObject)
        => NativeMethods.FPDFPageObj_GetType(pageObject) == NativeMethods.FPDF_PAGEOBJ_IMAGE;

    public static bool IsTextObject(IntPtr pageObject)
        => NativeMethods.FPDFPageObj_GetType(pageObject) == NativeMethods.FPDF_PAGEOBJ_TEXT;

    /// <summary>The size a text run was created at, or null when it cannot be read.</summary>
    public static float? GetTextObjectFontSize(IntPtr textObject)
        => NativeMethods.FPDFTextObj_GetFontSize(textObject, out float size) != 0 ? size : null;

    /// <summary>
    /// A text run's /BaseFont name, e.g. "Helvetica-BoldOblique", or null.
    ///
    /// The returned font handle is the document's, not a loaned one, so it is
    /// deliberately never closed here — closing it would free a font the page
    /// still draws with.
    /// </summary>
    public static string? GetTextObjectFontName(IntPtr textObject)
    {
        IntPtr font = NativeMethods.FPDFTextObj_GetFont(textObject);
        if (font == IntPtr.Zero)
        {
            return null;
        }

        uint length = NativeMethods.FPDFFont_GetBaseFontName(font, null, 0);
        if (length <= 1)
        {
            return null; // 0 is failure, 1 is the terminator alone
        }

        var buffer = new byte[length];
        if (NativeMethods.FPDFFont_GetBaseFontName(font, buffer, length) != length)
        {
            return null;
        }
        // ASCII, and the length PDFium reports includes the NUL.
        return System.Text.Encoding.ASCII.GetString(buffer, 0, (int)length - 1);
    }

    /// <summary>An object's fill colour, or null when it paints with none.</summary>
    public static (byte R, byte G, byte B, byte A)? GetObjectFillColor(IntPtr pageObject)
        => NativeMethods.FPDFPageObj_GetFillColor(pageObject, out uint r, out uint g, out uint b, out uint a) != 0
            ? ((byte)r, (byte)g, (byte)b, (byte)a)
            : null;

    /// <summary>
    /// Reads an image object's pixels back as straight BGRA at its native size.
    ///
    /// Tries the rendered bitmap first: a signature's transparency lives in an
    /// /SMask, and the plain decoded bitmap can come back as opaque BGR with the
    /// mask dropped, which would stamp a white block. Falls back to the decoded
    /// bitmap when the rendered one is unavailable.
    ///
    /// Returns null when there is nothing readable, which callers must treat as
    /// "cannot do this to that stamp" rather than as an error.
    /// </summary>
    public static (byte[] Bgra, int Width, int Height)? TryReadImagePixels(
        IntPtr document, IntPtr page, IntPtr imageObject)
    {
        IntPtr bitmap = NativeMethods.FPDFImageObj_GetRenderedBitmap(document, page, imageObject);
        if (bitmap == IntPtr.Zero)
        {
            bitmap = NativeMethods.FPDFImageObj_GetBitmap(imageObject);
        }
        if (bitmap == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            int width = NativeMethods.FPDFBitmap_GetWidth(bitmap);
            int height = NativeMethods.FPDFBitmap_GetHeight(bitmap);
            int stride = NativeMethods.FPDFBitmap_GetStride(bitmap);
            int format = NativeMethods.FPDFBitmap_GetFormat(bitmap);
            IntPtr buffer = NativeMethods.FPDFBitmap_GetBuffer(bitmap);

            if (width <= 0 || height <= 0 || buffer == IntPtr.Zero)
            {
                return null;
            }

            var bgra = new byte[width * height * 4];
            // Copied straight through, NOT un-premultiplied. PDFium's rendered
            // bitmap already holds straight alpha here: a half-alpha grey placed
            // as 128/128 reads back as 128 with alpha 128, and dividing by alpha
            // blew it out to white.
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int dst = (y * width + x) * 4;
                    switch (format)
                    {
                        case 4: // BGRA
                        {
                            int src = y * stride + x * 4;
                            bgra[dst] = Marshal.ReadByte(buffer, src);
                            bgra[dst + 1] = Marshal.ReadByte(buffer, src + 1);
                            bgra[dst + 2] = Marshal.ReadByte(buffer, src + 2);
                            bgra[dst + 3] = Marshal.ReadByte(buffer, src + 3);
                            break;
                        }
                        case 2: // BGR
                        case 3: // BGRx
                        {
                            int pixel = format == 2 ? 3 : 4;
                            int src = y * stride + x * pixel;
                            bgra[dst] = Marshal.ReadByte(buffer, src);
                            bgra[dst + 1] = Marshal.ReadByte(buffer, src + 1);
                            bgra[dst + 2] = Marshal.ReadByte(buffer, src + 2);
                            bgra[dst + 3] = 255;
                            break;
                        }
                        case 1: // Gray
                        {
                            byte v = Marshal.ReadByte(buffer, y * stride + x);
                            bgra[dst] = bgra[dst + 1] = bgra[dst + 2] = v;
                            bgra[dst + 3] = 255;
                            break;
                        }
                        default:
                            return null;
                    }
                }
            }
            return (bgra, width, height);
        }
        finally
        {
            NativeMethods.FPDFBitmap_Destroy(bitmap);
        }
    }

    public static void TransformPageObject(IntPtr pageObject, double a, double b, double c, double d, double e, double f)
        => NativeMethods.FPDFPageObj_Transform(pageObject, a, b, c, d, e, f);

    public static void InsertPageObject(IntPtr page, IntPtr pageObject) => NativeMethods.FPDFPage_InsertObject(page, pageObject);

    /// <summary>Required after any page-content edit, or the change is not serialized.</summary>
    public static bool GenerateContent(IntPtr page) => NativeMethods.FPDFPage_GenerateContent(page) != 0;

    public static bool AppendAnnotObject(IntPtr annot, IntPtr pageObject)
        => NativeMethods.FPDFAnnot_AppendObject(annot, pageObject) != 0;

    // ---- Text objects ----

    /// <summary>
    /// Loads one of the standard 14 fonts by PostScript name. Returns
    /// <see cref="IntPtr.Zero"/> if PDFium does not know the name, which is the
    /// only failure mode worth checking: these fonts are built in, so there is
    /// no file to be missing.
    /// </summary>
    public static IntPtr LoadStandardFont(IntPtr document, string postScriptName)
        => NativeMethods.FPDFText_LoadStandardFont(document, postScriptName);

    public static void CloseFont(IntPtr font) => NativeMethods.FPDFFont_Close(font);

    public static IntPtr NewTextObject(IntPtr document, IntPtr font, float fontSize)
        => NativeMethods.FPDFPageObj_CreateTextObj(document, font, fontSize);

    public static bool SetTextObjectText(IntPtr textObject, string text)
        => NativeMethods.FPDFText_SetText(textObject, text) != 0;

    public static bool SetObjectFillColor(IntPtr pageObject, byte r, byte g, byte b, byte a)
        => NativeMethods.FPDFPageObj_SetFillColor(pageObject, r, g, b, a) != 0;

    /// <summary>
    /// The extent PDFium will draw this object at, in page space. Returns null
    /// when it cannot say, which for a text object means there is nothing to
    /// measure.
    /// </summary>
    public static (float L, float B, float R, float T)? GetObjectBounds(IntPtr pageObject)
        => NativeMethods.FPDFPageObj_GetBounds(pageObject, out float l, out float b, out float r, out float t) != 0
            ? (l, b, r, t)
            : null;

    /// <summary>
    /// How far the font hangs below the baseline at this size, as a positive
    /// number of points. Falls back to a twelfth of the size when PDFium will
    /// not say — the standard 14 all sit near that, and an underline slightly
    /// off is better than none.
    /// </summary>
    public static float GetFontDescent(IntPtr font, float fontSize)
        => NativeMethods.FPDFFont_GetDescent(font, fontSize, out float descent) != 0
            ? Math.Abs(descent)
            : fontSize / 12f;

    /// <summary>
    /// A filled rectangle, ready to append. <paramref name="y"/> is its BOTTOM
    /// edge, PDF's way up. Returns <see cref="IntPtr.Zero"/> when PDFium refuses
    /// it, which callers treat as "no rule under this line" rather than as an
    /// error worth taking the whole text box down for.
    /// </summary>
    public static IntPtr NewFilledRect(float x, float y, float width, float height, byte r, byte g, byte b)
    {
        IntPtr path = NativeMethods.FPDFPageObj_CreateNewRect(x, y, width, height);
        if (path == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }
        // Fill, no stroke. Without a draw mode the path carries no paint
        // operator at all and serializes to something that draws nothing.
        if (NativeMethods.FPDFPath_SetDrawMode(path, NativeMethods.FPDF_FILLMODE_WINDING, 0) == 0
            || !SetObjectFillColor(path, r, g, b, 255))
        {
            DestroyPageObject(path);
            return IntPtr.Zero;
        }
        return path;
    }

    // ---- Document properties ----

    public static uint GetDocPermissions(IntPtr document) => NativeMethods.FPDF_GetDocPermissions(document);

    /// <summary>The security handler revision, or -1 when the file is not encrypted.</summary>
    public static int GetSecurityRevision(IntPtr document) => NativeMethods.FPDF_GetSecurityHandlerRevision(document);

    public static bool IsTagged(IntPtr document) => NativeMethods.FPDFCatalog_IsTagged(document) != 0;

    public static int GetAttachmentCount(IntPtr document) => NativeMethods.FPDFDoc_GetAttachmentCount(document);

    public static int CountPageObjects(IntPtr page) => NativeMethods.FPDFPage_CountObjects(page);

    public static IntPtr GetPageObject(IntPtr page, int index) => NativeMethods.FPDFPage_GetObject(page, index);

    public static bool IsFormObject(IntPtr pageObject)
        => NativeMethods.FPDFPageObj_GetType(pageObject) == NativeMethods.FPDF_PAGEOBJ_FORM;

    public static int CountFormObjects(IntPtr formObject) => NativeMethods.FPDFFormObj_CountObjects(formObject);

    public static IntPtr GetFormObject(IntPtr formObject, int index)
        => NativeMethods.FPDFFormObj_GetObject(formObject, (uint)index);

    /// <summary>
    /// A text run's font as its name, whether it is embedded, and its descriptor
    /// flags. One hop rather than three, because the font handle is borrowed
    /// from the document and must not outlive the call that fetched it.
    /// </summary>
    public static (string Name, bool Embedded, int Flags)? DescribeTextObjectFont(IntPtr textObject)
    {
        IntPtr font = NativeMethods.FPDFTextObj_GetFont(textObject);
        if (font == IntPtr.Zero)
        {
            return null;
        }

        uint length = NativeMethods.FPDFFont_GetBaseFontName(font, null, 0);
        if (length <= 1)
        {
            return null; // 0 is failure, 1 is the terminator alone
        }

        var buffer = new byte[length];
        if (NativeMethods.FPDFFont_GetBaseFontName(font, buffer, length) != length)
        {
            return null;
        }

        return (System.Text.Encoding.ASCII.GetString(buffer, 0, (int)length - 1),
                NativeMethods.FPDFFont_GetIsEmbedded(font) != 0,
                NativeMethods.FPDFFont_GetFlags(font));
    }

    /// <summary>
    /// Points an image object at BGRA pixels.
    ///
    /// The buffer must stay pinned for the duration of the call — PDFium copies
    /// out of it here, but the FPDF_BITMAP wraps it directly. Uses
    /// FPDFBitmap_CreateEx (the same primitive the renderer uses, just in
    /// reverse) because FPDFBitmap_GetBuffer isn't bound, which would leave a
    /// bitmap from FPDFBitmap_Create impossible to fill.
    /// </summary>
    public static unsafe bool SetImageObjectBitmap(IntPtr page, IntPtr imageObject, byte[] bgra, int width, int height)
    {
        fixed (byte* p = bgra)
        {
            IntPtr bitmap = NativeMethods.FPDFBitmap_CreateEx(
                width, height, NativeMethods.FPDFBitmap_BGRA, (IntPtr)p, width * 4);
            if (bitmap == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                // Takes an ARRAY of pages, not a page — the image may be shared
                // by several. Passing a bare handle compiles and corrupts memory.
                IntPtr[] pages = [page];
                return NativeMethods.FPDFImageObj_SetBitmap(pages, 1, imageObject, bitmap) != 0;
            }
            finally
            {
                NativeMethods.FPDFBitmap_Destroy(bitmap);
            }
        }
    }

    // ---- Flatten ----

    /// <summary>Bakes annotations and form widgets into page content. Returns an FLATTEN_* code.</summary>
    public static int FlattenPage(IntPtr page, bool forPrint = false)
        => NativeMethods.FPDFPage_Flatten(page, forPrint ? NativeMethods.FLAT_PRINT : NativeMethods.FLAT_NORMALDISPLAY);

    public const int FlattenFail = NativeMethods.FLATTEN_FAIL;
    public const int FlattenSuccess = NativeMethods.FLATTEN_SUCCESS;
    public const int FlattenNothingToDo = NativeMethods.FLATTEN_NOTHINGTODO;

    // ---- Digital signatures (read-only reporting; PDFium does not validate) ----

    public static int GetSignatureCount(IntPtr document) => NativeMethods.FPDF_GetSignatureCount(document);

    public static IntPtr GetSignatureObject(IntPtr document, int index) => NativeMethods.FPDF_GetSignatureObject(document, index);

    /// <summary>The /ByteRange array, normally 4 ints. Empty when absent or malformed.</summary>
    public static int[] GetSignatureByteRange(IntPtr signature)
    {
        uint count = NativeMethods.FPDFSignatureObj_GetByteRange(signature, null, 0);
        if (count == 0)
        {
            return [];
        }
        var buffer = new int[count];
        uint written = NativeMethods.FPDFSignatureObj_GetByteRange(signature, buffer, count);
        return written == count ? buffer : [];
    }

    /// <summary>Length of the raw PKCS#7 blob. Rune reports its size but cannot verify it.</summary>
    public static int GetSignatureContentsLength(IntPtr signature)
        => (int)NativeMethods.FPDFSignatureObj_GetContents(signature, null, 0);

    /// <summary>ASCII, per fpdf_signature.h — decoding this as UTF-16 yields mojibake.</summary>
    public static string GetSignatureSubFilter(IntPtr signature)
        => ReadAscii(NativeMethods.FPDFSignatureObj_GetSubFilter(signature, null, 0),
                     buffer => NativeMethods.FPDFSignatureObj_GetSubFilter(signature, buffer, (uint)buffer.Length));

    /// <summary>ASCII PDF date string.</summary>
    public static string GetSignatureTime(IntPtr signature)
        => ReadAscii(NativeMethods.FPDFSignatureObj_GetTime(signature, null, 0),
                     buffer => NativeMethods.FPDFSignatureObj_GetTime(signature, buffer, (uint)buffer.Length));

    /// <summary>UTF-16LE, unlike SubFilter and Time.</summary>
    public static string GetSignatureReason(IntPtr signature)
        => ReadUtf16(NativeMethods.FPDFSignatureObj_GetReason(signature, null, 0),
                     buffer => NativeMethods.FPDFSignatureObj_GetReason(signature, buffer, (uint)buffer.Length));

    /// <summary>DocMDP level 1–3, or 0 when the signature carries no DocMDP transform.</summary>
    public static uint GetSignatureDocMdpPermission(IntPtr signature)
        => NativeMethods.FPDFSignatureObj_GetDocMDPPermission(signature);

    private static string ReadAscii(uint bytes, Func<byte[], uint> fill)
    {
        if (bytes == 0)
        {
            return string.Empty;
        }
        var buffer = new byte[bytes];
        uint written = fill(buffer);
        int length = (int)Math.Min(written, bytes);
        // These APIs do not NUL-terminate consistently; trim if one is present.
        while (length > 0 && buffer[length - 1] == 0)
        {
            length--;
        }
        return System.Text.Encoding.ASCII.GetString(buffer, 0, length);
    }

    private static string ReadUtf16(uint bytes, Func<byte[], uint> fill)
    {
        if (bytes <= 2)
        {
            return string.Empty; // just the UTF-16 terminator, or nothing
        }
        var buffer = new byte[bytes];
        fill(buffer);
        // Strip the trailing UTF-16LE NUL.
        return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)bytes - 2);
    }
}
