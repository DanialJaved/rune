using Folio.PdfiumInterop;

namespace Folio.Engine;

/// <summary>
/// An open PDF document. Thread-safe: all PDFium access is serialized through
/// the global <see cref="PdfiumLibrary.Lock"/>.
/// </summary>
public sealed class PdfDocument : IDisposable
{
    private readonly FileAccessAdapter _fileAccess;
    private IntPtr _handle;
    private readonly (float Width, float Height)[] _pageSizes;

    public string FilePath { get; }
    public int PageCount { get; }

    private PdfDocument(string path, FileAccessAdapter fileAccess, IntPtr handle)
    {
        FilePath = path;
        _fileAccess = fileAccess;
        _handle = handle;

        // Page sizes come from the page tree without loading pages, so this
        // stays fast even for multi-thousand-page documents. Sizes are in
        // PDF points (1/72 inch) and already account for the page's /Rotate.
        PageCount = PdfiumNative.GetPageCount(handle);
        _pageSizes = new (float, float)[PageCount];
        for (int i = 0; i < PageCount; i++)
        {
            if (!PdfiumNative.TryGetPageSize(handle, i, out float w, out float h) || w <= 0 || h <= 0)
            {
                (w, h) = (612f, 792f); // broken page entry: pretend US Letter
            }
            _pageSizes[i] = (w, h);
        }
    }

    /// <exception cref="PdfiumException">Invalid/corrupt file, or password required.</exception>
    public static PdfDocument Open(string path, string? password = null)
    {
        PdfiumLibrary.EnsureInitialized();

        var fileAccess = new FileAccessAdapter(path);
        lock (PdfiumLibrary.Lock)
        {
            IntPtr handle = PdfiumNative.LoadCustomDocument(fileAccess, password);
            if (handle == IntPtr.Zero)
            {
                var error = PdfiumNative.LastError();
                fileAccess.Dispose();
                throw error;
            }
            return new PdfDocument(path, fileAccess, handle);
        }
    }

