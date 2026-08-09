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
        ViewerControl.RotationChanged += (_, rotation) => OnViewRotated(rotation);
        ViewerControl.NightModeChanged += (_, _) => OnNightModeChanged();
        ViewerControl.AnnotationEdited += (_, e) => PushEdit(new DocumentEdit
        {
            Label = e.Label,
            IsPageMutation = false,
            PageIndex = e.PageIndex,
            SnapshotBytes = e.SnapshotBytes,
            UndoAction = e.UndoAction,
            RedoAction = e.RedoAction,
        });
    }

    public bool IsPaneOpen
    {
        get => Split.IsPaneOpen;
        set => Split.IsPaneOpen = value;
    }

    /// <summary>
    /// Shows a notice floating over this document's pages. Returns false when
    /// the user has already dismissed a notice with the same key.
    /// </summary>
    public bool ShowNotice(string? title, string message, InfoBarSeverity severity, string? dismissKey = null)
        => Notice.Show(title, message, severity, dismissKey);

    public void ClearNotice() => Notice.Clear();

    /// <summary>
    /// Runs PDFium work on the render thread — the one thread every PDFium call
    /// must funnel through (see <see cref="PdfViewer.WorkQueue"/>).
    ///
    /// The fallback covers one narrow race: closing a tab disposes the viewer's
    /// scheduler on Unloaded, cancelling anything still queued. If that happens
    /// the render thread has already been joined, so nothing else can be inside
    /// PDFium and finishing the work off-thread is safe. Without the fallback a
    /// tab closed at the wrong moment would leak the native document.
    /// </summary>
    private async Task<T> RunPdfAsync<T>(PdfWorkPriority priority, Func<T> work)
    {
        try
        {
            return await Viewer.RunOnRenderThreadAsync(priority, work);
        }
        catch (OperationCanceledException)
        {
            return await Task.Run(work);
        }
    }

    private Task RunPdfAsync(PdfWorkPriority priority, Action work)
        => RunPdfAsync(priority, () =>
        {
            work();
            return true;
        });

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
            _document = await RunPdfAsync(PdfWorkPriority.Interactive, () => PdfDocument.Open(FilePath));
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

        PopulateThumbnails(_document);
        // CurrentPageChanged doesn't fire on load (the page never changes), so
        // mark the starting page here or its ring never appears.
        SyncThumbnailSelection(Viewer.CurrentPage);
        _ = PopulateOutlineAsync(_document);
        _ = ReadSignatureCountAsync(_document);
        Loaded2?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Digital signatures in this document. Read once at load on the render
    /// thread and cached, because the toolbar consults it on every tab switch
    /// and PDFium must never be touched from the UI thread.
    /// </summary>
    public int SignatureCount { get; private set; }

    public bool HasSignatures => SignatureCount > 0;

    private async Task ReadSignatureCountAsync(PdfDocument document)
    {
        try
        {
            int count = await RunPdfAsync(PdfWorkPriority.Background, () => document.SignatureCount);
            if (_document == document)
            {
                SignatureCount = count;
                SignaturesRead?.Invoke(this, EventArgs.Empty);
            }
        }
        catch
        {
            // Signature reporting is informational; a failure just leaves it off.
        }
    }

    /// <summary>Raised once the signature count is known, so the shell can refresh its notice.</summary>
    public event EventHandler? SignaturesRead;

    public void Close()
    {
        ClearUndoHistory();
        Viewer.SetDocument(null);
        var document = _document;
        _document = null;
        if (document is not null)
        {
            // Dispose takes the global PDFium lock; never block the UI thread
            // on it (a tile render can hold it for tens of milliseconds).
            _ = RunPdfAsync(PdfWorkPriority.Interactive, document.Dispose);
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
        await RunPdfAsync(PdfWorkPriority.Interactive, () => document.SaveAs(tmp));

        double zoom = Viewer.Zoom;
        int rotation = Viewer.ViewRotation;
        double fraction = Viewer.ScrollFraction;

        Viewer.SetDocument(null);
        await RunPdfAsync(PdfWorkPriority.Interactive, document.Dispose); // takes the PDFium lock — keep it off the UI thread
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
            // Reopen whatever now lives at FilePath (swapped or original). The
            // reopen gives every page a fresh identity, so undo edits recorded
            // against the old document no longer apply — drop the history.
            ClearUndoHistory();
            _document = await RunPdfAsync(PdfWorkPriority.Interactive, () => PdfDocument.Open(FilePath));
            Viewer.SetDocument(_document);
            Viewer.RestoreView(zoom, rotation, fraction);
            PopulateThumbnails(_document);
            _ = PopulateOutlineAsync(_document);
        }
    }

    // ---------------------------------------------------------------- thumbnails

    /// <summary>
    /// Rebuilds the strip, giving each item its page's dimensions so every box
    /// is correctly shaped before any bitmap arrives. <c>GetPageSize</c> is a
    /// pure array read (no PDFium call), so this is safe on the UI thread — but
    /// it must run *after* the viewer has re-read page metrics following a page
    /// mutation, or it will index past the end.
    /// </summary>
    private void PopulateThumbnails(PdfDocument document)
    {
        int rotation = Viewer.ViewRotation;
        _thumbnails.Clear();
        for (int i = 0; i < document.PageCount; i++)
        {
            var (ptWidth, ptHeight) = document.GetPageSize(i);
            _thumbnails.Add(new ThumbnailItem(i, ptWidth, ptHeight, rotation));
        }
    }

    /// <summary>Re-shapes and re-renders thumbnails after the view is rotated.</summary>
    private void OnViewRotated(int rotation)
    {
        foreach (var item in _thumbnails)
        {
            item.SetRotation(rotation); // resizes the box and clears the stale render
        }
        // Only re-render what's on screen; the rest come back through
        // virtualization as the user scrolls to them.
        foreach (var item in _thumbnails)
        {
            if (ThumbList.ContainerFromItem(item) is not null)
            {
                _ = RenderThumbnailAsync(item);
            }
        }
    }

    // Lazily render a thumbnail as its container is realized (list virtualization).
    private void ThumbList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ThumbnailItem item || item.IsRendered || _document is null)
        {
            return;
        }
        _ = RenderThumbnailAsync(item);
    }

    /// <summary>
    /// Renders one thumbnail on the render thread at Thumbnail priority —
    /// visible tiles always win, so scrolling the sidebar can't make the
    /// document stutter.
    /// </summary>
    private async Task RenderThumbnailAsync(ThumbnailItem item)
    {
        if (_document is not { } document)
        {
            return;
        }

        int pageIndex = item.PageIndex;
        int rotation = Viewer.ViewRotation;
        try
        {
            var bmp = await Viewer.RunOnRenderThreadAsync(PdfWorkPriority.Thumbnail, () =>
            {
                // Render at 1.5x the 168-DIP display width so thumbnails stay
                // crisp on the typical 125-150% display scale. Scale off the
                // rotated width so a rotated page still fills the box.
                var (ptWidth, ptHeight) = document.GetPageSize(pageIndex);
                float acrossPt = ViewRotationMath.SwapsAxes(rotation) ? ptHeight : ptWidth;
                float scale = 252f / Math.Max(1f, acrossPt);
                return document.RenderPage(pageIndex, scale, rotation);
            });
            // Drop the result if the document or rotation moved on while we waited.
            if (_document == document && Viewer.ViewRotation == rotation)
            {
                item.Image = ToBitmap(bmp, Viewer.NightMode);
            }
            bmp.Return();
        }
        catch
        {
            // Skip unrenderable thumbnails (also covers doc-swap cancellation).
        }
    }

    /// <summary>
    /// Copies a rendered page into a bitmap, optionally inverting it for night
    /// mode. The viewer inverts on the GPU with a Win2D effect, but an Image in
    /// a virtualized ListView has no equivalent — and leaving thumbnails bright
    /// beside inverted pages was a long-standing visual bug. A 168x220 DIP
    /// thumbnail is ~55k pixels, so inverting during the copy is free.
    /// </summary>
    private static WriteableBitmap ToBitmap(PageBitmap page, bool invert)
    {
        var bitmap = new WriteableBitmap(page.Width, page.Height);
        int byteCount = page.Stride * page.Height;

        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            if (!invert)
            {
                // Copy exactly one image's worth of bytes (the pooled buffer may be larger).
                stream.Write(page.Pixels, 0, byteCount);
            }
            else
            {
                // BGRA: invert colour, leave alpha alone.
                var inverted = new byte[byteCount];
                for (int i = 0; i < byteCount; i += 4)
                {
                    inverted[i] = (byte)(255 - page.Pixels[i]);
                    inverted[i + 1] = (byte)(255 - page.Pixels[i + 1]);
                    inverted[i + 2] = (byte)(255 - page.Pixels[i + 2]);
                    inverted[i + 3] = page.Pixels[i + 3];
                }
                stream.Write(inverted, 0, byteCount);
            }
        }
        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>
    /// Re-renders realized thumbnails when night mode flips. Only what is on
    /// screen — the rest come back inverted through virtualization as the user
    /// scrolls to them.
    /// </summary>
    private void OnNightModeChanged()
    {
        foreach (var item in _thumbnails)
        {
            if (ThumbList.ContainerFromItem(item) is not null)
            {
                item.Image = null; // clears IsRendered so the re-request is honoured
                _ = RenderThumbnailAsync(item);
            }
        }
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
        // The ring marks the page being read regardless of what is selected for
        // page editing, so it updates even when the early-return below fires.
        for (int i = 0; i < _thumbnails.Count; i++)
        {
            _thumbnails[i].IsCurrent = i == pageIndex;
        }

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
            outline = await RunPdfAsync(PdfWorkPriority.Background, document.GetOutline);
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

    /// <summary>
    /// Raised when the user asks to extract the selection. The shell handles it
    /// because the save picker needs the window handle.
    /// </summary>
    public event EventHandler? ExtractRequested;

    private List<int> SelectedPageIndices() =>
        [.. ThumbList.SelectedItems.OfType<ThumbnailItem>().Select(t => t.PageIndex).OrderBy(i => i)];

    /// <summary>How many pages the thumbnail sidebar has selected, for the shell's menu state.</summary>
    public int SelectedPageCount => ThumbList.SelectedItems.Count;

    /// <summary>
    /// The selected pages as a filename fragment: "pages 2-5" for a run,
    /// "pages 1, 4, 9" for a scattered pick, capped so a 200-page selection
    /// cannot produce a filename the filesystem rejects.
    /// </summary>
    public string SelectedPageRangeLabel()
    {
        var pages = SelectedPageIndices();
        if (pages.Count == 0)
        {
            return "pages";
        }
        if (pages.Count == 1)
        {
            return $"page {pages[0] + 1}";
        }
        // Contiguous runs read far better as a range than as a list.
        bool contiguous = pages[^1] - pages[0] == pages.Count - 1;
        if (contiguous)
        {
            return $"pages {pages[0] + 1}-{pages[^1] + 1}";
        }
        var shown = pages.Take(6).Select(p => (p + 1).ToString());
        return "pages " + string.Join(", ", shown) + (pages.Count > 6 ? $" +{pages.Count - 6}" : "");
    }

    /// <summary>
    /// Writes the selected pages out as a new PDF, leaving this document
    /// untouched. Reuses <c>ExportPages</c>, the same engine call that backs the
    /// page clipboard, so extract is a save destination rather than new
    /// machinery. Returns true when the file was written.
    /// </summary>
    public async Task<bool> ExtractSelectedPagesAsync(string path)
    {
        var pages = SelectedPageIndices();
        if (pages.Count == 0 || _document is not { } document || _pageOpRunning)
        {
            return false;
        }

        try
        {
            var bytes = await Viewer.RunOnRenderThreadAsync(
                PdfWorkPriority.Interactive, () => document.ExportPages(pages));
            await File.WriteAllBytesAsync(path, bytes);
            return true;
        }
        catch (Exception ex) when (ex is Rune.PdfiumInterop.PdfiumException or IOException or UnauthorizedAccessException)
        {
            PageOpFailed?.Invoke(this, $"Could not extract pages: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Runs one page mutation on the render thread, then rebuilds every
    /// page-derived cache and remaps bookmarks. One op at a time — the
    /// sidebar is a live view of the document and re-entrancy would race it.
    /// Returns true when the op ran without error.
    /// </summary>
    private async Task<bool> RunPageOpAsync(Action<PdfDocument> op, Func<int, int?>? remapBookmark)
    {
        if (_pageOpRunning || _document is not { } document)
        {
            return false;
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
            return true;
        }
        catch (Exception ex) when (ex is Rune.PdfiumInterop.PdfiumException or InvalidOperationException)
        {
            AfterPageMutation(null); // resync the UI; metrics may have moved
            PageOpFailed?.Invoke(this, ex.Message);
            return false;
        }
        finally
        {
            _pageOpRunning = false;
        }
    }

    /// <summary>
    /// Bakes every annotation and form field into page content.
    ///
    /// Routed through the page-mutation path because flatten rewrites page
    /// content and annotation lists wholesale — every tile, thumbnail and text
    /// map derived from the old structure is stale afterwards. Undo history is
    /// dropped for the same reason: the annotation objects an undo entry refers
    /// to no longer exist.
    /// </summary>
    public async Task<int> FlattenDocumentAsync()
    {
        if (_document is not { } document)
        {
            return 0;
        }

        int changed = 0;
        await RunPageOpAsync(doc => changed = doc.FlattenAllPages(), remapBookmark: null);
        if (changed > 0)
        {
            ClearUndoHistory();
            PagesEdited?.Invoke(this, EventArgs.Empty);
        }
        return changed;
    }

    private void AfterPageMutation(Func<int, int?>? remapBookmark)
    {
        if (_document is not { } document)
        {
            return;
        }

        Viewer.HandleDocumentMutated();
        PopulateThumbnails(document);
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
        await DeletePagesWithUndoAsync(pages, cut: false);
    }

    /// <summary>Shared delete/cut: snapshots each victim page so undo re-inserts it at its original index.</summary>
    private async Task DeletePagesWithUndoAsync(List<int> pages, bool cut)
    {
        var deleted = pages.ToHashSet();
        var before = GetBookmarks();
        byte[][]? snapshots = null;
        byte[]? clipboardBytes = null;

        bool ok = await RunPageOpAsync(d =>
        {
            snapshots = [.. pages.Select(p => d.ExportPages([p]))]; // per-page, for undo restore
            if (cut)
            {
                clipboardBytes = d.ExportPages(pages); // whole selection in one PDF, in order
            }
            d.DeletePages(pages);
        }, p => BookmarkRemap.AfterDelete(p, deleted));

        if (!ok || snapshots is null)
        {
            return;
        }
        if (cut && clipboardBytes is not null)
        {
            PageClipboard.Set(clipboardBytes, pages.Count);
        }

        var origIndices = pages.ToList();
        var pageSnapshots = snapshots;
        PushEdit(new DocumentEdit
        {
            Label = pages.Count == 1 ? (cut ? "cut page" : "delete page") : $"{(cut ? "cut" : "delete")} {pages.Count} pages",
            IsPageMutation = true,
            SnapshotBytes = pageSnapshots.Sum(s => (long)s.Length),
            BookmarksBefore = before,
            BookmarksAfter = GetBookmarks(),
            UndoAction = d =>
            {
                for (int i = 0; i < origIndices.Count; i++)
                {
                    d.InsertPages(pageSnapshots[i], origIndices[i]); // ascending — positions line up
                }
            },
            RedoAction = d => d.DeletePages(origIndices),
        });
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

        await DeletePagesWithUndoAsync(pages, cut: true);
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
        await InsertBytesWithUndoAsync(bytes, at, "paste");
    }

    /// <summary>Insert all pages of another PDF file. Returns the number of pages inserted.</summary>
    public async Task<int> InsertPdfFileAsync(string path, int atIndex)
    {
        byte[] bytes;
        try
        {
            bytes = await Viewer.RunOnRenderThreadAsync(PdfWorkPriority.Interactive, () =>
            {
                using var src = PdfDocument.Open(path);
                return src.ExportPages([.. Enumerable.Range(0, src.PageCount)]);
            });
        }
        catch (Exception ex) when (ex is Rune.PdfiumInterop.PdfiumException or IOException)
        {
            PageOpFailed?.Invoke(this, ex.Message);
            return 0;
        }
        return await InsertBytesWithUndoAsync(bytes, atIndex, "insert PDF");
    }

    /// <summary>Insert a serialized PDF at an index with a matching undo (delete the inserted block).</summary>
    private async Task<int> InsertBytesWithUndoAsync(byte[] bytes, int at, string verb)
    {
        var before = GetBookmarks();
        int inserted = 0;
        bool ok = await RunPageOpAsync(d => inserted = d.InsertPages(bytes, at),
            p => BookmarkRemap.AfterInsert(p, at, inserted));
        if (!ok || inserted == 0)
        {
            return inserted;
        }

        int insertedCount = inserted;
        PushEdit(new DocumentEdit
        {
            Label = insertedCount == 1 ? $"{verb} page" : $"{verb} {insertedCount} pages",
            IsPageMutation = true,
            SnapshotBytes = bytes.Length,
            BookmarksBefore = before,
            BookmarksAfter = GetBookmarks(),
            UndoAction = d => d.DeletePages([.. Enumerable.Range(at, insertedCount)]),
            RedoAction = d => d.InsertPages(bytes, at),
        });
        return inserted;
    }

    // ---------------------------------------------------------------- undo / redo

    /// <summary>One undoable edit: an annotation change, or a page mutation with bookmark snapshots.</summary>
    private sealed class DocumentEdit : IUndoableEdit
    {
        public required string Label { get; init; }
        public long SnapshotBytes { get; init; }
        public required Action<PdfDocument> UndoAction { get; init; }
        public required Action<PdfDocument> RedoAction { get; init; }
        public bool IsPageMutation { get; init; }
        public int PageIndex { get; init; } // annotation edits: the page to refresh
        public IReadOnlyList<Rune.Services.BookmarkEntry>? BookmarksBefore { get; init; }
        public IReadOnlyList<Rune.Services.BookmarkEntry>? BookmarksAfter { get; init; }
    }

    private readonly UndoStack<DocumentEdit> _undoStack = new();

    /// <summary>Raised whenever undo/redo availability or labels change.</summary>
    public event EventHandler? UndoStateChanged;

    public bool CanUndo => _undoStack.CanUndo;
    public bool CanRedo => _undoStack.CanRedo;
    public string? UndoLabel => _undoStack.UndoLabel;
    public string? RedoLabel => _undoStack.RedoLabel;

    private void PushEdit(DocumentEdit edit)
    {
        _undoStack.Push(edit);
        UndoStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task UndoAsync()
    {
        if (_pageOpRunning)
        {
            return;
        }
        var edit = _undoStack.PopUndo();
        if (edit is not null)
        {
            await ApplyEditAsync(edit, edit.UndoAction, edit.BookmarksBefore);
        }
    }

    public async Task RedoAsync()
    {
        if (_pageOpRunning)
        {
            return;
        }
        var edit = _undoStack.PopRedo();
        if (edit is not null)
        {
            await ApplyEditAsync(edit, edit.RedoAction, edit.BookmarksAfter);
        }
    }

    private async Task ApplyEditAsync(DocumentEdit edit, Action<PdfDocument> action,
        IReadOnlyList<Rune.Services.BookmarkEntry>? bookmarks)
    {
        if (_document is not { } document)
        {
            return;
        }
        _pageOpRunning = true;
        try
        {
            if (edit.IsPageMutation)
            {
                Viewer.PreparePageMutation();
            }
            await Viewer.RunOnRenderThreadAsync(PdfWorkPriority.Interactive, () =>
            {
                action(document);
                return true;
            });

            if (edit.IsPageMutation)
            {
                Viewer.HandleDocumentMutated();
                PopulateThumbnails(document);
                _ = PopulateOutlineAsync(document);
                if (bookmarks is not null)
                {
                    SetBookmarks(bookmarks);
                }
                RefreshPaneVisibility();
                SyncThumbnailSelection(Viewer.CurrentPage);
            }
            else
            {
                // Undo can move or remove the very annotation that is selected,
                // and the selection frame is drawn from a cached rect — leaving
                // it would strand an empty box where the signature used to be.
                Viewer.ClearSignatureSelection();
                Viewer.InvalidatePage(edit.PageIndex);
            }
            PagesEdited?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is Rune.PdfiumInterop.PdfiumException or InvalidOperationException)
        {
            PageOpFailed?.Invoke(this, ex.Message);
        }
        finally
        {
            _pageOpRunning = false;
            UndoStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetBookmarks(IReadOnlyList<Rune.Services.BookmarkEntry> entries)
    {
        _bookmarks.Clear();
        foreach (var entry in entries.OrderBy(b => b.PageIndex))
        {
            _bookmarks.Add(new BookmarkItem(entry.PageIndex, entry.Name));
        }
        BookmarksChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops the undo history — page identity is invalidated after a save-in-place reopen or close.</summary>
    public void ClearUndoHistory()
    {
        _undoStack.Clear();
        UndoStateChanged?.Invoke(this, EventArgs.Empty);
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

        var before = GetBookmarks();
        bool ok = await RunPageOpAsync(d => d.MovePages(movedIndices, destIndex), p => map[p]);
        if (ok)
        {
            PushEdit(new DocumentEdit
            {
                Label = movedIndices.Count == 1 ? "move page" : $"move {movedIndices.Count} pages",
                IsPageMutation = true,
                SnapshotBytes = 0, // moves store no snapshot — the inverse is a permutation
                BookmarksBefore = before,
                BookmarksAfter = GetBookmarks(),
                UndoAction = d => d.RestoreMovedPages(movedIndices, destIndex),
                RedoAction = d => d.MovePages(movedIndices, destIndex),
            });
        }
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
        AddMenuAction(menu, $"Extract {pages} to a new file…", Symbol.Save,
            () => ExtractRequested?.Invoke(this, EventArgs.Empty));
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
        if (await Rune.Services.DialogHost.ShowAsync(dialog) == ContentDialogResult.Primary &&
            !string.IsNullOrWhiteSpace(box.Text))
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
