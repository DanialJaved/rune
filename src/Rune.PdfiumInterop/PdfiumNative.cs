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

    /// <summary>Maps a top-left-origin page-point (1 unit = 1 pt) to page space (bottom-left origin).</summary>
    public static (double X, double Y) DeviceToPage(IntPtr page, int sizeX, int sizeY, int deviceX, int deviceY)
    {
        NativeMethods.FPDF_DeviceToPage(page, 0, 0, sizeX, sizeY, 0, deviceX, deviceY, out double px, out double py);
        return (px, py);
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
