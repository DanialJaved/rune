using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Rune.Engine;
using Rune.PdfiumInterop;
using Rune.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Rune.Controls;

/// <summary>
/// One open document: the <see cref="PdfViewer"/> plus a collapsible sidebar
/// (thumbnails + outline). Owns the <see cref="PdfDocument"/> lifetime and
/// loads it lazily so background tabs cost nothing until first shown
/// (SumatraPDF-style lazy tabs).
/// </summary>
public sealed partial class DocumentView : UserControl
{
    private readonly DispatcherQueue _dispatcher;
    private readonly ObservableCollection<ThumbnailItem> _thumbnails = [];

    private PdfDocument? _document;
    private bool _loaded;
    private bool _syncingSelection;

    public string FilePath { get; }
    public string DisplayName => Path.GetFileName(FilePath);
    public PdfViewer Viewer => ViewerControl;
    public bool IsDocumentLoaded => _loaded;
    public string? LoadError { get; private set; }

    /// <summary>Set by the shell (from settings) before load: open the sidebar once the document is ready.</summary>
    public bool OpenSidebarOnLoad { get; set; }

    public event EventHandler? Loaded2;

    public DocumentView(string filePath)
    {
        InitializeComponent();
        FilePath = filePath;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        ThumbList.ItemsSource = _thumbnails;
        BookmarkList.ItemsSource = _bookmarks;

        ViewerControl.CurrentPageChanged += (_, page) => SyncThumbnailSelection(page);
    }

    public bool IsPaneOpen
    {
        get => Split.IsPaneOpen;
        set => Split.IsPaneOpen = value;
    }

    /// <summary>Opens the document (once) and populates the view. Safe to await repeatedly.</summary>
    public async Task EnsureLoadedAsync(RecentFile? restore)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;

        try
        {
            _document = await Task.Run(() => PdfDocument.Open(FilePath));
        }
        catch (Exception ex) when (ex is PdfiumException or IOException)
        {
            LoadError = ex.Message;
            Loaded2?.Invoke(this, EventArgs.Empty);
            return;
        }

        Viewer.SetDocument(_document);

        if (restore is not null)
        {
            Viewer.RestoreView(restore.Zoom, restore.Rotation, restore.ScrollFraction);
        }
        if (OpenSidebarOnLoad)
        {
            IsPaneOpen = true;
        }

