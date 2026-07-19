using Rune.Controls;
using Rune.Engine;
using Rune.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace Rune;

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

    private PrintService? _printService;
    private DateTime _lastGPress = DateTime.MinValue; // vim "gg" sequence

    public MainWindow()
    {
        InitializeComponent();

        // Chrome-style: the tab strip is the title bar; its footer is the
        // drag region, kept clear of the system caption buttons.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragArea);
        SizeChanged += (_, _) => UpdateCaptionClearance();

        _state = _store.Load();
        ApplyTheme(_state.Settings.Theme);
        NightButton.IsChecked = _state.Settings.NightMode;

        RegisterAccelerators();
        ((UIElement)Content).KeyDown += Content_KeyDown;
        // Tunneling handler: navigation keys must reach the document even when
        // focus sits on the tab strip or a toolbar button (those controls eat
        // arrow keys in the bubbling phase for their own focus movement).
        ((UIElement)Content).PreviewKeyDown += Content_PreviewKeyDown;
        BuildInkOptionsFlyout();
        PopulateRecents();

        Activated += MainWindow_FirstActivated;
        Closed += MainWindow_Closed;
        AppWindow.Closing += AppWindow_Closing;
    }

    private bool _closeApproved;

    private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_closeApproved)
        {
            return;
        }
        var dirty = AllDocumentViews().Where(v => v.IsDirty).ToList();
        if (dirty.Count == 0)
        {
            return;
        }

        args.Cancel = true; // must be set before any await
        var choice = await PromptSaveChangesAsync(
            dirty.Count == 1 ? dirty[0].DisplayName : $"{dirty.Count} documents");
        if (choice is null)
        {
            return;
        }
        if (choice == true)
        {
            foreach (var view in dirty)
            {
                try
                {
                    await view.SaveInPlaceAsync();
                }
                catch (Exception ex)
                {
                    ShowError($"Save failed: {ex.Message}");
                    return;
                }
            }
        }
        _closeApproved = true;
        Close();
    }

    private void ApplyTheme(string theme)
    {
        ((FrameworkElement)Content).RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private void UpdateCaptionClearance()
    {
        // Reserve room so tabs and the drag area never slide under the
        // minimize/maximize/close buttons. RightInset is in physical pixels.
        double scale = Content.XamlRoot?.RasterizationScale ?? 1.0;
        double inset = AppWindow.TitleBar.RightInset / scale;
        TitleBarDragArea.MinWidth = Math.Max(48, inset + 48);
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
        await CheckForUpdatesAsync(userInitiated: false);
    }

    private async Task RestoreSessionAsync()
    {
        if (!_state.Settings.RestoreSession)
        {
            UpdateStartPageVisibility();
            return;
        }

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
        if (Tabs.SelectedItem is TabViewItem item && item.Tag is DocumentView view)
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
            if (obj is TabViewItem { Tag: DocumentView view })
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
        if (!view.IsDocumentLoaded || view.LoadError is not null)
        {
            return;
        }
        var viewer = view.Viewer;
        _state.Remember(view.FilePath, view.DisplayName,
            viewer.CurrentPage, viewer.Zoom, viewer.ViewRotation, viewer.ScrollFraction);
        // The open view owns the truth about bookmarks while it lives.
        if (_state.FindRecent(view.FilePath) is { } entry)
        {
            entry.Bookmarks = view.GetBookmarks();
        }
    }

    // ---------------------------------------------------------------- tabs

    private void AddTab(string path, RecentFile? restore, bool select)
    {
        var view = new DocumentView(path);
        _pendingRestore[view] = restore;
        view.OpenSidebarOnLoad = _state.Settings.SidebarOpenByDefault;
        view.Viewer.LinkActivated += Viewer_LinkActivated;
        view.Viewer.NightMode = _state.Settings.NightMode;
        view.Viewer.SetInkStyle(_state.Settings.InkColor, _state.Settings.InkWidth);
        view.Viewer.DocumentEdited += (_, _) => UpdateDirtyIndicator(view);
        view.Viewer.NoteRequested += Viewer_NoteRequested;
        view.BookmarksChanged += (_, _) => PersistBookmarks(view);
        view.Loaded2 += (_, _) => { if (view == CurrentView) { UpdateToolbarForActive(); } };

        // Tabs are strip-only (they live in the title bar); the view itself
        // is swapped into DocHost on selection. Tag carries the association.
        var tab = new TabViewItem
        {
            Header = view.DisplayName,
            Tag = view,
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
            if (obj is TabViewItem { Tag: DocumentView v } tabItem &&
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
        DocHost.Content = CurrentView;
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

        if (view.LoadError is null)
        {
            view.LoadBookmarks(_state.FindRecent(view.FilePath)?.Bookmarks ?? []);
        }
        if (view.LoadError is { } error && view == CurrentView)
        {
            ShowError(error);
        }
        if (view == CurrentView)
        {
            UpdateToolbarForActive();
        }
    }

    /// <summary>Writes a view's bookmarks into the recents entry (creating one if needed) and saves.</summary>
    private void PersistBookmarks(DocumentView view)
    {
        var entry = _state.FindRecent(view.FilePath);
        if (entry is null)
        {
            CaptureState(view); // creates the recents entry with the current position
            entry = _state.FindRecent(view.FilePath);
        }
        if (entry is not null)
        {
            entry.Bookmarks = view.GetBookmarks();
            _store.Save(_state);
        }
    }

    private void ToggleBookmark()
    {
        if (CurrentView is not { IsDocumentLoaded: true, LoadError: null } view || _activeViewer is null)
        {
            return;
        }
        view.ToggleBookmark(_activeViewer.CurrentPage);
        if (view.IsPaneOpen)
        {
            view.ShowBookmarksPane(); // show the result where it landed
        }
    }

    private void Tabs_AddTabButtonClick(TabView sender, object args) => OpenButton_Click(sender, null!);

    private async void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        => await CloseTabWithPromptAsync(args.Tab);

    private async Task CloseTabWithPromptAsync(TabViewItem tab)
    {
        if (tab.Tag is DocumentView { IsDirty: true } dirtyView)
        {
            var choice = await PromptSaveChangesAsync(dirtyView.DisplayName);
            if (choice is null)
            {
                return; // cancelled
            }
            if (choice == true)
            {
                try
                {
                    await dirtyView.SaveInPlaceAsync();
                }
                catch (Exception ex)
                {
                    ShowError($"Save failed: {ex.Message}");
                    return;
                }
            }
        }
        CloseTab(tab);
    }

    /// <summary>true = save, false = discard, null = cancel.</summary>
    private async Task<bool?> PromptSaveChangesAsync(string name)
    {
        var dialog = new ContentDialog
        {
            Title = $"Save changes to {name}?",
            Content = "The document has unsaved annotations.",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => true,
            ContentDialogResult.Secondary => false,
            _ => null,
        };
    }

    private void CloseTab(TabViewItem tab)
    {
        if (tab.Tag is DocumentView view)
        {
            CaptureState(view);
            view.Viewer.LinkActivated -= Viewer_LinkActivated;
            view.Viewer.NoteRequested -= Viewer_NoteRequested;
            view.Close();
            _pendingRestore.Remove(view);
        }
        Tabs.TabItems.Remove(tab);
        UpdateStartPageVisibility();
    }

    private DocumentView? CurrentView =>
        (Tabs.SelectedItem as TabViewItem)?.Tag as DocumentView;

    private void UpdateStartPageVisibility()
    {
        bool hasTabs = Tabs.TabItems.Count > 0;
        StartPage.Visibility = hasTabs ? Visibility.Collapsed : Visibility.Visible;
        DocHost.Visibility = hasTabs ? Visibility.Visible : Visibility.Collapsed;
        // The document header + zoom pill are meaningless on the start page
        // (and would show stale page/zoom from the last-closed tab).
        Toolbar.Visibility = hasTabs ? Visibility.Visible : Visibility.Collapsed;
        ZoomPill.Visibility = hasTabs ? Visibility.Visible : Visibility.Collapsed;
        if (!hasTabs)
        {
            Title = "Rune";
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
        }

        _activeViewer = CurrentView?.Viewer;

        if (_activeViewer is not null)
        {
            _activeViewer.CurrentPageChanged += Viewer_CurrentPageChanged;
            _activeViewer.ZoomChanged += Viewer_ZoomChanged;
        }

        if (CurrentView is { } view)
        {
            string name = view.DisplayName;
            Title = $"{name} — Rune";
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
    }

    private void Viewer_ZoomChanged(object? sender, double zoom)
    {
        ZoomLabel.Text = $"{Math.Round(zoom * 100)}%";
        UpdateFitToggles();
    }

    private void UpdateToolbarForActive()
    {
        var view = CurrentView;
        bool ready = view is { IsDocumentLoaded: true, LoadError: null };
        var viewer = ready ? view!.Viewer : null;

        foreach (var control in new Control[]
                 {
                     SidebarButton, PageBox, FindButton, InkButton, NightButton,
                     ZoomInButton, ZoomOutButton, ZoomLabelButton,
                 })
        {
            control.IsEnabled = ready;
        }
        foreach (var item in new MenuFlyoutItemBase[]
                 {
                     SaveMenuItem, SaveAsMenuItem, PrintMenuItem, RotateMenuItem,
                     PropertiesMenuItem, InkOptionsMenuItem, PresentMenuItem,
                 })
        {
            item.IsEnabled = ready;
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
        ZoomLabel.Text = $"{Math.Round(viewer.Zoom * 100)}%";
        SidebarButton.IsChecked = view!.IsPaneOpen;
        InkButton.IsChecked = viewer.IsInkMode;
        UpdateFitToggles();
    }

    private void SetInkMode(bool on)
    {
        if (_activeViewer is not null)
        {
            _activeViewer.IsInkMode = on;
            InkButton.IsChecked = on;
        }
    }

    private static readonly (string Name, string Hex)[] InkColors =
    [
        ("Red", "#E22222"), ("Blue", "#2266DD"), ("Green", "#1E9E4A"), ("Black", "#000000"),
    ];
    private static readonly (string Name, double Width)[] InkWidths =
    [
        ("Thin", 1.5), ("Medium", 2.5), ("Thick", 4.5),
    ];

    /// <summary>(Re)fills the "Pen color and width" submenu in the main menu.</summary>
    private void BuildInkOptionsFlyout()
    {
        var flyout = InkOptionsMenuItem;
        flyout.Items.Clear();
        flyout.Items.Add(new MenuFlyoutItem { Text = "Pen color", IsEnabled = false });
        foreach (var (name, hex) in InkColors)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = name,
                IsChecked = string.Equals(_state.Settings.InkColor, hex, StringComparison.OrdinalIgnoreCase),
                Icon = new FontIcon { Glyph = "", Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(HexToColor(hex)) },
            };
            item.Click += (_, _) =>
            {
                _state.Settings.InkColor = hex;
                _store.Save(_state);
                ApplyInkStyleToAll();
            };
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(new MenuFlyoutItem { Text = "Width", IsEnabled = false });
        foreach (var (name, width) in InkWidths)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = name,
                IsChecked = Math.Abs(_state.Settings.InkWidth - width) < 0.01,
            };
            item.Click += (_, _) =>
            {
                _state.Settings.InkWidth = width;
                _store.Save(_state);
                ApplyInkStyleToAll();
            };
            flyout.Items.Add(item);
        }
    }

    private void ApplyInkStyleToAll()
    {
        foreach (var view in AllDocumentViews())
        {
            view.Viewer.SetInkStyle(_state.Settings.InkColor, _state.Settings.InkWidth);
        }
        BuildInkOptionsFlyout(); // refresh checkmarks
    }

    private static Color HexToColor(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromArgb(255,
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
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

    private void SidebarButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentView is { IsDocumentLoaded: true } view)
        {
            view.IsPaneOpen = !view.IsPaneOpen;
            SidebarButton.IsChecked = view.IsPaneOpen;
        }
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => _activeViewer?.ZoomIn();
    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => _activeViewer?.ZoomOut();
    private void RotateButton_Click(object sender, RoutedEventArgs e) => _activeViewer?.RotateClockwise();
    private void FitWidthButton_Click(object sender, RoutedEventArgs e) => SetFitMode(FitMode.FitWidth);
    private void FitPageButton_Click(object sender, RoutedEventArgs e) => SetFitMode(FitMode.FitPage);
    private void SaveButton_Click(object sender, RoutedEventArgs e) => _ = SaveActiveAsync();
    private void SaveAsButton_Click(object sender, RoutedEventArgs e) => _ = SaveAsActiveAsync();
    private void PropertiesButton_Click(object sender, RoutedEventArgs e) => _ = ShowPropertiesAsync();
    private void UpdatesButton_Click(object sender, RoutedEventArgs e) => _ = CheckForUpdatesAsync(userInitiated: true);
    private void InkButton_Click(object sender, RoutedEventArgs e) => SetInkMode(InkButton.IsChecked == true);
    private void FindButton_Click(object sender, RoutedEventArgs e) => ShowFindBar();
    private void PresentMenuItem_Click(object sender, RoutedEventArgs e) => TogglePresentation();

    // ---------------------------------------------------------------- presentation

    private void TogglePresentation()
    {
        if (Presentation.IsActive)
        {
            ExitPresentation();
            return;
        }
        if (_activeViewer is not { } viewer ||
            CurrentView is not { IsDocumentLoaded: true, LoadError: null })
        {
            return;
        }

        Presentation.ExitRequested -= Presentation_ExitRequested;
        Presentation.ExitRequested += Presentation_ExitRequested;
        Presentation.Show(viewer, _state.Settings.NightMode);
        AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
    }

    private void Presentation_ExitRequested(object? sender, EventArgs e) => ExitPresentation();

    private void ExitPresentation()
    {
        if (!Presentation.IsActive)
        {
            return;
        }
        int page = Presentation.CurrentPage;
        Presentation.Hide();
        AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
        // Land the reading view on the page the show ended on.
        _activeViewer?.GoToPage(page);
    }

    private void ZoomPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string factor } &&
            double.TryParse(factor, System.Globalization.CultureInfo.InvariantCulture, out double zoom))
        {
            _activeViewer?.SetZoom(zoom);
        }
    }

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
        FitWidthItem.IsChecked = _activeViewer?.FitMode == FitMode.FitWidth;
        FitPageItem.IsChecked = _activeViewer?.FitMode == FitMode.FitPage;
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

    // ---------------------------------------------------------------- annotations & save

    private void UpdateDirtyIndicator(DocumentView view)
    {
        foreach (var obj in Tabs.TabItems)
        {
            if (obj is TabViewItem { Tag: DocumentView v } tab && v == view)
            {
                tab.Header = view.IsDirty ? $"{view.DisplayName} •" : view.DisplayName;
                return;
            }
        }
    }

    private async void Viewer_NoteRequested(object? sender, (int PageIndex, double X, double Y) at)
    {
        if (CurrentView is not { } view || !ReferenceEquals(sender, view.Viewer))
        {
            return;
        }

        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 100,
            MinWidth = 360,
            PlaceholderText = "Note text…",
        };
        var dialog = new ContentDialog
        {
            Title = "Add note",
            Content = box,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
        {
            view.Viewer.AddNote(at.PageIndex, at.X, at.Y, box.Text.Trim());
        }
    }

    private async Task SaveActiveAsync()
    {
        if (CurrentView is not { IsDirty: true } view)
        {
            return;
        }
        try
        {
            await view.SaveInPlaceAsync();
            UpdateDirtyIndicator(view);
        }
        catch (Exception ex)
        {
            ShowError($"Save failed: {ex.Message}");
        }
    }

    private async Task SaveAsActiveAsync()
    {
        if (CurrentView is not { IsDocumentLoaded: true, LoadError: null } view || view.Viewer.Document is not { } document)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(view.FilePath),
        };
        picker.FileTypeChoices.Add("PDF document", [".pdf"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        if (string.Equals(file.Path, view.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            await SaveActiveAsync(); // picked the same file: in-place save
            return;
        }

        try
        {
            await Task.Run(() => document.SaveAs(file.Path));
            OpenOrActivate(file.Path);
        }
        catch (Exception ex)
        {
            ShowError($"Save As failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- night / print / properties / settings

    private void NightButton_Click(object sender, RoutedEventArgs e) => ToggleNightMode();

    private void ToggleNightMode()
    {
        _state.Settings.NightMode = !_state.Settings.NightMode;
        NightButton.IsChecked = _state.Settings.NightMode;
        foreach (var view in AllDocumentViews())
        {
            view.Viewer.NightMode = _state.Settings.NightMode;
        }
        _store.Save(_state);
    }

    private IEnumerable<DocumentView> AllDocumentViews() =>
        Tabs.TabItems.OfType<TabViewItem>().Select(t => t.Tag).OfType<DocumentView>();

    private void PrintButton_Click(object sender, RoutedEventArgs e) => _ = PrintAsync();

    private async Task PrintAsync()
    {
        if (CurrentView is not { IsDocumentLoaded: true, LoadError: null } view || view.Viewer.Document is not { } document)
        {
            return;
        }
        if (!PrintService.IsSupported)
        {
            ShowError("Printing is not supported on this device.");
            return;
        }

        try
        {
            _printService ??= new PrintService(WinRT.Interop.WindowNative.GetWindowHandle(this));
            await _printService.ShowAsync(document, $"{view.DisplayName} — Rune");
        }
        catch (Exception ex)
        {
            ShowError($"Printing failed: {ex.Message}");
        }
    }

    private async Task ShowPropertiesAsync()
    {
        if (CurrentView is not { IsDocumentLoaded: true, LoadError: null } view || view.Viewer.Document is not { } document)
        {
            return;
        }

        var properties = await Task.Run(document.GetProperties);

        var panel = new StackPanel { Spacing = 6, MinWidth = 360 };
        foreach (var (name, value) in properties)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = name, Opacity = 0.6, MinWidth = 110 });
            row.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, MaxWidth = 340, IsTextSelectionEnabled = true });
            panel.Children.Add(row);
        }

        await new ContentDialog
        {
            Title = view.DisplayName,
            Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
        }.ShowAsync();
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var themeBox = new ComboBox
        {
            ItemsSource = (string[])["System", "Light", "Dark"],
            SelectedItem = _state.Settings.Theme,
            MinWidth = 160,
        };
        var restoreCheck = new CheckBox { Content = "Reopen last session at startup", IsChecked = _state.Settings.RestoreSession };
        var sidebarCheck = new CheckBox { Content = "Show the sidebar when a document opens", IsChecked = _state.Settings.SidebarOpenByDefault };
        var thumbsCheck = new CheckBox { Content = "Show recent documents as thumbnails on the start page", IsChecked = _state.Settings.ShowRecentThumbnails };
        var vimCheck = new CheckBox { Content = "Keyboard navigation (j/k scroll, gg/G first/last page, n next hit)", IsChecked = _state.Settings.VimKeys };
        var updateCheck = new CheckBox { Content = "Check for updates automatically", IsChecked = _state.Settings.AutoCheckUpdates };

        var checkNowButton = new Button { Content = "Check for updates now" };
        checkNowButton.Click += async (_, _) => await CheckForUpdatesAsync(userInitiated: true);

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Theme", Opacity = 0.7 });
        panel.Children.Add(themeBox);
        panel.Children.Add(restoreCheck);
        panel.Children.Add(sidebarCheck);
        panel.Children.Add(thumbsCheck);
        panel.Children.Add(vimCheck);
        panel.Children.Add(updateCheck);
        panel.Children.Add(checkNowButton);
        panel.Children.Add(new TextBlock
        {
            Text = $"Rune {UpdateService.CurrentVersion.ToString(3)}",
            Opacity = 0.5,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });

        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _state.Settings.Theme = themeBox.SelectedItem as string ?? "System";
            _state.Settings.RestoreSession = restoreCheck.IsChecked == true;
            _state.Settings.SidebarOpenByDefault = sidebarCheck.IsChecked == true;
            _state.Settings.ShowRecentThumbnails = thumbsCheck.IsChecked == true;
            _state.Settings.VimKeys = vimCheck.IsChecked == true;
            _state.Settings.AutoCheckUpdates = updateCheck.IsChecked == true;
            ApplyTheme(_state.Settings.Theme);
            _store.Save(_state);
            PopulateRecents(); // reflect the thumbnails toggle immediately
        }
    }

    // ---------------------------------------------------------------- shortcuts overlay

    private void ShortcutsMenuItem_Click(object sender, RoutedEventArgs e) => _ = ShowShortcutsAsync();

    /// <summary>GNOME-style two-column shortcuts window, fed by <see cref="ShortcutCatalog"/>.</summary>
    private async Task ShowShortcutsAsync()
    {
        var grid = new Grid { ColumnSpacing = 40, MinWidth = 620 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var columns = new[] { new StackPanel { Spacing = 20 }, new StackPanel { Spacing = 20 } };
        Grid.SetColumn(columns[1], 1);
        grid.Children.Add(columns[0]);
        grid.Children.Add(columns[1]);

        var strongStyle = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
        var captionStyle = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        var keyBackground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlFillColorDefaultBrush"];

        // Flow groups into whichever column is currently shorter.
        var weight = new int[2];
        foreach (var group in ShortcutCatalog.Groups)
        {
            int target = weight[0] <= weight[1] ? 0 : 1;
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new TextBlock { Text = group.Title, Style = strongStyle });
            foreach (var shortcut in group.Shortcuts)
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var name = new TextBlock
                {
                    Text = shortcut.Name,
                    Opacity = 0.85,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                var keys = new Border
                {
                    Background = keyBackground,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(7, 3, 7, 3),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = shortcut.Keys, Style = captionStyle },
                };
                Grid.SetColumn(keys, 1);
                row.Children.Add(name);
                row.Children.Add(keys);
                panel.Children.Add(row);
            }
            columns[target].Children.Add(panel);
            weight[target] += group.Shortcuts.Length + 2;
        }

        await new ContentDialog
        {
            Title = "Keyboard shortcuts",
            Content = new ScrollViewer { Content = grid, MaxHeight = 540 },
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
        }.ShowAsync();
    }

    // ---------------------------------------------------------------- updates

    private readonly UpdateService _updater = new();

    /// <summary>Runs on launch (rate-limited) and from the Settings/palette "check now".</summary>
    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (!userInitiated)
        {
            if (!_state.Settings.AutoCheckUpdates ||
                (DateTime.UtcNow - _state.Settings.LastUpdateCheckUtc) < TimeSpan.FromHours(24))
            {
                return;
            }
        }

        _state.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
        _store.Save(_state);

        var update = await _updater.CheckAsync();
        if (update is null)
        {
            if (userInitiated)
            {
                await new ContentDialog
                {
                    Title = "You're up to date",
                    Content = $"Rune {UpdateService.CurrentVersion.ToString(3)} is the latest version.",
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot,
                }.ShowAsync();
            }
            return;
        }

        await ShowUpdateDialogAsync(update);
    }

    private async Task ShowUpdateDialogAsync(UpdateInfo update)
    {
        bool portable = UpdateService.IsPortable() && update.ZipUrl is not null;

        var notes = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(update.Notes) ? "" : update.Notes,
            TextWrapping = TextWrapping.Wrap,
        };
        var dialog = new ContentDialog
        {
            Title = $"Update available — Rune {update.Version.ToString(3)}",
            Content = new ScrollViewer { Content = notes, MaxHeight = 320, MinWidth = 420 },
            PrimaryButtonText = portable ? "Download and install" : "Open releases page",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (portable)
        {
            bool ok = await _updater.DownloadAndApplyAsync(update);
            if (ok)
            {
                _closeApproved = true;
                Close(); // apply-update.cmd waits for exit, swaps files, relaunches
            }
            else
            {
                await OpenReleasesPageFallbackAsync("Rune couldn't update in place (its folder may be read-only). Opening the releases page instead.");
            }
        }
        else
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(update.HtmlUrl));
        }
    }

    private async Task OpenReleasesPageFallbackAsync(string message)
    {
        await new ContentDialog
        {
            Title = "Manual update needed",
            Content = message,
            PrimaryButtonText = "Open releases page",
            CloseButtonText = "Cancel",
            XamlRoot = Content.XamlRoot,
        }.ShowAsync();
        await Windows.System.Launcher.LaunchUriAsync(new Uri(_updater.ReleasesPageUrl));
    }

    // ---------------------------------------------------------------- command palette

    private void ShowPalette()
    {
        var commands = new List<PaletteCommand>
        {
            new("Open file…", "Ctrl+O", () => OpenButton_Click(this, null!)),
            new("Keyboard shortcuts", "F1", () => _ = ShowShortcutsAsync()),
            new("Settings", "", () => SettingsButton_Click(this, null!)),
            new("Check for updates", "", () => _ = CheckForUpdatesAsync(userInitiated: true)),
        };

        if (_activeViewer is { } viewer && CurrentView is { IsDocumentLoaded: true, LoadError: null })
        {
            commands.AddRange(
            [
                new("Find in document", "Ctrl+F", ShowFindBar),
                new("Highlight selection", "Ctrl+H", () => viewer.MarkupSelection(MarkupKind.Highlight)),
                new("Draw (toggle pen)", "Ctrl+E", () => SetInkMode(!viewer.IsInkMode)),
                new("Save", "Ctrl+S", () => _ = SaveActiveAsync()),
                new("Save As…", "Ctrl+Shift+S", () => _ = SaveAsActiveAsync()),
                new("Print", "Ctrl+P", () => _ = PrintAsync()),
                new("Document properties", "Ctrl+D", () => _ = ShowPropertiesAsync()),
                new("Toggle night mode", "Ctrl+I", ToggleNightMode),
                new("Toggle sidebar", "F9", () => SidebarButton_Click(this, null!)),
                new("Presentation mode", "F5", TogglePresentation),
                new("Bookmark this page", "Ctrl+B", ToggleBookmark),
                new("Next page", "", () => viewer.GoToPage(viewer.CurrentPage + 1)),
                new("Previous page", "", () => viewer.GoToPage(viewer.CurrentPage - 1)),
                new("First page", "gg", () => viewer.GoToPage(0, recordHistory: true)),
                new("Last page", "G", () => viewer.GoToPage(viewer.PageCount - 1, recordHistory: true)),
                new("Zoom in", "Ctrl++", viewer.ZoomIn),
                new("Zoom out", "Ctrl+-", viewer.ZoomOut),
                new("Actual size", "Ctrl+1", () => viewer.SetZoom(1.0)),
                new("Fit width", "Ctrl+2", () => SetFitMode(FitMode.FitWidth)),
                new("Fit page", "Ctrl+0", () => SetFitMode(FitMode.FitPage)),
                new("Rotate clockwise", "Ctrl+R", viewer.RotateClockwise),
                new("Close tab", "Ctrl+W", CloseCurrentTab),
            ]);
        }

        foreach (var recent in _state.Recents.Take(8))
        {
            string path = recent.Path;
            commands.Add(new PaletteCommand($"Open recent: {recent.DisplayName}", "", () => OpenOrActivate(path)));
        }

        Palette.Show(commands, pageNumber =>
            _activeViewer is { PageCount: > 0 } v && pageNumber >= 1 && pageNumber <= v.PageCount
                ? new PaletteCommand($"Go to page {pageNumber}", "", () => v.GoToPage(pageNumber - 1, recordHistory: true))
                : null);
    }

    // ---------------------------------------------------------------- drag & drop

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Link;
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }
        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is Windows.Storage.StorageFile file &&
                file.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                OpenOrActivate(file.Path);
            }
        }
    }

    // ---------------------------------------------------------------- recents

    private readonly ThumbnailCache _thumbnails = new();

    /// <summary>How many cards the homepage grid shows (page-1 thumbnails are cached to disk).</summary>
    private const int MaxRecentCards = 18;

    private void PopulateRecents()
    {
        var recents = _state.Recents.Take(MaxRecentCards).ToList();
        bool any = recents.Count > 0;
        RecentsHeader.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        RecentThumbs.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;

        var cards = recents.Select(r => new RecentCard(r.Path, r.DisplayName)).ToList();
        RecentThumbs.ItemsSource = cards;

        // With thumbnails disabled the cards keep their document glyph; with
        // them enabled each card swaps its glyph for the page-1 render as the
        // (disk-cached) bitmap arrives.
        if (_state.Settings.ShowRecentThumbnails)
        {
            foreach (var card in cards)
            {
                _ = LoadThumbnailAsync(card);
            }
        }
    }

    private async Task LoadThumbnailAsync(RecentCard card)
    {
        byte[]? png = await _thumbnails.GetAsync(card.Path);
        if (png is null)
        {
            return;
        }
        // Decode on the UI thread into a BitmapImage.
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
        using (var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
        {
            using (var writer = new Windows.Storage.Streams.DataWriter(stream))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
        }
        card.Thumbnail = bitmap;
    }

    private void RecentThumbs_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecentCard card)
        {
            if (File.Exists(card.Path))
            {
                OpenOrActivate(card.Path);
            }
            else
            {
                ShowError($"File not found: {card.Path}");
                _state.Recents.RemoveAll(r => r.Path == card.Path);
                PopulateRecents();
            }
        }
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
        AddAccelerator(VirtualKey.F3, VirtualKeyModifiers.None, () => StepHit(+1));
        AddAccelerator(VirtualKey.F3, VirtualKeyModifiers.Shift, () => StepHit(-1));
        AddAccelerator(VirtualKey.I, VirtualKeyModifiers.Control, ToggleNightMode);
        AddAccelerator(VirtualKey.P, VirtualKeyModifiers.Control, () => _ = PrintAsync());
        AddAccelerator(VirtualKey.D, VirtualKeyModifiers.Control, () => _ = ShowPropertiesAsync());
        AddAccelerator(VirtualKey.H, VirtualKeyModifiers.Control, () => _activeViewer?.MarkupSelection(MarkupKind.Highlight));
        AddAccelerator(VirtualKey.E, VirtualKeyModifiers.Control, () => SetInkMode(!(_activeViewer?.IsInkMode ?? false)));
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control, () => _ = SaveActiveAsync());
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => _ = SaveAsActiveAsync());

        // Moved off the old CommandBar buttons when the header was slimmed down.
        AddAccelerator(VirtualKey.F9, VirtualKeyModifiers.None, () => SidebarButton_Click(this, null!));
        AddAccelerator(VirtualKey.R, VirtualKeyModifiers.Control, () => _activeViewer?.RotateClockwise());
        AddAccelerator(VirtualKey.B, VirtualKeyModifiers.Control, ToggleBookmark);

        // Available even with no document open.
        AddAccelerator(VirtualKey.O, VirtualKeyModifiers.Control, () => OpenButton_Click(this, null!), requiresDocument: false);
        AddAccelerator(VirtualKey.K, VirtualKeyModifiers.Control, ShowPalette, requiresDocument: false);
        AddAccelerator(VirtualKey.F1, VirtualKeyModifiers.None, () => _ = ShowShortcutsAsync(), requiresDocument: false);
        // Ctrl+? — GNOME's other shortcuts-window binding (Shift+/ = ? on US layouts).
        AddAccelerator((VirtualKey)0xBF, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            () => _ = ShowShortcutsAsync(), requiresDocument: false);
        AddAccelerator(VirtualKey.F5, VirtualKeyModifiers.None, TogglePresentation);
        AddAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None, () =>
        {
            if (Presentation.IsActive)
            {
                ExitPresentation();
            }
            else if (Palette.IsOpen)
            {
                Palette.Hide();
            }
            else
            {
                HideFindBar();
            }
        }, requiresDocument: false);

        // Ctrl+C must fall through to focused text boxes (find box, page box).
        AddAccelerator(VirtualKey.C, VirtualKeyModifiers.Control, CopySelection, skipWhenTextInputFocused: true);
    }

    private void AddAccelerator(
        VirtualKey key, VirtualKeyModifiers modifiers, Action action,
        bool requiresDocument = true, bool skipWhenTextInputFocused = false)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            if (skipWhenTextInputFocused && IsTextInputFocused())
            {
                return; // leave args.Handled false so the text box gets the key
            }
            if (!requiresDocument || _activeViewer is not null)
            {
                action();
                args.Handled = true;
            }
        };
        ((UIElement)Content).KeyboardAccelerators.Add(accelerator);
    }

    private bool IsTextInputFocused() =>
        FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or NumberBox or AutoSuggestBox or PasswordBox;

    // ---------------------------------------------------------------- vim-style keys

    private void Content_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || _activeViewer is null || Palette.IsOpen || IsTextInputFocused())
        {
            return;
        }

        bool shift = IsKeyDown(VirtualKey.Shift);
        if (IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu))
        {
            return; // modified combos belong to the KeyboardAccelerators
        }

        // Space pages here in the BUBBLING phase (not PreviewKeyDown) so a
        // focused button keeps its Space-to-activate accessibility behavior.
        if (e.Key == VirtualKey.Space)
        {
            _activeViewer.ScrollByViewport(shift ? -0.9 : +0.9);
            e.Handled = true;
            return;
        }

        if (!_state.Settings.VimKeys)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.J:
                _activeViewer.ScrollByLines(+3);
                break;
            case VirtualKey.K:
                _activeViewer.ScrollByLines(-3);
                break;
            case VirtualKey.H:
                _activeViewer.ScrollHorizontally(-1);
                break;
            case VirtualKey.L:
                _activeViewer.ScrollHorizontally(+1);
                break;
            case VirtualKey.N:
                // Next search hit while a search is active, else next page.
                if (_searchHits.Count > 0)
                {
                    StepHit(shift ? -1 : +1);
                }
                else if (!shift)
                {
                    _activeViewer.GoToPage(_activeViewer.CurrentPage + 1);
                }
                break;
            case VirtualKey.P:
                _activeViewer.GoToPage(_activeViewer.CurrentPage - 1);
                break;
            case VirtualKey.G when shift:
                _activeViewer.GoToPage(_activeViewer.PageCount - 1, recordHistory: true);
                break;
            case VirtualKey.G:
                // "gg" = go to first page (two presses within 500 ms).
                if ((DateTime.UtcNow - _lastGPress).TotalMilliseconds < 500)
                {
                    _activeViewer.GoToPage(0, recordHistory: true);
                    _lastGPress = DateTime.MinValue;
                }
                else
                {
                    _lastGPress = DateTime.UtcNow;
                }
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>
    /// Standard navigation — always on, matching Evince/GNOME Papers: arrows
    /// scroll/page, PageUp/Down step viewports, Home/End jump. Tunneling so
    /// the tab strip and toolbar can't swallow the keys; text inputs and the
    /// sidebar's own lists are explicitly excluded.
    /// </summary>
    private void Content_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        // Presentation mode owns the keyboard while active (Esc/F5 exit via
        // their accelerators).
        if (Presentation.IsActive)
        {
            switch (e.Key)
            {
                case VirtualKey.Right:
                case VirtualKey.Down:
                case VirtualKey.Space:
                case VirtualKey.PageDown:
                    Presentation.Next();
                    e.Handled = true;
                    break;
                case VirtualKey.Left:
                case VirtualKey.Up:
                case VirtualKey.PageUp:
                    Presentation.Prev();
                    e.Handled = true;
                    break;
            }
            return;
        }

        if (_activeViewer is null || Palette.IsOpen || IsTextInputFocused())
        {
            return;
        }
        if (IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu))
        {
            return;
        }
        // Sidebar thumbnails/outline/bookmarks keep their own arrow navigation.
        if (FocusManager.GetFocusedElement(Content.XamlRoot)
            is Microsoft.UI.Xaml.Controls.Primitives.SelectorItem or TreeViewItem)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Up:
                _activeViewer.ScrollByLines(-3);
                break;
            case VirtualKey.Down:
                _activeViewer.ScrollByLines(+3);
                break;
            case VirtualKey.Left:
                _activeViewer.GoToPage(_activeViewer.CurrentPage - 1);
                break;
            case VirtualKey.Right:
                _activeViewer.GoToPage(_activeViewer.CurrentPage + 1);
                break;
            case VirtualKey.PageUp:
                _activeViewer.ScrollByViewport(-0.9);
                break;
            case VirtualKey.PageDown:
                _activeViewer.ScrollByViewport(+0.9);
                break;
            case VirtualKey.Home:
                _activeViewer.GoToPage(0, recordHistory: true);
                break;
            case VirtualKey.End:
                _activeViewer.GoToPage(_activeViewer.PageCount - 1, recordHistory: true);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private static bool IsKeyDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

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
        if (CurrentView is not { IsDocumentLoaded: true })
        {
            return;
        }
        FindBar.Visibility = Visibility.Visible;
        // Prefill from the selection BEFORE SelectAll, so typing replaces it.
        if (_activeViewer?.HasSelection == true)
        {
            FindBox.Text = _activeViewer.SelectedText.Split('\n')[0].Trim();
        }
        FindBox.Focus(FocusState.Programmatic);
        FindBox.SelectAll();
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
            // Route each page's search through the viewer's render thread at
            // Background priority: visible tiles always outrank the sweep.
            var search = new DocumentSearch(document, query, matchCase, wholeWord: false,
                workQueue: viewer.WorkQueue);

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