    /// <summary>Page size in PDF points (1 pt = 1/72 inch).</summary>
    public (float Width, float Height) GetPageSize(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);
        return _pageSizes[pageIndex];
    }

    /// <summary>
    /// Full page pixel dimensions at a scale (pixels per point), after view rotation.
    /// </summary>
    public (int Width, int Height) GetPagePixelSize(int pageIndex, float scale, int rotation)
    {
        var (ptWidth, ptHeight) = GetPageSize(pageIndex);
        if (rotation % 2 == 1)
        {
            (ptWidth, ptHeight) = (ptHeight, ptWidth);
        }
        return (Math.Max(1, (int)MathF.Round(ptWidth * scale)),
                Math.Max(1, (int)MathF.Round(ptHeight * scale)));
    }

    /// <summary>Renders a whole page. Convenience over <see cref="RenderRegion"/>.</summary>
    public PageBitmap RenderPage(int pageIndex, float scale, int rotation = 0)
    {
        var (width, height) = GetPagePixelSize(pageIndex, scale, rotation);
        return RenderRegion(pageIndex, scale, rotation, 0, 0, width, height);
    }

    /// <summary>
    /// Renders the (srcX, srcY, width, height) pixel window of a page laid out
    /// at <paramref name="scale"/> pixels per point with a view rotation
    /// (0–3 quarter turns clockwise). Safe to call from any thread.
    /// </summary>
    public PageBitmap RenderRegion(int pageIndex, float scale, int rotation, int srcX, int srcY, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var (fullWidth, fullHeight) = GetPagePixelSize(pageIndex, scale, rotation);
        int stride = width * 4;
        // Rented, not allocated: tile renders happen dozens of times per second
        // while scrolling, and multi-MB garbage arrays would thrash the GC.
        var pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(stride * height);

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);

            IntPtr page = PdfiumNative.LoadPage(_handle, pageIndex);
            if (page == IntPtr.Zero)
            {
                throw PdfiumNative.LastError();
            }

            try
            {
                PdfiumNative.RenderRegionToBuffer(page, pixels, srcX, srcY, width, height, fullWidth, fullHeight, rotation % 4, stride);
            }
            finally
            {
                PdfiumNative.ClosePage(page);
            }
        }

        return new PageBitmap(pixels, width, height, stride);
    }

    /// <summary>
    /// The document's table of contents, or an empty list if it has none.
    /// Guards against the cyclic/oversized bookmark trees malformed PDFs carry.
    /// </summary>
    public IReadOnlyList<OutlineItem> GetOutline()
    {
        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            var visited = new HashSet<IntPtr>();
            int budget = 5000; // cap total nodes so a malicious file can't hang us
            return ReadSiblings(PdfiumNative.BookmarkGetFirstChild(_handle, IntPtr.Zero), visited, ref budget);
        }
    }

    private List<OutlineItem> ReadSiblings(IntPtr bookmark, HashSet<IntPtr> visited, ref int budget)
    {
        var items = new List<OutlineItem>();
        while (bookmark != IntPtr.Zero && budget > 0)
        {
            if (!visited.Add(bookmark))
            {
                break; // cycle
            }
            budget--;

            string title = PdfiumNative.BookmarkGetTitle(bookmark);
            int pageIndex = ResolveBookmarkPage(bookmark);
            var children = ReadSiblings(PdfiumNative.BookmarkGetFirstChild(_handle, bookmark), visited, ref budget);

            items.Add(new OutlineItem { Title = title, PageIndex = pageIndex, Children = children });
            bookmark = PdfiumNative.BookmarkGetNextSibling(_handle, bookmark);
        }
        return items;
    }

    private int ResolveBookmarkPage(IntPtr bookmark)
    {
        IntPtr dest = PdfiumNative.BookmarkGetDest(_handle, bookmark);
        if (dest == IntPtr.Zero)
        {
            IntPtr action = PdfiumNative.BookmarkGetAction(bookmark);
            if (action != IntPtr.Zero && PdfiumNative.ActionGetType(action) == PdfiumNative.ActionGoto)
            {
                dest = PdfiumNative.ActionGetDest(_handle, action);
            }
        }
        return dest == IntPtr.Zero ? -1 : PdfiumNative.DestGetPageIndex(_handle, dest);
    }

    /// <summary>
    /// Clickable links on a page, with rectangles in page points and a
    /// top-left origin (multiply by zoom to get layout-space rectangles).
    /// </summary>
    public IReadOnlyList<PdfLink> GetLinks(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

        var links = new List<PdfLink>();
        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);

            IntPtr page = PdfiumNative.LoadPage(_handle, pageIndex);
            if (page == IntPtr.Zero)
            {
                return links;
            }

            try
            {
                var (ptWidth, ptHeight) = _pageSizes[pageIndex];
                int sizeX = Math.Max(1, (int)MathF.Round(ptWidth));
                int sizeY = Math.Max(1, (int)MathF.Round(ptHeight));

                int startPos = 0;
                while (PdfiumNative.LinkEnumerate(page, ref startPos, out IntPtr linkAnnot))
                {
                    if (linkAnnot == IntPtr.Zero || !PdfiumNative.LinkGetRect(linkAnnot, out float l, out float t, out float r, out float b))
                    {
                        continue;
                    }

                    // Map both page-space corners (bottom-left origin) to
                    // device pixels (top-left origin), letting PDFium handle
                    // the page's own /Rotate. 1 device px == 1 point here.
                    var (x1, y1) = PdfiumNative.PageToDevice(page, sizeX, sizeY, 0, l, t);
                    var (x2, y2) = PdfiumNative.PageToDevice(page, sizeX, sizeY, 0, r, b);
                    double x = Math.Min(x1, x2), y = Math.Min(y1, y2);
                    double w = Math.Abs(x2 - x1), h = Math.Abs(y2 - y1);
                    if (w < 1 || h < 1)
                    {
                        continue;
                    }

                    var (target, uri) = ResolveLinkTarget(linkAnnot);
                    if (target >= 0 || uri is not null)
                    {
                        links.Add(new PdfLink(x, y, w, h, target, uri));
                    }
                }
            }
            finally
            {
                PdfiumNative.ClosePage(page);
            }
        }
        return links;
    }

    private (int TargetPage, string? Uri) ResolveLinkTarget(IntPtr linkAnnot)
    {
        IntPtr dest = PdfiumNative.LinkGetDest(_handle, linkAnnot);
        if (dest != IntPtr.Zero)
        {
            return (PdfiumNative.DestGetPageIndex(_handle, dest), null);
        }

        IntPtr action = PdfiumNative.LinkGetAction(linkAnnot);
        if (action == IntPtr.Zero)
        {
            return (-1, null);
        }

        uint type = PdfiumNative.ActionGetType(action);
        if (type == PdfiumNative.ActionGoto)
        {
            IntPtr actionDest = PdfiumNative.ActionGetDest(_handle, action);
            return (actionDest == IntPtr.Zero ? -1 : PdfiumNative.DestGetPageIndex(_handle, actionDest), null);
        }
        if (type == PdfiumNative.ActionUri)
        {
            string uri = PdfiumNative.ActionGetUri(_handle, action);
            return (-1, string.IsNullOrEmpty(uri) ? null : uri);
        }
        return (-1, null);
    }

    public void Dispose()
    {
        lock (PdfiumLibrary.Lock)
        {
            if (_handle != IntPtr.Zero)
            {
                PdfiumNative.CloseDocument(_handle);
                _handle = IntPtr.Zero;
            }
        }
        // Only safe to tear down the file stream after the document is closed:
        // PDFium may read from it lazily right up until FPDF_CloseDocument.
        _fileAccess.Dispose();
    }
}
