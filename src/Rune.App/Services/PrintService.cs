using System.Runtime.InteropServices.WindowsRuntime;
using Rune.Engine;
using Rune.PdfiumInterop;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using Windows.Graphics.Printing;

namespace Rune.Services;

/// <summary>
/// Prints a <see cref="PdfDocument"/> through the Windows print dialog.
/// Pages are rendered by PDFium at ~150 DPI into Image elements that XAML's
/// PrintDocument paginates. Preview pages render on demand; the final job
/// honors the dialog's custom page-range option so huge documents don't have
/// to materialize entirely.
/// </summary>
public sealed class PrintService
{
    private const float PrintDpi = 150f;

    private readonly nint _hwnd;
    private PrintDocument? _printDocument;
    private IPrintDocumentSource? _printSource;
    private PdfDocument? _document;
    private IPdfWorkQueue? _workQueue;
    private string _jobName = "Rune";
    private bool _registered;

    public PrintService(nint hwnd) => _hwnd = hwnd;

    public static bool IsSupported => PrintManager.IsSupported();

    /// <summary>
    /// Opens the system print dialog for the given document.
    /// <paramref name="workQueue"/> is the document's render thread — page
    /// rasterization is scheduled there, never run inline in the print
    /// callbacks (which arrive on the UI thread).
    /// </summary>
    public async Task ShowAsync(PdfDocument document, string jobName, IPdfWorkQueue workQueue)
    {
        _document = document;
        _workQueue = workQueue;
        _jobName = jobName;

        if (!_registered)
        {
            var manager = PrintManagerInterop.GetForWindow(_hwnd);
            manager.PrintTaskRequested += Manager_PrintTaskRequested;
            _registered = true;
        }

        // A fresh PrintDocument per job: reusing one across jobs breaks preview.
        _printDocument = new PrintDocument();
        _printDocument.Paginate += PrintDocument_Paginate;
        _printDocument.GetPreviewPage += PrintDocument_GetPreviewPage;
        _printDocument.AddPages += PrintDocument_AddPages;
        _printSource = _printDocument.DocumentSource;

        await PrintManagerInterop.ShowPrintUIForWindowAsync(_hwnd);
    }

    private void Manager_PrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        var task = args.Request.CreatePrintTask(_jobName, sourceArgs =>
        {
            sourceArgs.SetSource(_printSource);
        });
        // Let the dialog offer "All pages" vs a custom range.
        task.Options.PageRangeOptions.AllowAllPages = true;
        task.Options.PageRangeOptions.AllowCustomSetOfPages = true;
    }

    private void PrintDocument_Paginate(object sender, PaginateEventArgs e)
    {
        _printDocument?.SetPreviewPageCount(_document?.PageCount ?? 0, PreviewPageCountType.Final);
    }

    // Both handlers are async void: the rasterization they need belongs on the
    // render thread, and the print system explicitly allows SetPreviewPage /
    // AddPage to be called after the handler returns.
    private async void PrintDocument_GetPreviewPage(object sender, GetPreviewPageEventArgs e)
    {
        if (_document is not { } doc || e.PageNumber < 1 || e.PageNumber > doc.PageCount)
        {
            return;
        }

        int pageNumber = e.PageNumber; // read before awaiting; the args are not ours to keep
        try
        {
            // Preview at screen-ish resolution; cheap and rendered on demand.
            var raster = await RenderAsync(doc, pageNumber - 1, dpi: 96f);
            _printDocument?.SetPreviewPage(pageNumber, BuildPageElement(raster));
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or PdfiumException)
        {
            // Document closed or page unreadable — that preview page stays blank.
        }
    }

    private async void PrintDocument_AddPages(object sender, AddPagesEventArgs e)
    {
        if (_document is not { } doc || _printDocument is null)
        {
            return;
        }

        // Honor a custom range from the dialog; otherwise print everything.
        // Materialize before the first await — the event args are only valid
        // for the synchronous part of the callback.
        var ranges = e.PrintTaskOptions.CustomPageRanges;
        List<int> pageNumbers = ranges.Count > 0
            ? [.. ranges.SelectMany(r => Enumerable.Range(r.FirstPageNumber, r.LastPageNumber - r.FirstPageNumber + 1))
                        .Where(n => n >= 1 && n <= doc.PageCount)
                        .Distinct()
                        .OrderBy(n => n)]
            : [.. Enumerable.Range(1, doc.PageCount)];

        try
        {
            foreach (int pageNumber in pageNumbers)
            {
                var raster = await RenderAsync(doc, pageNumber - 1, PrintDpi);
                _printDocument.AddPage(BuildPageElement(raster));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or PdfiumException)
        {
            // Fall through: the job still has to be completed or the dialog hangs.
        }
        finally
        {
            _printDocument.AddPagesComplete();
        }
    }

    /// <summary>A rendered page detached from the pooled buffer, plus its physical size in DIPs.</summary>
    private readonly record struct RasterPage(byte[] Pixels, int Width, int Height, double WidthDip, double HeightDip);

    /// <summary>Rasterizes one page on the render thread and copies it out of the pooled buffer.</summary>
    private async Task<RasterPage> RenderAsync(PdfDocument doc, int pageIndex, float dpi)
    {
        if (_workQueue is not { } queue)
        {
            throw new InvalidOperationException("PrintService was used before ShowAsync supplied a work queue.");
        }

        return await queue.RunAsync(PdfWorkPriority.Interactive, () =>
        {
            var page = doc.RenderPage(pageIndex, scale: dpi / 72f);
            try
            {
                var pixels = new byte[page.Stride * page.Height];
                Array.Copy(page.Pixels, pixels, pixels.Length);
                var (ptWidth, ptHeight) = doc.GetPageSize(pageIndex);
                // Element size in DIPs = points ÷ 72 × 96 so the printed page keeps its physical size.
                return new RasterPage(pixels, page.Width, page.Height, ptWidth / 72.0 * 96.0, ptHeight / 72.0 * 96.0);
            }
            finally
            {
                page.Return();
            }
        });
    }

    /// <summary>UI-thread half: WriteableBitmap and Image must be created here.</summary>
    private static Image BuildPageElement(RasterPage page)
    {
        var bitmap = new WriteableBitmap(page.Width, page.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(page.Pixels, 0, page.Pixels.Length);
        }
        bitmap.Invalidate();

        return new Image
        {
            Source = bitmap,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            Width = page.WidthDip,
            Height = page.HeightDip,
        };
    }
}
