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