        PopulateThumbnails(_document.PageCount);
        _ = PopulateOutlineAsync(_document);
        Loaded2?.Invoke(this, EventArgs.Empty);
    }

    public void Close()
    {
        Viewer.SetDocument(null);
        var document = _document;
        _document = null;
        if (document is not null)
        {
            // Dispose takes the global PDFium lock; never block the UI thread
            // on it (a tile render can hold it for tens of milliseconds).
            _ = Task.Run(document.Dispose);
        }
    }

    public bool IsDirty => _document?.IsDirty == true;

    /// <summary>
    /// Persists annotation edits back to the original file. The open document
    /// holds the source file, so this saves to a temp copy, closes, swaps the
    /// files, and reopens at the same view position.
    /// </summary>
    public async Task SaveInPlaceAsync()
    {
        if (_document is not { IsDirty: true } document)
        {
            return;
        }

        string tmp = FilePath + ".saving";
        await Task.Run(() => document.SaveAs(tmp));

        double zoom = Viewer.Zoom;
        int rotation = Viewer.ViewRotation;
        double fraction = Viewer.ScrollFraction;

        Viewer.SetDocument(null);
        await Task.Run(document.Dispose); // takes the PDFium lock — keep it off the UI thread
        _document = null;

        try
        {
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
        finally
        {
            // Reopen whatever now lives at FilePath (swapped or original).
            _document = await Task.Run(() => PdfDocument.Open(FilePath));
            Viewer.SetDocument(_document);
            Viewer.RestoreView(zoom, rotation, fraction);
            PopulateThumbnails(_document.PageCount);
            _ = PopulateOutlineAsync(_document);
        }
    }

    // ---------------------------------------------------------------- thumbnails

    private void PopulateThumbnails(int pageCount)
    {
        _thumbnails.Clear();
        for (int i = 0; i < pageCount; i++)
        {
            _thumbnails.Add(new ThumbnailItem(i));
        }
    }

    // Lazily render a thumbnail as its container is realized (list virtualization).
    // Runs on the render thread at Thumbnail priority: visible tiles always win,
    // so scrolling the sidebar can't make the document stutter.
    private async void ThumbList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ThumbnailItem item || item.IsRendered || _document is null)
        {
            return;
        }

        var document = _document;
        int pageIndex = item.PageIndex;
        try
        {
            var bmp = await Viewer.RunOnRenderThreadAsync(PdfWorkPriority.Thumbnail, () =>
            {
                // Render at 1.5x the 168-DIP display width so thumbnails stay
                // crisp on the typical 125-150% display scale.
                var (ptWidth, _) = document.GetPageSize(pageIndex);
                float scale = 252f / Math.Max(1f, ptWidth);
                return document.RenderPage(pageIndex, scale);
            });
            if (_document == document)
            {
                item.Image = ToBitmap(bmp);
            }
            bmp.Return();
        }
        catch
        {
            // Skip unrenderable thumbnails (also covers doc-swap cancellation).
        }
    }

    private static WriteableBitmap ToBitmap(PageBitmap page)
    {
        var bitmap = new WriteableBitmap(page.Width, page.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            // Copy exactly one image's worth of bytes (the pooled buffer may be larger).
            stream.Write(page.Pixels, 0, page.Stride * page.Height);
        }
        bitmap.Invalidate();
        return bitmap;
    }

    private void ThumbList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || ThumbList.SelectedItem is not ThumbnailItem item)
        {
            return;
        }
        Viewer.GoToPage(item.PageIndex, recordHistory: true);
    }

    private void SyncThumbnailSelection(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _thumbnails.Count)
        {
            return;
        }
        _syncingSelection = true;
        ThumbList.SelectedIndex = pageIndex;
        if (Split.IsPaneOpen && ThumbList.Visibility == Visibility.Visible)
        {
            ThumbList.ScrollIntoView(_thumbnails[pageIndex]);
        }
        _syncingSelection = false;
    }

    // ---------------------------------------------------------------- outline

    private bool _hasOutline;

    private async Task PopulateOutlineAsync(PdfDocument document)
    {
        IReadOnlyList<OutlineItem> outline;
        try
        {
            outline = await Task.Run(document.GetOutline);
        }
        catch
        {
            outline = [];
        }

        if (_document != document)
        {
            return;
        }

        // Bind data objects directly: the DataTemplate's x:DataType is
        // OutlineNode, and its TreeViewItem.ItemsSource="{x:Bind Children}"
        // supplies the hierarchy. (Populating RootNodes with TreeViewNode
        // wrappers instead crashes template realization with a type mismatch.)
        var nodes = outline.Select(item => new OutlineNode(item)).ToList();
        _hasOutline = nodes.Count > 0;
        OutlineTree.ItemsSource = nodes;
    }

    private void OutlineTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is OutlineNode node && node.PageIndex >= 0)
        {
            Viewer.GoToPage(node.PageIndex, recordHistory: true);
        }
    }

    // ---------------------------------------------------------------- bookmarks

    private readonly ObservableCollection<BookmarkItem> _bookmarks = [];
    private bool _bookmarksLoaded;

    /// <summary>Raised after any bookmark add/remove/rename; the shell persists.</summary>
    public event EventHandler? BookmarksChanged;

    /// <summary>Fills the pane from persisted state. First call wins (idempotent across tab switches).</summary>
    public void LoadBookmarks(IEnumerable<Rune.Services.BookmarkEntry> entries)
    {
        if (_bookmarksLoaded)
        {
            return;
        }
        _bookmarksLoaded = true;
        foreach (var entry in entries.OrderBy(b => b.PageIndex))
        {
            _bookmarks.Add(new BookmarkItem(entry.PageIndex, entry.Name));
        }
        RefreshPaneVisibility();
    }

    public List<Rune.Services.BookmarkEntry> GetBookmarks() =>
        [.. _bookmarks.Select(b => new Rune.Services.BookmarkEntry { PageIndex = b.PageIndex, Name = b.Name })];

    /// <summary>Adds a bookmark on the page (or removes the existing one). Returns true when added.</summary>
    public bool ToggleBookmark(int pageIndex)
    {
        var existing = _bookmarks.FirstOrDefault(b => b.PageIndex == pageIndex);
        if (existing is not null)
        {
            _bookmarks.Remove(existing);
        }
        else
        {
            var item = new BookmarkItem(pageIndex, $"Page {pageIndex + 1}");
            int insertAt = 0;
            while (insertAt < _bookmarks.Count && _bookmarks[insertAt].PageIndex < pageIndex)
            {
                insertAt++;
            }
            _bookmarks.Insert(insertAt, item);
        }
        RefreshPaneVisibility();
        BookmarksChanged?.Invoke(this, EventArgs.Empty);
        return existing is null;
    }

    /// <summary>Switches the sidebar to the Bookmarks pane (used after Ctrl+B when the pane is open).</summary>
    public void ShowBookmarksPane() => ShowSidebar(SidebarPane.Bookmarks);

    private void BookmarkList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BookmarkItem item)
        {
            Viewer.GoToPage(item.PageIndex, recordHistory: true);
        }
    }

    private void BookmarkList_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete && BookmarkList.SelectedItem is BookmarkItem item)
        {
            RemoveBookmark(item);
            e.Handled = true;
        }
    }

    private void BookmarkList_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not BookmarkItem item)
        {
            return;
        }

        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "Rename", Icon = new SymbolIcon(Symbol.Rename) };
        rename.Click += async (_, _) => await RenameBookmarkAsync(item);
        var delete = new MenuFlyoutItem { Text = "Delete", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += (_, _) => RemoveBookmark(item);
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        menu.ShowAt(BookmarkList, e.GetPosition(BookmarkList));
        e.Handled = true;
    }

    private void RemoveBookmark(BookmarkItem item)
    {
        _bookmarks.Remove(item);
        RefreshPaneVisibility();
        BookmarksChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RenameBookmarkAsync(BookmarkItem item)
    {
        var box = new TextBox { Text = item.Name, SelectionStart = item.Name.Length, MinWidth = 280 };
        var dialog = new ContentDialog
        {
            Title = "Rename bookmark",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
        {
            item.Name = box.Text.Trim();
            BookmarksChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RefreshPaneVisibility() => ShowSidebar(_activePane);

    // ---------------------------------------------------------------- sidebar panes

    private enum SidebarPane
    {
        Thumbnails,
        Chapters,
        Bookmarks,
    }

    private SidebarPane _activePane = SidebarPane.Thumbnails;

    private void ThumbsTab_Click(object sender, RoutedEventArgs e) => ShowSidebar(SidebarPane.Thumbnails);
    private void OutlineTab_Click(object sender, RoutedEventArgs e) => ShowSidebar(SidebarPane.Chapters);
    private void BookmarksTab_Click(object sender, RoutedEventArgs e) => ShowSidebar(SidebarPane.Bookmarks);

    private void ShowSidebar(SidebarPane pane)
    {
        _activePane = pane;
        ThumbsTab.IsChecked = pane == SidebarPane.Thumbnails;
        OutlineTab.IsChecked = pane == SidebarPane.Chapters;
        BookmarksTab.IsChecked = pane == SidebarPane.Bookmarks;

        ThumbList.Visibility = pane == SidebarPane.Thumbnails ? Visibility.Visible : Visibility.Collapsed;
        OutlineTree.Visibility = pane == SidebarPane.Chapters && _hasOutline ? Visibility.Visible : Visibility.Collapsed;
        NoOutlineLabel.Visibility = pane == SidebarPane.Chapters && !_hasOutline ? Visibility.Visible : Visibility.Collapsed;

        bool hasBookmarks = _bookmarks.Count > 0;
        BookmarkList.Visibility = pane == SidebarPane.Bookmarks && hasBookmarks ? Visibility.Visible : Visibility.Collapsed;
        NoBookmarksLabel.Visibility = pane == SidebarPane.Bookmarks && !hasBookmarks ? Visibility.Visible : Visibility.Collapsed;
    }
}
