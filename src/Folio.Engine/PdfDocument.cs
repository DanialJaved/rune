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

        // Cache page sizes up front: cheap (header-only reads) and needed
        // constantly for layout. Sizes are in PDF points (1/72 inch).
        PageCount = PdfiumNative.GetPageCount(handle);
        _pageSizes = new (float, float)[PageCount];
        for (int i = 0; i < PageCount; i++)
        {
            IntPtr page = PdfiumNative.LoadPage(handle, i);
            if (page == IntPtr.Zero)
            {
                _pageSizes[i] = (612f, 792f); // corrupt page: pretend US Letter, render will show error later
                continue;
            }
            _pageSizes[i] = (PdfiumNative.GetPageWidth(page), PdfiumNative.GetPageHeight(page));
            PdfiumNative.ClosePage(page);
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
    /// Renders a page at the given scale (pixels per PDF point; 1.0 ≈ 72 DPI).
    /// Safe to call from any thread.
    /// </summary>
    public PageBitmap RenderPage(int pageIndex, float scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);

        var (ptWidth, ptHeight) = _pageSizes[pageIndex];
        int width = Math.Max(1, (int)MathF.Round(ptWidth * scale));
        int height = Math.Max(1, (int)MathF.Round(ptHeight * scale));
        int stride = width * 4;

        var pixels = new byte[stride * height];

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
                PdfiumNative.RenderPageToBuffer(page, pixels, width, height, stride);
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
