using Rune.PdfiumInterop;

namespace Rune.Engine;

/// <summary>
/// An open PDF document. Thread-safe: all PDFium access is serialized through
/// the global <see cref="PdfiumLibrary.Lock"/>.
/// </summary>
public sealed partial class PdfDocument : IDisposable
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
    /// Document information for the properties dialog: standard metadata
    /// fields (empty entries omitted), PDF version, page count, file size.
    /// </summary>
    public IReadOnlyList<(string Name, string Value)> GetProperties()
    {
        var properties = new List<(string, string)>();
        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);

            foreach (string tag in (string[])["Title", "Author", "Subject", "Keywords", "Creator", "Producer", "CreationDate", "ModDate"])
            {
                string value = PdfiumNative.GetMetaText(_handle, tag);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    properties.Add((tag, value));
                }
            }

            int version = PdfiumNative.GetFileVersion(_handle);
            if (version > 0)
            {
                properties.Add(("PDF version", $"{version / 10}.{version % 10}"));
            }
        }

        properties.Add(("Pages", PageCount.ToString()));
        try
        {
            long bytes = new FileInfo(FilePath).Length;
            properties.Add(("File size", bytes switch
            {
                < 1024 => $"{bytes} B",
                < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
                _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
            }));
        }
        catch (IOException)
        {
            // File may have been moved since opening; size is optional.
        }
        return properties;
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

    // ---------------------------------------------------------------- text & search

    /// <summary>Full extracted text of a page (reading order), or empty for image-only pages.</summary>
    public string ExtractText(int pageIndex) =>
        WithTextPage(pageIndex, (_, textPage) =>
        {
            int count = PdfiumNative.TextCountChars(textPage);
            return count <= 0 ? string.Empty : PdfiumNative.TextGetText(textPage, 0, count);
        }, string.Empty);

    /// <summary>
    /// Char index at a page-local point (top-left origin points), or -1.
    /// <paramref name="tolerance"/> is in points.
    /// </summary>
    public int CharIndexAt(int pageIndex, double localX, double localY, double tolerance = 6) =>
        WithPageAndText(pageIndex, (page, textPage, w, h) =>
        {
            var (px, py) = PdfiumNative.DeviceToPage(page, w, h, (int)Math.Round(localX), (int)Math.Round(localY));
            return PdfiumNative.TextCharIndexAtPos(textPage, px, py, tolerance);
        }, -1);

    /// <summary>Builds a selection over a char range, with text and highlight rects (top-left points).</summary>
    public TextSelection GetSelection(int pageIndex, int anchorChar, int focusChar)
    {
        int start = Math.Min(anchorChar, focusChar);
        int count = Math.Abs(focusChar - anchorChar) + 1;
        return WithPageAndText(pageIndex, (page, textPage, w, h) =>
        {
            int total = PdfiumNative.TextCountChars(textPage);
            start = Math.Clamp(start, 0, Math.Max(0, total - 1));
            count = Math.Clamp(count, 0, total - start);
            string text = count > 0 ? PdfiumNative.TextGetText(textPage, start, count) : string.Empty;
            var rects = CollectRangeRects(textPage, page, w, h, start, count);
            return new TextSelection(pageIndex, start, count, text, rects);
        }, new TextSelection(pageIndex, 0, 0, string.Empty, []));
    }

    /// <summary>All occurrences of <paramref name="query"/> on a page, with highlight rects.</summary>
    public IReadOnlyList<SearchHit> SearchPage(int pageIndex, string query, bool matchCase, bool wholeWord)
    {
        if (string.IsNullOrEmpty(query))
        {
            return [];
        }
        return WithPageAndText(pageIndex, (page, textPage, w, h) =>
        {
            var hits = new List<SearchHit>();
            IntPtr handle = PdfiumNative.TextFindStart(textPage, query, matchCase, wholeWord, 0);
            if (handle == IntPtr.Zero)
            {
                return (IReadOnlyList<SearchHit>)hits;
            }
            try
            {
                const int MaxHitsPerPage = 5000;
                while (hits.Count < MaxHitsPerPage && PdfiumNative.TextFindNext(handle))
                {
                    int idx = PdfiumNative.TextSchResultIndex(handle);
                    int len = PdfiumNative.TextSchCount(handle);
                    var rects = CollectRangeRects(textPage, page, w, h, idx, len);
                    hits.Add(new SearchHit(pageIndex, idx, len, rects));
                }
            }
            finally
            {
                PdfiumNative.TextFindClose(handle);
            }
            return (IReadOnlyList<SearchHit>)hits;
        }, []);
    }

    private static List<TextRect> CollectRangeRects(IntPtr textPage, IntPtr page, int w, int h, int start, int count)
    {
        var rects = new List<TextRect>();
        if (count <= 0)
        {
            return rects;
        }
        int rectCount = PdfiumNative.TextCountRects(textPage, start, count);
        for (int i = 0; i < rectCount; i++)
        {
            if (!PdfiumNative.TextGetRect(textPage, i, out double left, out double top, out double right, out double bottom))
            {
                continue;
            }
            // Page space (bottom-left origin, top>bottom) → device pixels (top-left).
            var (x1, y1) = PdfiumNative.PageToDevice(page, w, h, 0, left, top);
            var (x2, y2) = PdfiumNative.PageToDevice(page, w, h, 0, right, bottom);
            double x = Math.Min(x1, x2), y = Math.Min(y1, y2);
            rects.Add(new TextRect(x, y, Math.Abs(x2 - x1), Math.Abs(y2 - y1)));
        }
        return rects;
    }

    private T WithTextPage<T>(int pageIndex, Func<IntPtr, IntPtr, T> fn, T fallback) =>
        WithPageAndText(pageIndex, (page, textPage, _, _) => fn(page, textPage), fallback);

    /// <summary>Loads page + text page under the lock, runs fn, and cleans up. Returns fallback on failure.</summary>
    private T WithPageAndText<T>(int pageIndex, Func<IntPtr, IntPtr, int, int, T> fn, T fallback)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = PdfiumNative.LoadPage(_handle, pageIndex);
            if (page == IntPtr.Zero)
            {
                return fallback;
            }
            IntPtr textPage = PdfiumNative.TextLoadPage(page);
            if (textPage == IntPtr.Zero)
            {
                PdfiumNative.ClosePage(page);
                return fallback;
            }
            try
            {
                var (ptW, ptH) = _pageSizes[pageIndex];
                return fn(page, textPage, Math.Max(1, (int)MathF.Round(ptW)), Math.Max(1, (int)MathF.Round(ptH)));
            }
            finally
            {
                PdfiumNative.TextClosePage(textPage);
                PdfiumNative.ClosePage(page);
            }
        }
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
