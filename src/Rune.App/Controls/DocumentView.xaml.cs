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
        // Navigate only on a plain single selection — Ctrl/Shift multi-select
        // (for page editing) must not yank the view around.
        if (_syncingSelection || ThumbList.SelectedItems.Count != 1 ||
            ThumbList.SelectedItem is not ThumbnailItem item)
        {
            return;
        }
        Viewer.GoToPage(item.PageIndex, recordHistory: true);
    }

    private void SyncThumbnailSelection(int pageIndex)
    {
        // Never collapse an in-progress multi-selection just because the
        // viewer scrolled to another page.
        if (pageIndex < 0 || pageIndex >= _thumbnails.Count || ThumbList.SelectedItems.Count > 1)
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

    // ---------------------------------------------------------------- page editing

    private bool _pageOpRunning;

    /// <summary>Raised after any page delete/move/insert (dirty marker + toolbar refresh).</summary>
    public event EventHandler? PagesEdited;

    /// <summary>Raised when a page operation fails with a user-relevant message.</summary>
    public event EventHandler<string>? PageOpFailed;

    private List<int> SelectedPageIndices() =>
        [.. ThumbList.SelectedItems.OfType<ThumbnailItem>().Select(t => t.PageIndex).OrderBy(i => i)];

    /// <summary>
    /// Runs one page mutation on the render thread, then rebuilds every
    /// page-derived cache and remaps bookmarks. One op at a time — the
    /// sidebar is a live view of the document and re-entrancy would race it.
    /// </summary>
    private async Task RunPageOpAsync(Action<PdfDocument> op, Func<int, int?>? remapBookmark)
    {
        if (_pageOpRunning || _document is not { } document)
        {
            return;
        }
        _pageOpRunning = true;
        try
        {
            Viewer.PreparePageMutation();
            await Viewer.RunOnRenderThreadAsync(PdfWorkPriority.Interactive, () =>
            {
                op(document);
                return true;
            });
            AfterPageMutation(remapBookmark);
        }
        catch (Exception ex) when (ex is Rune.PdfiumInterop.PdfiumException or InvalidOperationException)
        {
            AfterPageMutation(null); // resync the UI; metrics may have moved
            PageOpFailed?.Invoke(this, ex.Message);
        }
        finally
        {
            _pageOpRunning = false;
        }
    }

    private void AfterPageMutation(Func<int, int?>? remapBookmark)
    {
        if (_document is not { } document)
        {
            return;
        }

        Viewer.HandleDocumentMutated();
        PopulateThumbnails(document.PageCount);
        _ = PopulateOutlineAsync(document);

        if (remapBookmark is not null && _bookmarks.Count > 0)
        {
            var remapped = _bookmarks
                .Select(b => (Item: b, NewPage: remapBookmark(b.PageIndex)))
                .ToList();
            _bookmarks.Clear();
            foreach (var (item, newPage) in remapped.Where(t => t.NewPage is not null).OrderBy(t => t.NewPage))
            {
                item.PageIndex = newPage!.Value;
                _bookmarks.Add(item);
            }
            BookmarksChanged?.Invoke(this, EventArgs.Empty);
        }

        RefreshPaneVisibility();
        SyncThumbnailSelection(Viewer.CurrentPage);
        PagesEdited?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteSelectedPagesAsync()
    {
        var pages = SelectedPageIndices();
        if (pages.Count == 0 || _document is not { } document)
        {
            return;
        }
        if (pages.Count >= document.PageCount)
        {
            PageOpFailed?.Invoke(this, "Cannot delete every page of a document.");
            return;
        }
        var deleted = pages.ToHashSet();
        await RunPageOpAsync(d => d.DeletePages(pages), p => BookmarkRemap.AfterDelete(p, deleted));
    }

    /// <summary>Copy (or cut) the selected pages into the app-wide page clipboard.</summary>
    public async Task CopySelectedPagesAsync(bool cut)
    {
        var pages = SelectedPageIndices();
        if (pages.Count == 0 || _document is not { } document || _pageOpRunning)
        {
            return;
        }
        if (cut && pages.Count >= document.PageCount)
        {
            PageOpFailed?.Invoke(this, "Cannot cut every page of a document.");
            return;
        }

        if (!cut)
        {
            var bytes = await Viewer.RunOnRenderThreadAsync(
                PdfWorkPriority.Interactive, () => document.ExportPages(pages));
            PageClipboard.Set(bytes, pages.Count);
            return;
        }

        byte[]? cutBytes = null;
        var deleted = pages.ToHashSet();
        await RunPageOpAsync(d =>
        {
            cutBytes = d.ExportPages(pages);
            d.DeletePages(pages);
        }, p => BookmarkRemap.AfterDelete(p, deleted));
        if (cutBytes is not null)
        {
            PageClipboard.Set(cutBytes, pages.Count);
        }
    }

    /// <summary>Insert the page clipboard at <paramref name="atIndex"/> (default: after the selection / current page).</summary>
    public async Task PastePagesAsync(int? atIndex = null)
    {
        if (!PageClipboard.HasPages || _document is null)
        {
            return;
        }
        var bytes = PageClipboard.Pdf!;
        int at = atIndex ?? (SelectedPageIndices() is { Count: > 0 } sel ? sel[^1] + 1 : Viewer.CurrentPage + 1);
        int inserted = 0;
        await RunPageOpAsync(d => inserted = d.InsertPages(bytes, at),
            p => BookmarkRemap.AfterInsert(p, at, inserted));
    }

    /// <summary>Insert all pages of another PDF file. Returns the number of pages inserted.</summary>
    public async Task<int> InsertPdfFileAsync(string path, int atIndex)
    {
        int inserted = 0;
        await RunPageOpAsync(d => inserted = d.InsertPagesFromFile(path, atIndex),
            p => BookmarkRemap.AfterInsert(p, atIndex, inserted));
        return inserted;
    }

    // Ctrl+C/X/V routing helpers for the shell: page ops only apply when the
    // thumbnail list has keyboard focus (otherwise Ctrl+C means "copy text").
    public bool TryCopyPages(bool cut)
    {
        if (!IsThumbListFocused() || ThumbList.SelectedItems.Count == 0)
        {
            return false;
        }
        _ = CopySelectedPagesAsync(cut);
        return true;
    }

    public bool TryPastePages()
    {
        if (_document is null || !PageClipboard.HasPages)
        {
            return false;
        }
        _ = PastePagesAsync();
        return true;
    }

    private bool IsThumbListFocused()
    {
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, ThumbList))
            {
                return true;
            }
            focused = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(focused);
        }
        return false;
    }

    // ---- sidebar input: drag-reorder, context menu, keys, external drops ----

    private async void ThumbList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move ||
            _document is not { } document || args.Items.Count == 0)
        {
            return;
        }

        var moved = args.Items.OfType<ThumbnailItem>().ToList();
        if (moved.Count == 0)
        {
            return;
        }
        // Original page indices of the dragged items, and the block's landing
        // position in the reordered collection (= final ordering).
        var movedIndices = moved.Select(m => m.PageIndex).OrderBy(i => i).ToList();
        int destIndex = moved.Min(m => _thumbnails.IndexOf(m));

        var map = BookmarkRemap.MovePermutation(document.PageCount, movedIndices, destIndex);
        bool isNoOp = true;
        for (int i = 0; i < map.Length && isNoOp; i++)
        {
            isNoOp = map[i] == i;
        }
        if (isNoOp)
        {
            return;
        }

        await RunPageOpAsync(d => d.MovePages(movedIndices, destIndex), p => map[p]);
    }

    private void ThumbList_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.Handled = true; // ours — don't let the window-level "open as tab" handler take it
        }
    }

    private async void ThumbList_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            return;
        }
        e.Handled = true;

        int at = DropIndexFromPosition(e.GetPosition(ThumbList));
        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
        {
            if (item is Windows.Storage.StorageFile file &&
                file.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                at += await InsertPdfFileAsync(file.Path, at); // keep multi-file order
            }
        }
    }

    /// <summary>Insertion index for a drop at a point in the (vertical) thumbnail list.</summary>
    private int DropIndexFromPosition(Windows.Foundation.Point position)
    {
        for (int i = 0; i < _thumbnails.Count; i++)
        {
            if (ThumbList.ContainerFromIndex(i) is ListViewItem container)
            {
                var top = container.TransformToVisual(ThumbList).TransformPoint(new Windows.Foundation.Point(0, 0));
                if (position.Y < top.Y + container.ActualHeight / 2)
                {
                    return i;
                }
            }
        }
        return _thumbnails.Count;
    }

    private void ThumbList_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete && ThumbList.SelectedItems.Count > 0)
        {
            _ = DeleteSelectedPagesAsync();
            e.Handled = true;
        }
    }

    private void ThumbList_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ThumbnailItem item &&
            !ThumbList.SelectedItems.Contains(item))
        {
            ThumbList.SelectedIndex = item.PageIndex; // right-click targets what's under the cursor
        }

        int count = ThumbList.SelectedItems.Count;
        if (count == 0)
        {
            return;
        }
        string pages = count == 1 ? "page" : $"{count} pages";
        int insertAt = SelectedPageIndices() is { Count: > 0 } sel ? sel[^1] + 1 : _thumbnails.Count;

        var menu = new MenuFlyout();
        AddMenuAction(menu, $"Copy {pages}", Symbol.Copy, () => _ = CopySelectedPagesAsync(cut: false));
        AddMenuAction(menu, $"Cut {pages}", Symbol.Cut, () => _ = CopySelectedPagesAsync(cut: true));
        var paste = new MenuFlyoutItem
        {
            Text = PageClipboard.PageCount is > 1 and var n ? $"Paste {n} pages after" : "Paste after",
            Icon = new SymbolIcon(Symbol.Paste),
            IsEnabled = PageClipboard.HasPages,
        };
        paste.Click += (_, _) => _ = PastePagesAsync(insertAt);
        menu.Items.Add(paste);
        AddMenuAction(menu, "Insert PDF here…", Symbol.Add, () => _ = PickAndInsertPdfAsync(insertAt));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenuAction(menu, $"Delete {pages}", Symbol.Delete, () => _ = DeleteSelectedPagesAsync());

        menu.ShowAt(ThumbList, e.GetPosition(ThumbList));
        e.Handled = true;
    }

    private static void AddMenuAction(MenuFlyout menu, string text, Symbol icon, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new SymbolIcon(icon) };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private async Task PickAndInsertPdfAsync(int atIndex)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".pdf");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await InsertPdfFileAsync(file.Path, atIndex);
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
