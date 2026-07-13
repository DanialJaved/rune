using Folio.Controls;
using Folio.Engine;
using Folio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;

namespace Folio;

public sealed partial class MainWindow : Window
{
    private readonly AppStateStore _store = new();
    private readonly AppState _state;
    private readonly Dictionary<DocumentView, RecentFile?> _pendingRestore = [];

    private PdfViewer? _activeViewer;
    private bool _suppressPageBox;
    private bool _restoringSession;

    // Find-in-document state.
    private CancellationTokenSource? _searchCts;
    private List<SearchHit> _searchHits = [];
    private int _activeHitIndex = -1;

    public MainWindow()
    {
        InitializeComponent();

        // Draw our own title bar so the window chrome matches the app.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _state = _store.Load();
        RegisterAccelerators();
        PopulateRecents();

        Activated += MainWindow_FirstActivated;
        Closed += MainWindow_Closed;
    }

    // ---------------------------------------------------------------- lifecycle

    private bool _sessionRestored;

    private async void MainWindow_FirstActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_sessionRestored)
        {
            return;
        }
        _sessionRestored = true;
        await RestoreSessionAsync();
    }

    private async Task RestoreSessionAsync()
    {
        _restoringSession = true;
        var paths = _state.Session.OpenPaths.Where(File.Exists).ToList();
        foreach (var path in paths)
        {
            AddTab(path, _state.FindRecent(path), select: false);
        }

        if (Tabs.TabItems.Count > 0)
        {
            int active = Math.Clamp(_state.Session.ActiveIndex, 0, Tabs.TabItems.Count - 1);
            Tabs.SelectedIndex = active;
        }
        _restoringSession = false;

        UpdateStartPageVisibility();
        if (Tabs.SelectedItem is TabViewItem item && item.Content is DocumentView view)
        {
            await LoadTabAsync(view);
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs e)
    {
        // Persist each open document's position, then the session itself.
        var openPaths = new List<string>();
        foreach (var obj in Tabs.TabItems)
        {
            if (obj is TabViewItem { Content: DocumentView view })
            {
                openPaths.Add(view.FilePath);
                CaptureState(view);
                view.Close();
            }
        }
        _state.Session = new SessionState
        {
            OpenPaths = openPaths,
            ActiveIndex = Math.Max(0, Tabs.SelectedIndex),
        };
        _store.Save(_state);
    }

    private void CaptureState(DocumentView view)
    {
        if (!view.IsLoaded || view.LoadError is not null)
        {
            return;
        }
        var viewer = view.Viewer;
        _state.Remember(view.FilePath, view.DisplayName,
            viewer.CurrentPage, viewer.Zoom, viewer.Rotation, viewer.ScrollFraction);
    }

    // ---------------------------------------------------------------- tabs

    private void AddTab(string path, RecentFile? restore, bool select)
    {
        var view = new DocumentView(path);
        _pendingRestore[view] = restore;
        view.Viewer.LinkActivated += Viewer_LinkActivated;
        view.Loaded2 += (_, _) => { if (view == CurrentView) { UpdateToolbarForActive(); } };

        var tab = new TabViewItem
        {
            Header = view.DisplayName,
            Content = view,
            IconSource = new SymbolIconSource { Symbol = Symbol.Document },
        };
        Tabs.TabItems.Add(tab);
        if (select)
        {
            Tabs.SelectedItem = tab;
        }
    }

    /// <summary>Opens a path in a new tab, or activates the existing tab if already open.</summary>
    private async void OpenOrActivate(string path)
    {
        foreach (var obj in Tabs.TabItems)
        {
            if (obj is TabViewItem { Content: DocumentView v } tabItem &&
                string.Equals(v.FilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                Tabs.SelectedItem = tabItem;
                return;
            }
        }

        AddTab(path, _state.FindRecent(path), select: true);
        UpdateStartPageVisibility();
        if (CurrentView is { } view)
        {
            await LoadTabAsync(view);
        }
    }

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        HookActiveViewer();
        UpdateStartPageVisibility();

        if (!_restoringSession && CurrentView is { } view)
        {
            await LoadTabAsync(view);
        }
    }

    private async Task LoadTabAsync(DocumentView view)
    {
        _pendingRestore.Remove(view, out var restore);
        await view.EnsureLoadedAsync(restore);

        if (view.LoadError is { } error && view == CurrentView)
        {
            ShowError(error);
        }
        if (view == CurrentView)
        {
            UpdateToolbarForActive();
        }
    }

    private void Tabs_AddTabButtonClick(TabView sender, object args) => OpenButton_Click(sender, null!);

    private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) => CloseTab(args.Tab);

    private void CloseTab(TabViewItem tab)
    {
        if (tab.Content is DocumentView view)
        {
            CaptureState(view);
            view.Viewer.LinkActivated -= Viewer_LinkActivated;
            view.Close();
            _pendingRestore.Remove(view);
        }
        Tabs.TabItems.Remove(tab);
        UpdateStartPageVisibility();
    }

    private DocumentView? CurrentView =>
        (Tabs.SelectedItem as TabViewItem)?.Content as DocumentView;

    private void UpdateStartPageVisibility()
    {
        bool hasTabs = Tabs.TabItems.Count > 0;
        StartPage.Visibility = hasTabs ? Visibility.Collapsed : Visibility.Visible;
        Tabs.Visibility = hasTabs ? Visibility.Visible : Visibility.Collapsed;
        if (!hasTabs)
        {
            Title = "Folio";
            TitleText.Text = "Folio";
            PopulateRecents();
        }
    }

    // ---------------------------------------------------------------- active viewer wiring

    private void HookActiveViewer()
    {
        if (_activeViewer is not null)
        {
            _activeViewer.CurrentPageChanged -= Viewer_CurrentPageChanged;
            _activeViewer.ZoomChanged -= Viewer_ZoomChanged;
            _activeViewer.HistoryChanged -= Viewer_HistoryChanged;
        }

        _activeViewer = CurrentView?.Viewer;

        if (_activeViewer is not null)
        {
            _activeViewer.CurrentPageChanged += Viewer_CurrentPageChanged;
            _activeViewer.ZoomChanged += Viewer_ZoomChanged;
            _activeViewer.HistoryChanged += Viewer_HistoryChanged;
        }

        if (CurrentView is { } view)
        {
            string name = view.DisplayName;
            Title = $"{name} — Folio";
            TitleText.Text = $"{name} — Folio";
        }

        // Re-run any active search against the newly-focused document.
        if (FindBar.Visibility == Visibility.Visible)
        {
            RunSearch();
        }
    }

    private void Viewer_CurrentPageChanged(object? sender, int pageIndex)
    {
        _suppressPageBox = true;
        PageBox.Value = pageIndex + 1;
        _suppressPageBox = false;
        int count = _activeViewer?.PageCount ?? 0;
        PrevButton.IsEnabled = pageIndex > 0;
        NextButton.IsEnabled = pageIndex < count - 1;
    }

    private void Viewer_ZoomChanged(object? sender, double zoom)
    {
        ZoomLabel.Text = $"{Math.Round(zoom * 100)}%";
        UpdateFitToggles();
    }

    private void Viewer_HistoryChanged(object? sender, EventArgs e)
    {
        BackButton.IsEnabled = _activeViewer?.CanGoBack ?? false;
        ForwardButton.IsEnabled = _activeViewer?.CanGoForward ?? false;
    }

    private void UpdateToolbarForActive()
    {
        var view = CurrentView;
        bool ready = view is { IsLoaded: true, LoadError: null };
        var viewer = ready ? view!.Viewer : null;

        foreach (var control in new Control[]
                 {
                     SidebarButton, PageBox, ZoomInButton, ZoomOutButton,
                     FitWidthButton, FitPageButton, RotateButton,
                 })
        {
            control.IsEnabled = ready;
        }

        if (viewer is null)
        {
            PageCountLabel.Text = "";
            return;
        }

        _suppressPageBox = true;
        PageBox.Maximum = viewer.PageCount;
        PageBox.Value = viewer.CurrentPage + 1;
        _suppressPageBox = false;
        PageCountLabel.Text = $"of {viewer.PageCount}";
        PrevButton.IsEnabled = viewer.CurrentPage > 0;
        NextButton.IsEnabled = viewer.CurrentPage < viewer.PageCount - 1;
        ZoomLabel.Text = $"{Math.Round(viewer.Zoom * 100)}%";
        SidebarButton.IsChecked = view!.IsPaneOpen;
        BackButton.IsEnabled = viewer.CanGoBack;
        ForwardButton.IsEnabled = viewer.CanGoForward;
        UpdateFitToggles();
    }

    // ---------------------------------------------------------------- commands

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".pdf");

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            OpenOrActivate(file.Path);
        }
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
        {
            if (File.Exists(path))
            {
                OpenOrActivate(path);
            }
            else
            {
                ShowError($"File not found: {path}");
                _state.Recents.RemoveAll(r => r.Path == path);
                PopulateRecents();
            }
        }
    }

    private void SidebarButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentView is { IsLoaded: true } view)
        {
            view.IsPaneOpen = !view.IsPaneOpen;
            SidebarButton.IsChecked = view.IsPaneOpen;
        }
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e) => _activeViewer?.GoToPage((_activeViewer?.CurrentPage ?? 1) - 1);
    private void NextButton_Click(object sender, RoutedEventArgs e) => _activeViewer?.GoToPage((_activeViewer?.CurrentPage ?? -1) + 1);
    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => _activeViewer?.ZoomIn();
    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => _activeViewer?.ZoomOut();
    private void RotateButton_Click(object sender, RoutedEventArgs e) => _activeViewer?.RotateClockwise();
    private void BackButton_Click(object sender, RoutedEventArgs e) => _activeViewer?.GoBack();
    private void ForwardButton_Click(object sender, RoutedEventArgs e) => _activeViewer?.GoForward();
    private void FitWidthButton_Click(object sender, RoutedEventArgs e) => SetFitMode(FitMode.FitWidth);
    private void FitPageButton_Click(object sender, RoutedEventArgs e) => SetFitMode(FitMode.FitPage);

    private void SetFitMode(FitMode mode)
    {
        if (_activeViewer is not null)
        {
            _activeViewer.FitMode = mode;
            UpdateFitToggles();
        }
    }

    private void UpdateFitToggles()
    {
        FitWidthButton.IsChecked = _activeViewer?.FitMode == FitMode.FitWidth;
        FitPageButton.IsChecked = _activeViewer?.FitMode == FitMode.FitPage;
    }

    private void PageBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_suppressPageBox && _activeViewer is not null && !double.IsNaN(args.NewValue))
        {
            _activeViewer.GoToPage((int)args.NewValue - 1, recordHistory: true);
        }
    }

    private async void Viewer_LinkActivated(object? sender, string uri)
    {
        // Opening a URL leaves the app — confirm the destination first, since
        // a PDF link's target isn't visible before clicking.
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != "http" && parsed.Scheme != "https"))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Open link?",
            Content = parsed.ToString(),
            PrimaryButtonText = "Open in browser",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await Launcher.LaunchUriAsync(parsed);
        }
    }

    // ---------------------------------------------------------------- recents

    private void PopulateRecents()
    {
        var recents = _state.Recents.Take(AppState.MaxRecents).ToList();
        RecentsList.ItemsSource = recents;
        RecentsHeader.Visibility = recents.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- accelerators

    private void RegisterAccelerators()
    {
        AddAccelerator(VirtualKey.Add, VirtualKeyModifiers.Control, () => _activeViewer?.ZoomIn());
        AddAccelerator((VirtualKey)0xBB, VirtualKeyModifiers.Control, () => _activeViewer?.ZoomIn());
        AddAccelerator(VirtualKey.Subtract, VirtualKeyModifiers.Control, () => _activeViewer?.ZoomOut());
        AddAccelerator((VirtualKey)0xBD, VirtualKeyModifiers.Control, () => _activeViewer?.ZoomOut());
        AddAccelerator(VirtualKey.Number0, VirtualKeyModifiers.Control, () => SetFitMode(FitMode.FitPage));
        AddAccelerator(VirtualKey.Number1, VirtualKeyModifiers.Control, () => _activeViewer?.SetZoom(1.0));
        AddAccelerator(VirtualKey.Number2, VirtualKeyModifiers.Control, () => SetFitMode(FitMode.FitWidth));
        AddAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, () => _activeViewer?.GoBack());
        AddAccelerator(VirtualKey.Right, VirtualKeyModifiers.Menu, () => _activeViewer?.GoForward());
        AddAccelerator(VirtualKey.W, VirtualKeyModifiers.Control, CloseCurrentTab);
        AddAccelerator(VirtualKey.F, VirtualKeyModifiers.Control, ShowFindBar);
        AddAccelerator(VirtualKey.C, VirtualKeyModifiers.Control, CopySelection);
        AddAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None, HideFindBar);
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            if (_activeViewer is not null)
            {
                action();
                args.Handled = true;
            }
        };
        ((UIElement)Content).KeyboardAccelerators.Add(accelerator);
    }

    private void CloseCurrentTab()
    {
        if (Tabs.SelectedItem is TabViewItem tab)
        {
            CloseTab(tab);
        }
    }

    // ---------------------------------------------------------------- find in document

    private void ShowFindBar()
    {
        if (CurrentView is not { IsLoaded: true })
        {
            return;
        }
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus(FocusState.Programmatic);
        FindBox.SelectAll();
        if (_activeViewer?.HasSelection == true)
        {
            FindBox.Text = _activeViewer.SelectedText.Split('\n')[0].Trim();
        }
        if (!string.IsNullOrEmpty(FindBox.Text))
        {
            RunSearch();
        }
    }

    private void HideFindBar()
    {
        if (FindBar.Visibility != Visibility.Visible)
        {
            return;
        }
        FindBar.Visibility = Visibility.Collapsed;
        _searchCts?.Cancel();
        _activeViewer?.ClearSearch();
        _searchHits = [];
        _activeHitIndex = -1;
        FindCount.Text = "";
    }

    private void FindClose_Click(object sender, RoutedEventArgs e) => HideFindBar();
    private void FindBox_TextChanged(object sender, TextChangedEventArgs e) => RunSearch();
    private void MatchCase_Click(object sender, RoutedEventArgs e) => RunSearch();
    private void FindNext_Click(object sender, RoutedEventArgs e) => StepHit(+1);
    private void FindPrev_Click(object sender, RoutedEventArgs e) => StepHit(-1);

    private void FindBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            StepHit(shift ? -1 : +1);
            e.Handled = true;
        }
    }

    private async void RunSearch()
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        string query = FindBox.Text;
        var viewer = _activeViewer;
        var document = CurrentView?.Viewer.Document;

        viewer?.ClearSearch();
        var collected = new List<SearchHit>();
        _searchHits = collected;
        _activeHitIndex = -1;
        FindCount.Text = "";

        if (string.IsNullOrEmpty(query) || viewer is null || document is null)
        {
            return;
        }

        try
        {
            await Task.Delay(200, cts.Token); // debounce rapid typing
            bool matchCase = MatchCaseButton.IsChecked == true;
            var search = new DocumentSearch(document, query, matchCase, wholeWord: false);

            await search.RunAsync(
                onPageHits: hits => DispatcherQueue.TryEnqueue(() =>
                {
                    if (_searchCts != cts)
                    {
                        return; // superseded by a newer query
                    }
                    collected.AddRange(hits);
                    viewer.SetSearchResults(collected);
                    if (_activeHitIndex < 0 && collected.Count > 0)
                    {
                        _activeHitIndex = 0;
                        viewer.HighlightHit(collected[0]);
                    }
                    UpdateFindCount();
                }),
                onProgress: null,
                cts.Token);

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_searchCts == cts && collected.Count == 0)
                {
                    FindCount.Text = "No results";
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded query; ignore.
        }
    }

    private void StepHit(int delta)
    {
        int n = _searchHits.Count;
        if (n == 0)
        {
            return;
        }
        _activeHitIndex = ((_activeHitIndex + delta) % n + n) % n;
        _activeViewer?.HighlightHit(_searchHits[_activeHitIndex]);
        UpdateFindCount();
    }

    private void UpdateFindCount()
    {
        int n = _searchHits.Count;
        FindCount.Text = n == 0 ? "" : $"{_activeHitIndex + 1} of {n}";
    }

    private void CopySelection()
    {
        string text = _activeViewer?.SelectedText ?? "";
        if (!string.IsNullOrEmpty(text))
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
    }

    // ---------------------------------------------------------------- errors + external open

    internal async Task LoadDocumentAsync(string path, int? initialPage = null, double? initialZoom = null)
    {
        if (!File.Exists(path))
        {
            ShowError($"File not found: {path}");
            return;
        }
        OpenOrActivate(path);

        // Command-line overrides for scripted testing.
        if ((initialPage ?? initialZoom) is not null && CurrentView is { } view)
        {
            await view.EnsureLoadedAsync(null);
            if (initialZoom is double z)
            {
                view.Viewer.SetZoom(z);
            }
            if (initialPage is int p)
            {
                view.Viewer.GoToPage(p - 1);
            }
        }
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
