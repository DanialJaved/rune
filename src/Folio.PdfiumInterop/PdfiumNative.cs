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
