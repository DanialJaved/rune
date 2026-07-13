using System.Runtime.InteropServices.WindowsRuntime;
using Folio.Engine;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using Windows.Graphics.Printing;

namespace Folio.Services;

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
    private string _jobName = "Folio";
    private bool _registered;

    public PrintService(nint hwnd) => _hwnd = hwnd;

    public static bool IsSupported => PrintManager.IsSupported();

    /// <summary>Opens the system print dialog for the given document.</summary>
    public async Task ShowAsync(PdfDocument document, string jobName)
    {
        _document = document;
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

    private void PrintDocument_GetPreviewPage(object sender, GetPreviewPageEventArgs e)
    {
        if (_document is { } doc && e.PageNumber >= 1 && e.PageNumber <= doc.PageCount)
        {
            // Preview at screen-ish resolution; cheap and rendered on demand.
            _printDocument?.SetPreviewPage(e.PageNumber, BuildPageElement(doc, e.PageNumber - 1, dpi: 96f));
        }
    }

    private void PrintDocument_AddPages(object sender, AddPagesEventArgs e)
    {
        if (_document is not { } doc || _printDocument is null)
        {
            return;
        }

        // Honor a custom range from the dialog; otherwise print everything.
        var ranges = e.PrintTaskOptions.CustomPageRanges;
        var pageNumbers = ranges.Count > 0
            ? ranges.SelectMany(r => Enumerable.Range(r.FirstPageNumber, r.LastPageNumber - r.FirstPageNumber + 1))
                    .Where(n => n >= 1 && n <= doc.PageCount)
                    .Distinct()
                    .OrderBy(n => n)
            : Enumerable.Range(1, doc.PageCount);

        foreach (int pageNumber in pageNumbers)
        {
            _printDocument.AddPage(BuildPageElement(doc, pageNumber - 1, PrintDpi));
        }
        _printDocument.AddPagesComplete();
    }

    private static Image BuildPageElement(PdfDocument doc, int pageIndex, float dpi)
    {
        var page = doc.RenderPage(pageIndex, scale: dpi / 72f);
        var bitmap = new WriteableBitmap(page.Width, page.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(page.Pixels, 0, page.Stride * page.Height);
        }
        bitmap.Invalidate();
        page.Return();

        var (ptWidth, ptHeight) = doc.GetPageSize(pageIndex);
        return new Image
        {
            Source = bitmap,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            // Element size in DIPs = points ÷ 72 × 96 so the printed page keeps its physical size.
            Width = ptWidth / 72.0 * 96.0,
            Height = ptHeight / 72.0 * 96.0,
        };
    }
}
