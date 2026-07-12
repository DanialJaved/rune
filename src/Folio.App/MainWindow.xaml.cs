using Folio.Controls;
using Folio.Engine;
using Folio.PdfiumInterop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Windows.System;

namespace Folio;

public sealed partial class MainWindow : Window
{
    private PdfDocument? _document;
    private bool _suppressPageBox;

    public MainWindow()
    {
        InitializeComponent();

        // Draw our own title bar so the window chrome matches the app (Preview/Papers style).
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        Viewer.CurrentPageChanged += Viewer_CurrentPageChanged;
        Viewer.ZoomChanged += Viewer_ZoomChanged;
        Closed += (_, _) =>
        {
            Viewer.SetDocument(null);
            _document?.Dispose();
        };

        RegisterZoomAccelerators();
    }

    private void RegisterZoomAccelerators()
    {
        // Keys with no clean XAML name: +/- (both main row and numpad),
        // and the Ctrl+0/1/2 view presets (Sumatra's shortcuts).
        AddAccelerator(VirtualKey.Add, () => Viewer.ZoomIn());
        AddAccelerator((VirtualKey)0xBB /* OemPlus */, () => Viewer.ZoomIn());
        AddAccelerator(VirtualKey.Subtract, () => Viewer.ZoomOut());
        AddAccelerator((VirtualKey)0xBD /* OemMinus */, () => Viewer.ZoomOut());
        AddAccelerator(VirtualKey.Number0, () => SetFitMode(FitMode.FitPage));
        AddAccelerator(VirtualKey.Number1, () => Viewer.SetZoom(1.0));
        AddAccelerator(VirtualKey.Number2, () => SetFitMode(FitMode.FitWidth));
    }

    private void AddAccelerator(VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
        accelerator.Invoked += (_, e) =>
        {
            if (_document is not null)
            {
                action();
                e.Handled = true;
            }
        };
        ((UIElement)Content).KeyboardAccelerators.Add(accelerator);
    }

    // ---------------------------------------------------------------- commands

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

    private void PrevButton_Click(object sender, RoutedEventArgs e) => Viewer.GoToPage(Viewer.CurrentPage - 1);
    private void NextButton_Click(object sender, RoutedEventArgs e) => Viewer.GoToPage(Viewer.CurrentPage + 1);
    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => Viewer.ZoomIn();
    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => Viewer.ZoomOut();
    private void RotateButton_Click(object sender, RoutedEventArgs e) => Viewer.RotateClockwise();
    private void FitWidthButton_Click(object sender, RoutedEventArgs e) => SetFitMode(FitMode.FitWidth);
    private void FitPageButton_Click(object sender, RoutedEventArgs e) => SetFitMode(FitMode.FitPage);

    private void SetFitMode(FitMode mode)
    {
        Viewer.FitMode = mode;
        UpdateFitToggles();
    }

    private void UpdateFitToggles()
    {
        FitWidthButton.IsChecked = Viewer.FitMode == FitMode.FitWidth;
        FitPageButton.IsChecked = Viewer.FitMode == FitMode.FitPage;
    }

    private void PageBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_suppressPageBox && _document is not null && !double.IsNaN(args.NewValue))
        {
            Viewer.GoToPage((int)args.NewValue - 1);
        }
    }

    // ---------------------------------------------------------------- viewer events

    private void Viewer_CurrentPageChanged(object? sender, int pageIndex)
    {
        _suppressPageBox = true;
        PageBox.Value = pageIndex + 1;
        _suppressPageBox = false;
        PrevButton.IsEnabled = _document is not null && pageIndex > 0;
        NextButton.IsEnabled = _document is not null && pageIndex < Viewer.PageCount - 1;
    }

    private void Viewer_ZoomChanged(object? sender, double zoom)
    {
        ZoomLabel.Text = $"{Math.Round(zoom * 100)}%";
        UpdateFitToggles();
    }

    // ---------------------------------------------------------------- loading

    internal async Task LoadDocumentAsync(string path, int? initialPage = null, double? initialZoom = null)
    {
        try
        {
            var newDocument = await Task.Run(() => PdfDocument.Open(path));

            _document?.Dispose();
            _document = newDocument;

            string name = System.IO.Path.GetFileName(path);
            Title = $"{name} — Folio";
            TitleText.Text = $"{name} — Folio";
            EmptyState.Visibility = Visibility.Collapsed;
            ErrorBar.IsOpen = false;

            Viewer.SetDocument(newDocument);

            _suppressPageBox = true;
            PageBox.Maximum = newDocument.PageCount;
            PageBox.Value = 1;
            _suppressPageBox = false;
            PageCountLabel.Text = $"of {newDocument.PageCount}";

            foreach (var control in new Control[] { PageBox, ZoomInButton, ZoomOutButton, FitWidthButton, FitPageButton, RotateButton })
            {
                control.IsEnabled = true;
            }
            NextButton.IsEnabled = newDocument.PageCount > 1;
            UpdateFitToggles();
            ZoomLabel.Text = $"{Math.Round(Viewer.Zoom * 100)}%";

            if (initialZoom is double zoom)
            {
                Viewer.SetZoom(zoom);
            }
            if (initialPage is int page)
            {
                Viewer.GoToPage(page - 1);
            }
        }
        catch (Exception ex) when (ex is PdfiumException or IOException)
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
