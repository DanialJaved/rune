using System.Runtime.InteropServices.WindowsRuntime;
using Folio.Engine;
using Folio.PdfiumInterop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace Folio;

public sealed partial class MainWindow : Window
{
    private PdfDocument? _document;
    private int _pageIndex;

    public MainWindow()
    {
        InitializeComponent();

        // Draw our own title bar so the window chrome matches the app (Preview/Papers style).
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".pdf");

        // Unpackaged apps must associate the picker with our window handle.
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await LoadDocumentAsync(file.Path);
        }
    }

    private async void PrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is not null && _pageIndex > 0)
        {
            _pageIndex--;
            await RenderCurrentPageAsync();
        }
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is not null && _pageIndex < _document.PageCount - 1)
        {
            _pageIndex++;
            await RenderCurrentPageAsync();
        }
    }

    internal async Task LoadDocumentAsync(string path)
    {
        try
        {
            var newDocument = await Task.Run(() => PdfDocument.Open(path));

            _document?.Dispose();
            _document = newDocument;
            _pageIndex = 0;

            string name = System.IO.Path.GetFileName(path);
            Title = $"{name} — Folio";
            TitleText.Text = $"{name} — Folio";
            EmptyState.Visibility = Visibility.Collapsed;
            ErrorBar.IsOpen = false;

            await RenderCurrentPageAsync();
        }
        catch (Exception ex) when (ex is PdfiumException or IOException)
        {
            ShowError(ex.Message);
        }
    }

    private async Task RenderCurrentPageAsync()
    {
        if (_document is not PdfDocument document)
        {
            return;
        }

        int pageIndex = _pageIndex;
        var (pointWidth, _) = document.GetPageSize(pageIndex);

        // Fit the page to the viewport width, and render at the monitor's
        // physical pixel density so text is crisp on scaled displays.
        double viewportWidth = ViewerScroll.ActualWidth;
        if (viewportWidth < 100)
        {
            viewportWidth = 800;
        }
        double xamlScale = (viewportWidth - 48) / pointWidth;
        double rasterizationScale = Content.XamlRoot?.RasterizationScale ?? 1.0;

        try
        {
            var page = await Task.Run(() => document.RenderPage(pageIndex, (float)(xamlScale * rasterizationScale)));

            var bitmap = new WriteableBitmap(page.Width, page.Height);
            using (var stream = bitmap.PixelBuffer.AsStream())
            {
                stream.Write(page.Pixels, 0, page.Pixels.Length);
            }
            bitmap.Invalidate();

            PageImage.Source = bitmap;
            PageImage.Width = page.Width / rasterizationScale;
            PageImage.Height = page.Height / rasterizationScale;

            PageLabel.Text = $"Page {pageIndex + 1} of {document.PageCount}";
            PrevButton.IsEnabled = pageIndex > 0;
            NextButton.IsEnabled = pageIndex < document.PageCount - 1;
        }
        catch (Exception ex) when (ex is PdfiumException or ObjectDisposedException)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
