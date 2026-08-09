using Rune.Controls;
using Rune.Engine;
using Rune.Services;
using Rune.Styles;
using Microsoft.UI;
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

        // With the theme set to "System", flipping Windows' theme changes
        // ActualTheme without going through ApplyTheme — the caption buttons
        // have to be re-coloured or they keep the old glyph colour.
        ((FrameworkElement)Content).ActualThemeChanged += (s, _) => ApplyThemeToChrome(s.ActualTheme);

        RegisterAccelerators();
        ((UIElement)Content).KeyDown += Content_KeyDown;
        // Tunneling handler: navigation keys must reach the document even when
        // focus sits on the tab strip or a toolbar button (those controls eat
        // arrow keys in the bubbling phase for their own focus movement).
        ((UIElement)Content).PreviewKeyDown += Content_PreviewKeyDown;
        // The pen panel is built lazily on first use (BuildInkFlyout).

        // handledEventsToo, because TabViewItem marks the press handled for its
        // own selection before it would ever bubble to the TabView. Declaring
        // this in XAML silently never fires.
        Tabs.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(Tabs_PointerPressed), handledEventsToo: true);

        PopulateRecents();

        Activated += MainWindow_FirstActivated;
        Closed += MainWindow_Closed;
        AppWindow.Closing += AppWindow_Closing;
    }

    private bool _closeApproved;

    private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        try
        {
            if (_closeApproved || AllDocumentViews().All(v => !v.IsDirty))
            {
                return;
            }

            args.Cancel = true; // must be set before any await
            if (await EnsureSafeToCloseAsync())
            {
                _closeApproved = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            // async void: an escape here would kill the app mid-close.
            ErrorLog.Default.Write("AppWindow_Closing", ex);
            ShowError($"Couldn't close cleanly: {ex.Message}");
        }
    }

    /// <summary>
    /// Prompts for unsaved documents and saves them if asked. Returns false when
    /// the user cancelled or a save failed — i.e. it is NOT safe to close.
    /// Shared by window close and the self-update path (which also ends in Close).
    /// </summary>
    private async Task<bool> EnsureSafeToCloseAsync()
    {
        var dirty = AllDocumentViews().Where(v => v.IsDirty).ToList();
        if (dirty.Count == 0)
        {
            return true;
        }

        var choice = await PromptSaveChangesAsync(
            dirty.Count == 1 ? dirty[0].DisplayName : $"{dirty.Count} documents");
        if (choice is null)
        {
            return false; // cancelled
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
                    return false;
                }
            }
        }
        return true;
    }

    private void ApplyTheme(string theme)
    {
        var root = (FrameworkElement)Content;
        root.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        ApplyThemeToChrome(root.ActualTheme);
    }

    /// <summary>
    /// Colours the window's caption buttons to match the app's theme.
    ///
    /// Without this they follow the OS: because the theme is set on
    /// <c>Window.Content</c> rather than <c>Application.RequestedTheme</c>,
    /// choosing Dark inside Rune on a light-mode Windows leaves black glyphs
    /// on Rune's dark title bar (and vice versa). It is the most visible
    /// cross-theme inconsistency in the app.
    /// </summary>
    private void ApplyThemeToChrome(ElementTheme resolved)
    {
        // ElementTheme.Default means "follow the app", so resolve it here
        // rather than leaving the caption buttons on a different axis.
        bool dark = resolved == ElementTheme.Default
            ? Application.Current.RequestedTheme == ApplicationTheme.Dark
            : resolved == ElementTheme.Dark;

        var titleBar = AppWindow.TitleBar;

        // MUST stay transparent: an opaque button background paints over the
        // Mica backdrop behind the tab strip.
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        titleBar.ButtonForegroundColor = RuneColors.CaptionForeground(dark);
        titleBar.ButtonInactiveForegroundColor = RuneColors.CaptionInactiveForeground(dark);
        titleBar.ButtonHoverForegroundColor = RuneColors.CaptionForeground(dark);
        titleBar.ButtonPressedForegroundColor = RuneColors.CaptionForeground(dark);
        titleBar.ButtonHoverBackgroundColor = RuneColors.CaptionHoverBackground(dark);
        titleBar.ButtonPressedBackgroundColor = RuneColors.CaptionPressedBackground(dark);
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
        try
        {
            await RestoreSessionAsync();
        }
        catch (Exception ex)
        {
            // async void: this is the app's most dangerous one — an escape here
            // kills the process during startup.
            ErrorLog.Default.Write("FirstActivated", ex);
            ShowError($"Startup problem: {ex.Message}");
        }
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
        _thumbnails.Dispose(); // stops the homepage cache's PDFium thread
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
        SeedToolStyles(view.Viewer);
        view.Viewer.DocumentEdited += (_, _) => UpdateDirtyIndicator(view);
        view.Viewer.ActiveToolChanged += (_, tool) => SyncToolButtons(tool);
        view.Viewer.SignaturePlaced += (_, widthPt) =>
        {
            _state.Settings.SignatureWidthPt = widthPt;
            _store.Save(_state);
        };
        view.SignaturesRead += (_, _) => UpdateToolbarForActive();
        view.Viewer.NoteRequested += Viewer_NoteRequested;
        view.BookmarksChanged += (_, _) => PersistBookmarks(view);
        view.PagesEdited += (_, _) =>
        {
            UpdateDirtyIndicator(view);
            if (view == CurrentView)
            {
                UpdateToolbarForActive(); // page count / current page changed
            }
        };
        view.PageOpFailed += (_, message) => ShowError(message);
        view.ExtractRequested += (_, _) => _ = ExtractPagesAsync(view);
        view.UndoStateChanged += (_, _) =>
        {
            if (view == CurrentView)
            {
                UpdateUndoMenu();
            }
        };
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
        _showHome = false; // picking a tab is a request to read it
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
        return await ShowDialogAsync(dialog) switch
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

    /// <summary>
    /// True while the Home button is showing the recents screen over open tabs.
    /// Without this the start page would only ever appear with no tabs at all.
    /// </summary>
    private bool _showHome;

    /// <summary>
    /// The wordmark toggles Home. Tabs stay open the whole time, so this is a
    /// view switch, not a close — and clicking any tab also returns you.
    /// </summary>
    private void HomeButton_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.TabItems.Count == 0)
        {
            return; // already home; nothing to toggle back to
        }
        _showHome = !_showHome;
        UpdateStartPageVisibility();
        if (!_showHome)
        {
            UpdateToolbarForActive();
        }
    }

    /// <summary>
    /// Any press on the tab strip leaves Home for that tab.
    ///
    /// <see cref="Tabs_SelectionChanged"/> already clears the flag, but it only
    /// fires when the selection actually changes — so clicking the tab that is
    /// already selected did nothing, and with a single document open that is
    /// every tab. The wordmark was the only way back, which is not where anyone
    /// looks for it.
    /// </summary>
    private void Tabs_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_showHome && Tabs.TabItems.Count > 0)
        {
            _showHome = false;
            UpdateStartPageVisibility();
            UpdateToolbarForActive();
        }
    }

    private void UpdateStartPageVisibility()
    {
        bool hasTabs = Tabs.TabItems.Count > 0;
        bool showDocument = hasTabs && !_showHome;

        StartPage.Visibility = showDocument ? Visibility.Collapsed : Visibility.Visible;
        DocHost.Visibility = showDocument ? Visibility.Visible : Visibility.Collapsed;
        // The document header + zoom pill are meaningless on the start page
        // (and would show stale page/zoom from a document you aren't looking at).
        Toolbar.Visibility = showDocument ? Visibility.Visible : Visibility.Collapsed;
        ZoomPill.Visibility = showDocument ? Visibility.Visible : Visibility.Collapsed;
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
            _activeViewer.InkStrokeStarted -= Viewer_InkStrokeStarted;
        }

        _activeViewer = CurrentView?.Viewer;

        if (_activeViewer is not null)
        {
            _activeViewer.CurrentPageChanged += Viewer_CurrentPageChanged;
            _activeViewer.ZoomChanged += Viewer_ZoomChanged;
            _activeViewer.InkStrokeStarted += Viewer_InkStrokeStarted;
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

    /// <summary>Drawing started — get the tool's options panel out of the way.</summary>
    private void Viewer_InkStrokeStarted(object? sender, EventArgs e) => HideToolOptions();

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
                     SidebarButton, PageBox, FindButton, NightButton,
                     ZoomInButton, ZoomOutButton, ZoomLabelButton,
                     FitWidthButton, FitPageButton, RotateLeftButton, RotateRightButton,
                     // The annotation cluster. A tool button left out of this
                     // list stays permanently greyed out — nothing else enables it.
                     PenToolButton, HighlighterToolButton, NoteToolButton,
                     SignToolButton, EraserToolButton,
                 })
        {
            control.IsEnabled = ready;
        }
        foreach (var item in new MenuFlyoutItemBase[]
                 {
                     SaveMenuItem, SaveAsMenuItem, PrintMenuItem,
                     PropertiesMenuItem, PresentMenuItem,
                 })
        {
            item.IsEnabled = ready;
        }

        if (viewer is null)
        {
            PageCountLabel.Text = "";
            FlattenMenuItem.IsEnabled = false;
            SignaturesMenuItem.IsEnabled = false;
            return;
        }

        // Deliberately cheap. Asking the document what it contains would mean
        // PDFium calls on the UI thread — HasFlattenableContent() alone walks
        // every page — and this runs on every tab switch. Flatten stays
        // available and reports "nothing to flatten"; the signature count is
        // read once at load time (DocumentView.HasSignatures).
        FlattenMenuItem.IsEnabled = ready;
        SignaturesMenuItem.IsEnabled = ready && CurrentView?.HasSignatures == true;
        UpdateDocumentNotice(viewer);

        _suppressPageBox = true;
        PageBox.Maximum = viewer.PageCount;
        PageBox.Value = viewer.CurrentPage + 1;
        _suppressPageBox = false;
        PageCountLabel.Text = $"of {viewer.PageCount}";
        ZoomLabel.Text = $"{Math.Round(viewer.Zoom * 100)}%";
        SidebarButton.IsChecked = view!.IsPaneOpen;
        SyncToolButtons(viewer.ActiveTool);
        UpdateFitToggles();
        UpdateUndoMenu();
    }

    /// <summary>
    /// Arms a tool (or disarms with <see cref="AnnotationTool.None"/>).
    ///
    /// Tools are mutually exclusive, so this is the single place the armed tool
    /// changes — the buttons are only a reflection of the viewer's state, never
    /// the source of truth, which is what keeps them correct across tab
    /// switches.
    /// </summary>
    private void SetActiveTool(AnnotationTool tool)
    {
        if (_activeViewer is null)
        {
            return;
        }
        _activeViewer.ActiveTool = tool;
        SyncToolButtons(tool);
        if (tool == AnnotationTool.None)
        {
            HideToolOptions(); // covers Esc/Ctrl+E while a panel is open
        }
    }

    /// <summary>Reflects the armed tool onto the cluster. Never sets it.</summary>
    private void SyncToolButtons(AnnotationTool tool)
    {
        PenToolButton.IsChecked = tool == AnnotationTool.Pen;
        HighlighterToolButton.IsChecked = tool == AnnotationTool.Highlighter;
        NoteToolButton.IsChecked = tool == AnnotationTool.Note;
        SignToolButton.IsChecked = tool == AnnotationTool.Signature;
        EraserToolButton.IsChecked = tool == AnnotationTool.Eraser;
    }

    /// <summary>
    /// A tool button arms its tool and shows its options. Clicking the armed
    /// tool again just re-opens the panel rather than disarming — matching the
    /// old pen button, where accidentally turning drawing off mid-session was
    /// the more annoying failure. Esc disarms.
    /// </summary>
    private void ToolButton_Click(AnnotationTool tool)
    {
        SetActiveTool(tool);
        ShowToolOptions(tool);
    }

    private void PenToolButton_Click(object sender, RoutedEventArgs e) => ToolButton_Click(AnnotationTool.Pen);
    private void HighlighterToolButton_Click(object sender, RoutedEventArgs e) => ToolButton_Click(AnnotationTool.Highlighter);
    private void NoteToolButton_Click(object sender, RoutedEventArgs e) => ToolButton_Click(AnnotationTool.Note);
    private void SignToolButton_Click(object sender, RoutedEventArgs e) => ToolButton_Click(AnnotationTool.Signature);
    private void EraserToolButton_Click(object sender, RoutedEventArgs e) => ToolButton_Click(AnnotationTool.Eraser);

    // The per-tool options panels live in MainWindow.Tools.cs.

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
    private void RotateLeftButton_Click(object sender, RoutedEventArgs e) => _activeViewer?.RotateCounterClockwise();
    private void FitWidthButton_Click(object sender, RoutedEventArgs e) => SetFitMode(FitMode.FitWidth);
    private void FitPageButton_Click(object sender, RoutedEventArgs e) => SetFitMode(FitMode.FitPage);
    private void SaveButton_Click(object sender, RoutedEventArgs e) => _ = SaveActiveAsync();
    private void SaveAsButton_Click(object sender, RoutedEventArgs e) => _ = SaveAsActiveAsync();
    private void PropertiesButton_Click(object sender, RoutedEventArgs e) => _ = ShowPropertiesAsync();
    private void FindButton_Click(object sender, RoutedEventArgs e) => ShowFindBar();
    private void PresentMenuItem_Click(object sender, RoutedEventArgs e) => TogglePresentation();
    private void UndoMenuItem_Click(object sender, RoutedEventArgs e) => _ = CurrentView?.UndoAsync();
    private void RedoMenuItem_Click(object sender, RoutedEventArgs e) => _ = CurrentView?.RedoAsync();

    /// <summary>Reflects the active view's undo/redo availability and labels onto the menu.</summary>
    private void UpdateUndoMenu()
    {
        var view = CurrentView;
        UndoMenuItem.IsEnabled = view?.CanUndo == true;
        RedoMenuItem.IsEnabled = view?.CanRedo == true;
        UndoMenuItem.Text = view?.UndoLabel is { } u ? $"Undo {u}" : "Undo";
        RedoMenuItem.Text = view?.RedoLabel is { } r ? $"Redo {r}" : "Redo";
    }

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

    /// <summary>Keeps the header toggles and the zoom-pill menu items in step.</summary>
    private void UpdateFitToggles()
    {
        bool fitWidth = _activeViewer?.FitMode == FitMode.FitWidth;
        bool fitPage = _activeViewer?.FitMode == FitMode.FitPage;
        FitWidthButton.IsChecked = fitWidth;
        FitPageButton.IsChecked = fitPage;
        FitWidthItem.IsChecked = fitWidth;   // ToggleMenuFlyoutItem.IsChecked is bool,
        FitPageItem.IsChecked = fitPage;     // ToggleButton.IsChecked is bool? — assign separately
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
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
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

        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
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
            await view.Viewer.RunOnRenderThreadAsync(PdfWorkPriority.Interactive, () =>
            {
                document.SaveAs(file.Path);
                return true;
            });
            OpenOrActivate(file.Path);
        }
        catch (Exception ex)
        {
            ShowError($"Save As failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Palette route into extract. The palette entry is offered whenever a
    /// document is open, so it has to say what to do when no pages are picked
    /// rather than appearing to do nothing.
    /// </summary>
    private async Task ExtractSelectedPagesFromPaletteAsync()
    {
        if (CurrentView is not { IsDocumentLoaded: true } view)
        {
            return;
        }
        if (view.SelectedPageCount == 0)
        {
            ShowNotice(
                "Select pages in the thumbnail sidebar first, then extract them.",
                InfoBarSeverity.Informational);
            return;
        }
        await ExtractPagesAsync(view);
    }

    /// <summary>
    /// Writes the thumbnail selection out as a new PDF. The picker lives here
    /// because it needs the window handle; the work itself is DocumentView's.
    /// The current document is left alone — extract is a copy, not a cut.
    /// </summary>
    private async Task ExtractPagesAsync(DocumentView view)
    {
        if (view.SelectedPageCount == 0 || !view.IsDocumentLoaded)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedFileName = $"{Path.GetFileNameWithoutExtension(view.FilePath)} {view.SelectedPageRangeLabel()}",
        };
        picker.FileTypeChoices.Add("PDF document", [".pdf"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        // Writing over the document being read from would pull the file out from
        // under the open handle mid-export.
        if (string.Equals(file.Path, view.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            ShowError("Pick a different file: extracting over the open document would overwrite it.");
            return;
        }

        int count = view.SelectedPageCount;
        if (await view.ExtractSelectedPagesAsync(file.Path))
        {
            // Not ShowError: this succeeded, so it gets success semantics.
            ShowNotice(
                $"Extracted {count} page{(count == 1 ? "" : "s")} to {file.Name}.",
                InfoBarSeverity.Success);
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
            await _printService.ShowAsync(document, $"{view.DisplayName} — Rune", view.Viewer.WorkQueue);
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

        var properties = await view.Viewer.RunOnRenderThreadAsync(PdfWorkPriority.Interactive, document.GetProperties);

        var panel = new StackPanel { Spacing = 6, MinWidth = 360 };
        foreach (var (name, value) in properties)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = name, Opacity = 0.6, MinWidth = 110 });
            row.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, MaxWidth = 340, IsTextSelectionEnabled = true });
            panel.Children.Add(row);
        }

        await ShowDialogAsync(new ContentDialog
        {
            Title = view.DisplayName,
            Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
            CloseButtonText = "Close",
        });
    }

    private void ReportProblemMenuItem_Click(object sender, RoutedEventArgs e) => _ = ShowReportProblemAsync();

    /// <summary>
    /// Rune has no telemetry and makes no network requests, by design. That
    /// means a crash reaches nobody unless the user carries it out by hand, and
    /// until now nothing in the app said where the log even was. This shows the
    /// details a useful report needs and opens the two places to get them.
    /// </summary>
    private async Task ShowReportProblemAsync()
    {
        string logPath = ErrorLog.Default.Path_;
        bool haveLog = File.Exists(logPath);

        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Rune collects nothing and sends nothing anywhere, so bug reports only "
                 + "arrive if you send them. Open an issue on GitHub and paste in the details below.",
        });

        // Selectable, because the point is to paste this into an issue.
        var details = new StackPanel { Spacing = 4 };
        foreach (var (label, value) in new (string, string)[]
        {
            ("Rune", $"{CurrentVersion} ({(IsPackaged ? "Store" : "portable")})"),
            ("Windows", Environment.OSVersion.Version.ToString()),
            ("Architecture", System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString()),
        })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = label, Opacity = 0.6, MinWidth = 110 });
            row.Children.Add(new TextBlock { Text = value, IsTextSelectionEnabled = true });
            details.Children.Add(row);
        }
        panel.Children.Add(details);

        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Text = haveLog
                ? $"Errors are logged to {logPath}. Attach it if the problem was a crash."
                : $"Nothing has been logged yet. If Rune misbehaves, errors appear in {logPath}.",
        });

        var dialog = new ContentDialog
        {
            Title = "Report a problem",
            Content = panel,
            PrimaryButtonText = "Open issue tracker",
            SecondaryButtonText = haveLog ? "Show the log" : null,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.Primary)
        {
            await Launcher.LaunchUriAsync(new Uri("https://github.com/DanialJaved/rune/issues/new"));
        }
        else if (result == ContentDialogResult.Secondary && haveLog)
        {
            // Select the file in Explorer rather than opening it: the log is
            // plain text, but what the user needs is to find and attach it.
            await Launcher.LaunchFolderPathAsync(Path.GetDirectoryName(logPath));
        }
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

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Theme", Opacity = 0.7 });
        panel.Children.Add(themeBox);
        panel.Children.Add(restoreCheck);
        panel.Children.Add(sidebarCheck);
        panel.Children.Add(thumbsCheck);
        panel.Children.Add(vimCheck);
        panel.Children.Add(new TextBlock
        {
            Text = $"Rune {CurrentVersion}",
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
        };

        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
        {
            _state.Settings.Theme = themeBox.SelectedItem as string ?? "System";
            _state.Settings.RestoreSession = restoreCheck.IsChecked == true;
            _state.Settings.SidebarOpenByDefault = sidebarCheck.IsChecked == true;
            _state.Settings.ShowRecentThumbnails = thumbsCheck.IsChecked == true;
            _state.Settings.VimKeys = vimCheck.IsChecked == true;
            ApplyTheme(_state.Settings.Theme);
            _store.Save(_state);
            PopulateRecents(); // reflect the thumbnails toggle immediately
        }
    }

    /// <summary>The running build's version, for the Settings footer.</summary>
    private static string CurrentVersion =>
        (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0)).ToString(3);

    /// <summary>
    /// True for the MSIX (Store) build, false for the portable zip. Worth
    /// reporting in a bug: the two differ in how they are installed and
    /// updated, and only one of them is signed.
    /// <c>Package.Current</c> throws when there is no package identity, which
    /// is the documented way to ask.
    /// </summary>
    private static bool IsPackaged
    {
        get
        {
            try
            {
                return Windows.ApplicationModel.Package.Current is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    // ---------------------------------------------------------------- shortcuts overlay

    private void ShortcutsMenuItem_Click(object sender, RoutedEventArgs e) => _ = ShowShortcutsAsync();

    /// <summary>GNOME-style two-column shortcuts window, fed by <see cref="ShortcutCatalog"/>.</summary>
    private async Task ShowShortcutsAsync()
    {
        // Fixed width rather than MinWidth: a minimum only states what the grid
        // will not go below, so when the dialog's content area came out narrower
        // the grid overflowed and the second column's key chips were clipped off
        // the right edge — the overlay listed half its actions with no keys
        // beside them. Pinning both this and the dialog's MaxWidth means the
        // content area is known to be wider than the content.
        var grid = new Grid { ColumnSpacing = 40, Width = 760 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var columns = new[] { new StackPanel { Spacing = 20 }, new StackPanel { Spacing = 20 } };
        Grid.SetColumn(columns[1], 1);
        grid.Children.Add(columns[0]);
        grid.Children.Add(columns[1]);

        var strongStyle = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
        var captionStyle = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        // Styles, not brushes: resolving a theme brush here would hand light
        // mode the dark-theme fill (PROJECT.md §7).
        var keyChipStyle = (Style)Application.Current.Resources["ShortcutKeyChipStyle"];
        var secondaryTextStyle = (Style)Application.Current.Resources["SecondaryTextStyle"];

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
                    Style = secondaryTextStyle,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                var keys = new Border
                {
                    Style = keyChipStyle,
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

        var dialog = new ContentDialog
        {
            Title = "Keyboard shortcuts",
            Content = new ScrollViewer
            {
                Content = grid,
                MaxHeight = 540,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            CloseButtonText = "Close",
        };
        // A ContentDialog's width comes from the ContentDialogMaxWidth theme
        // resource, NOT from the MaxWidth property — setting the property alone
        // leaves the default ~548px cap in force, the content overflows it, and
        // the overflow is clipped. That is what cut the key chips off the second
        // column. Overriding the resource on this instance is the supported way
        // to widen one dialog without touching every other.
        dialog.Resources["ContentDialogMaxWidth"] = 880.0;
        await ShowDialogAsync(dialog);
    }

    // ---------------------------------------------------------------- command palette

    private void ShowPalette()
    {
        var commands = new List<PaletteCommand>
        {
            new("Open file…", "Ctrl+O", () => OpenButton_Click(this, null!)),
            new("Keyboard shortcuts", "F1", () => _ = ShowShortcutsAsync()),
            new("Settings", "", () => SettingsButton_Click(this, null!)),
        };

        if (_activeViewer is { } viewer && CurrentView is { IsDocumentLoaded: true, LoadError: null })
        {
            commands.AddRange(
            [
                new("Find in document", "Ctrl+F", ShowFindBar),
                new("Highlight selection", "Ctrl+H", () => viewer.MarkupSelection(MarkupKind.Highlight)),
                new("Draw (toggle pen)", "Ctrl+E", TogglePenTool),
                new("Highlighter tool", "", () => SetActiveTool(AnnotationTool.Highlighter)),
                new("Eraser tool", "", () => SetActiveTool(AnnotationTool.Eraser)),
                new("Save", "Ctrl+S", () => _ = SaveActiveAsync()),
                new("Save As…", "Ctrl+Shift+S", () => _ = SaveAsActiveAsync()),
                new("Extract selected pages to a new file…", "", () => _ = ExtractSelectedPagesFromPaletteAsync()),
                new("Print", "Ctrl+P", () => _ = PrintAsync()),
                new("Document properties", "Ctrl+D", () => _ = ShowPropertiesAsync()),
                new("Toggle night mode", "Ctrl+I", ToggleNightMode),
                new("Toggle sidebar", "F9", () => SidebarButton_Click(this, null!)),
                new("Presentation mode", "F5", TogglePresentation),
                new("Bookmark this page", "Ctrl+B", ToggleBookmark),
                new("Undo", "Ctrl+Z", () => _ = CurrentView?.UndoAsync()),
                new("Redo", "Ctrl+Y", () => _ = CurrentView?.RedoAsync()),
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
        // The card sizes its page box from the bitmap's pixel dimensions, which
        // are only known once decoding finishes. SetSourceAsync normally
        // completes after decode; re-assign on ImageOpened in case it doesn't.
        bitmap.ImageOpened += (_, _) => card.Thumbnail = bitmap;
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

    /// <summary>
    /// Right-click on a recent card: forget this one, or all of them.
    ///
    /// Removal also deletes the cached first-page thumbnail. That PNG is a
    /// picture of the document, so leaving it on disk would keep the contents of
    /// a file the user just asked Rune to forget.
    /// </summary>
    private void RecentCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RecentCard card })
        {
            return;
        }
        e.Handled = true;

        var forget = new MenuFlyoutItem { Text = "Remove from recents" };
        // Bookmarks hang off the recent entry, so forgetting the file forgets
        // them. Say so rather than discovering it later.
        int bookmarks = _state.FindRecent(card.Path)?.Bookmarks.Count ?? 0;
        if (bookmarks > 0)
        {
            forget.Text = bookmarks == 1
                ? "Remove from recents (discards 1 bookmark)"
                : $"Remove from recents (discards {bookmarks} bookmarks)";
        }
        forget.Click += (_, _) =>
        {
            _state.ForgetRecent(card.Path);
            _thumbnails.Forget(card.Path);
            _store.Save(_state);
            PopulateRecents();
        };

        var forgetAll = new MenuFlyoutItem { Text = "Clear all recent documents" };
        forgetAll.Click += (_, _) =>
        {
            _state.ForgetAllRecents();
            _thumbnails.ForgetAll();
            _store.Save(_state);
            PopulateRecents();
        };

        var flyout = new MenuFlyout();
        flyout.Items.Add(forget);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(forgetAll);
        var target = (FrameworkElement)sender;
        flyout.ShowAt(target, e.GetPosition(target));
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
        AddAccelerator(VirtualKey.E, VirtualKeyModifiers.Control, TogglePenTool);
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control, () => _ = SaveActiveAsync());
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => _ = SaveAsActiveAsync());

        // Moved off the old CommandBar buttons when the header was slimmed down.
        AddAccelerator(VirtualKey.F9, VirtualKeyModifiers.None, () => SidebarButton_Click(this, null!));
        AddAccelerator(VirtualKey.R, VirtualKeyModifiers.Control, () => _activeViewer?.RotateClockwise());
        AddAccelerator(VirtualKey.R, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            () => _activeViewer?.RotateCounterClockwise());
        AddAccelerator(VirtualKey.B, VirtualKeyModifiers.Control, ToggleBookmark);
        AddAccelerator(VirtualKey.Z, VirtualKeyModifiers.Control, () => _ = CurrentView?.UndoAsync());
        AddAccelerator(VirtualKey.Y, VirtualKeyModifiers.Control, () => _ = CurrentView?.RedoAsync());

        // Available even with no document open.
        AddAccelerator(VirtualKey.O, VirtualKeyModifiers.Control, () => OpenButton_Click(this, null!), requiresDocument: false);
        AddAccelerator(VirtualKey.K, VirtualKeyModifiers.Control, ShowPalette, requiresDocument: false);
        AddAccelerator(VirtualKey.F1, VirtualKeyModifiers.None, () => _ = ShowShortcutsAsync(), requiresDocument: false);
        // Ctrl+? — GNOME's other shortcuts-window binding (Shift+/ = ? on US layouts).
        AddAccelerator((VirtualKey)0xBF, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            () => _ = ShowShortcutsAsync(), requiresDocument: false);
        AddAccelerator(VirtualKey.F5, VirtualKeyModifiers.None, TogglePresentation);
        // Escape peels off one layer at a time, outermost first.
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
            else if (IsToolOptionsOpen)
            {
                HideToolOptions(); // close the panel but keep the tool armed
            }
            else if (FindBar.Visibility == Visibility.Visible)
            {
                HideFindBar();
            }
            else if (_activeViewer?.CancelSignaturePlacement() == true)
            {
                // Abandon a half-drawn placement before disarming the tool.
            }
            else if (_activeViewer?.ClearSignatureSelection() == true)
            {
                // Deselect a placed signature before disarming anything.
            }
            else if (_activeViewer?.ActiveTool is not (null or AnnotationTool.None))
            {
                _activeViewer?.ClearPendingSignature();
                SetActiveTool(AnnotationTool.None); // finally, put the tool away
            }
        }, requiresDocument: false);

        // Delete removes the selected signature. Guarded on there being one, so
        // Delete keeps deleting pages when the thumbnail sidebar has focus.
        AddAccelerator(VirtualKey.Delete, VirtualKeyModifiers.None, () =>
        {
            if (_activeViewer?.HasSelectedSignature == true)
            {
                _activeViewer.DeleteSelectedSignature();
            }
        }, skipWhenTextInputFocused: true);

        // Ctrl+C/X/V must fall through to focused text boxes (find box, page box).
        AddAccelerator(VirtualKey.C, VirtualKeyModifiers.Control, CopySelection, skipWhenTextInputFocused: true);
        AddAccelerator(VirtualKey.X, VirtualKeyModifiers.Control,
            () => CurrentView?.TryCopyPages(cut: true), skipWhenTextInputFocused: true);
        AddAccelerator(VirtualKey.V, VirtualKeyModifiers.Control,
            () => CurrentView?.TryPastePages(), skipWhenTextInputFocused: true);
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

    /// <summary>
    /// True when keystrokes belong to something that is taking text, so the
    /// document's own navigation must stand down.
    ///
    /// A focused PDF form field counts: it is a text input that happens to be
    /// drawn by PDFium rather than by XAML, so without this arrows would scroll
    /// the page instead of moving the caret, and Backspace would do nothing.
    /// Every keyboard path (accelerators, vim keys, the tunneling navigation
    /// handler) gates on this one predicate.
    /// </summary>
    private bool IsTextInputFocused() =>
        FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox or NumberBox or AutoSuggestBox or PasswordBox
        || _activeViewer?.IsFormFieldFocused == true;

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
        // Sidebar-focused Ctrl+C means "copy PAGES"; otherwise copy text.
        if (CurrentView is { } view && view.TryCopyPages(cut: false))
        {
            return;
        }
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

    // ---------------------------------------------------------------- forms / flatten / signatures

    /// <summary>
    /// Shows a standing notice for things the user needs to know about the
    /// document itself: an XFA form Rune cannot fill, or the presence of
    /// digital signatures.
    /// </summary>
    private void UpdateDocumentNotice(PdfViewer viewer)
    {
        if (CurrentView is not { } view)
        {
            return;
        }

        // Both notices describe a property of the document, not an event, so
        // they carry a dismiss key: this method runs again on every tab load,
        // page edit and flatten, and without one a notice the user closed would
        // immediately reappear.
        if (viewer.IsXfaForm)
        {
            // Saying nothing here is the worst option: the fields are visible,
            // so the user types into them and nothing happens.
            view.ShowNotice(
                "XFA form",
                "This document uses an XFA form, which Rune cannot fill. Adobe Acrobat can open it.",
                InfoBarSeverity.Warning,
                dismissKey: "xfa");
            return;
        }

        int signatures = view.SignatureCount;
        if (signatures > 0)
        {
            view.ShowNotice(
                signatures == 1 ? "Digitally signed" : $"{signatures} digital signatures",
                "Rune does not verify signatures. Open Signature details to see what it can read.",
                InfoBarSeverity.Informational,
                dismissKey: "signatures");
        }
    }

    private async void FlattenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentView is not { IsDocumentLoaded: true, LoadError: null } view || view.Viewer.Document is not { } document)
        {
            return;
        }

        // Flatten is irreversible in the file and clears the undo history, so
        // it always asks — and says plainly what will be lost.
        var confirm = new ContentDialog
        {
            Title = "Flatten annotations?",
            Content = "Highlights, notes, ink and filled form fields become part of the page. "
                    + "They can no longer be edited, moved or deleted, and this cannot be undone.\n\n"
                    + "The change is not written to disk until you save.",
            PrimaryButtonText = "Flatten",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(confirm) != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            view.Viewer.KillFormFocus();
            int changed = await view.FlattenDocumentAsync();
            UpdateToolbarForActive(); // flatten removes what made the item enabled
            // Not ShowError: this succeeded. Reporting it through the error
            // channel gave it a red bar, an error icon and error semantics for
            // screen readers.
            ShowNotice(
                changed == 0
                    ? "Nothing to flatten."
                    : $"Flattened {changed} page{(changed == 1 ? "" : "s")}. Save to write the change to disk.",
                changed == 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowError($"Flatten failed: {ex.Message}");
        }
    }

    private async void SignaturesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentView is not { IsDocumentLoaded: true, LoadError: null } view || view.Viewer.Document is not { } document)
        {
            return;
        }

        IReadOnlyList<PdfSignatureInfo> signatures;
        try
        {
            signatures = await view.Viewer.RunOnRenderThreadAsync(PdfWorkPriority.Interactive, document.GetSignatures);
        }
        catch (Exception ex)
        {
            ShowError($"Could not read signatures: {ex.Message}");
            return;
        }

        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };

        // This disclaimer is load-bearing, not boilerplate. Rune ships no
        // cryptography: it has not checked the certificate, the digest, trust
        // or revocation. Presenting any of the fields below as proof of
        // authenticity would mislead someone about a document they may be
        // relying on, so the limitation goes first and is never softened.
        panel.Children.Add(new TextBlock
        {
            Text = "Rune reports what the document claims. It does not verify signatures — "
                 + "the certificate, the signer's identity and revocation status are all unchecked. "
                 + "Use Adobe Acrobat or a dedicated validator to confirm a signature is genuine.",
            Style = (Style)Application.Current.Resources["CautionTextStyle"],
        });

        foreach (var signature in signatures)
        {
            panel.Children.Add(new Border { Style = (Style)Application.Current.Resources["FlyoutSeparatorStyle"] });

            var rows = new StackPanel { Spacing = 4 };
            rows.Children.Add(new TextBlock
            {
                Text = $"Signature {signature.Index + 1}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });

            AddSignatureRow(rows, "Reason", string.IsNullOrWhiteSpace(signature.Reason) ? "—" : signature.Reason);
            AddSignatureRow(rows, "Signed", signature.SignedAt?.ToString("f") ?? (string.IsNullOrWhiteSpace(signature.SignedAtRaw) ? "—" : signature.SignedAtRaw));
            AddSignatureRow(rows, "Format", string.IsNullOrWhiteSpace(signature.SubFilter) ? "—" : signature.SubFilter);
            AddSignatureRow(rows, "Coverage", signature.Coverage switch
            {
                SignatureCoverage.CoversWholeFile => "Signed byte range covers the whole file",
                SignatureCoverage.LeavesContentUnsigned => "Part of this file is outside the signed range — it was changed or added after signing",
                _ => "Could not read the signed byte range",
            });
            if (signature.IsCertifying)
            {
                AddSignatureRow(rows, "Certified", $"Author signature, DocMDP level {signature.DocMdpPermission}");
            }

            panel.Children.Add(rows);
        }

        await ShowDialogAsync(new ContentDialog
        {
            Title = signatures.Count == 1 ? "Signature details" : $"Signature details ({signatures.Count})",
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            CloseButtonText = "Close",
        });
    }

    private static void AddSignatureRow(Panel parent, string label, string value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Width = 90,
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
        });
        row.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 });
        parent.Children.Add(row);
    }

    /// <summary>
    /// The app's single notice channel.
    ///
    /// Routes to the active document's own notice host so the card floats over
    /// the page rather than across the sidebar; falls back to the window-level
    /// host for messages that arrive with no document open (startup problems,
    /// a missing recent file, background failures).
    /// </summary>
    private void ShowNotice(string message, InfoBarSeverity severity, string? title = null, string? dismissKey = null)
    {
        if (CurrentView is { IsDocumentLoaded: true, LoadError: null } view)
        {
            WindowNotice.Clear();
            view.ShowNotice(title, message, severity, dismissKey);
            return;
        }
        WindowNotice.Show(title, message, severity, dismissKey);
    }

    private void ShowError(string message) => ShowNotice(message, InfoBarSeverity.Error);

    /// <summary>Surfaces a background/unhandled failure (called by App's safety net).</summary>
    internal void ReportBackgroundError(string message) =>
        ShowError($"Something went wrong: {message}");

    /// <summary>
    /// Shows a dialog through <see cref="DialogHost"/> so two can never overlap,
    /// filling in XamlRoot and theme centrally. Every ContentDialog in this
    /// window goes through here.
    /// </summary>
    private Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        dialog.XamlRoot ??= Content.XamlRoot;
        // A ContentDialog is hosted in a popup outside the content tree, so it
        // does NOT inherit the theme ApplyTheme sets on Window.Content — it
        // follows the OS instead. Without this, choosing Light in Rune on a
        // dark-mode Windows gives a dark dialog over a light app. Same class of
        // bug as the caption buttons.
        dialog.RequestedTheme = ((FrameworkElement)Content).ActualTheme;
        return DialogHost.ShowAsync(dialog);
    }
}
