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

    public event EventHandler? Loaded2;

    public DocumentView(string filePath)
    {
        InitializeComponent();
        FilePath = filePath;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        ThumbList.ItemsSource = _thumbnails;

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

        PopulateThumbnails(_document.PageCount);
        _ = PopulateOutlineAsync(_document);
        Loaded2?.Invoke(this, EventArgs.Empty);
    }

    public void Close()
    {
        Viewer.SetDocument(null);
        _document?.Dispose();
        _document = null;
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
    private void ThumbList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ThumbnailItem item || item.IsRendered || _document is null)
        {
            return;
        }

        var document = _document;
        int pageIndex = item.PageIndex;
        Task.Run(() =>
        {
            try
            {
                // Small fixed-width render; thumbnails don't need DPI scaling.
                var (ptWidth, _) = document.GetPageSize(pageIndex);
                float scale = 120f / Math.Max(1f, ptWidth);
                var bmp = document.RenderPage(pageIndex, scale);
                _dispatcher.TryEnqueue(() =>
                {
                    if (_document != document)
                    {
                        bmp.Return();
                        return;
                    }
                    item.Image = ToBitmap(bmp);
                    bmp.Return();
                });
            }
            catch
            {
                // Skip unrenderable thumbnails.
            }
        });
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
        OutlineTab.IsEnabled = _hasOutline;
    }

    private void OutlineTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is OutlineNode node && node.PageIndex >= 0)
        {
            Viewer.GoToPage(node.PageIndex, recordHistory: true);
        }
    }

    // ---------------------------------------------------------------- sidebar tabs

    private void ThumbsTab_Click(object sender, RoutedEventArgs e) => ShowSidebar(thumbnails: true);
    private void OutlineTab_Click(object sender, RoutedEventArgs e) => ShowSidebar(thumbnails: false);

    private void ShowSidebar(bool thumbnails)
    {
        ThumbsTab.IsChecked = thumbnails;
        OutlineTab.IsChecked = !thumbnails;
        ThumbList.Visibility = thumbnails ? Visibility.Visible : Visibility.Collapsed;

        OutlineTree.Visibility = !thumbnails && _hasOutline ? Visibility.Visible : Visibility.Collapsed;
        NoOutlineLabel.Visibility = !thumbnails && !_hasOutline ? Visibility.Visible : Visibility.Collapsed;
    }
}
