using Rune.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.System;
using Windows.UI;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;

namespace Rune.Controls;

public enum FitMode
{
    None,
    FitWidth,
    FitPage,
}

/// <summary>
/// The document viewport: a virtualized Win2D canvas inside a ScrollViewer.
///
/// Drawing model: for every invalidated region we draw, per page — a white
/// page rectangle, then a stretched low-res preview if one is cached (the
/// "progressive" blurry pass), then any crisp tiles cached at the current
/// scale. Anything missing is requested from the render thread; when a tile
/// arrives we invalidate just its rectangle, and the region redraws crisp.
/// </summary>
public sealed partial class PdfViewer : UserControl
{
    private const double MinZoom = 0.1;
    private const double MaxZoom = 6.4;
    private const long TileCacheBudgetBytes = 128L * 1024 * 1024;
    private const int PreviewCacheCapacity = 64;
    private const float PreviewTargetWidthPx = 216f;

    private readonly DispatcherQueue _dispatcher;
    private readonly RenderScheduler _scheduler;

    private PdfDocument? _document;
    private (float Width, float Height)[] _pageSizes = [];
    private PageLayout? _layout;

    // Tile cache: LRU by byte budget. Only touched on the UI thread.
    private readonly Dictionary<TileKey, (CanvasBitmap Bitmap, LinkedListNode<TileKey> Node)> _tiles = [];
    private readonly LinkedList<TileKey> _tileLru = [];
    private long _tileBytes;

    // Whole-page previews at ~216 px wide; scale-independent placeholders.
    private readonly Dictionary<int, CanvasBitmap> _previews = [];
    private readonly LinkedList<int> _previewLru = [];

    private double _zoom = 1.0;
    private int _rotation;
    private FitMode _fitMode = FitMode.FitWidth;
    private int _currentPage;
    private bool _nightMode;

    // Link hit-testing: per-page links, extracted lazily off the UI thread.
    private readonly Dictionary<int, IReadOnlyList<PdfLink>> _links = [];
    private readonly HashSet<int> _linksRequested = [];
    private bool _pointerOverLink;

    // Back/forward: vertical offsets we jumped away from (outline/link/page jumps).
    private readonly Stack<double> _back = new();
    private readonly Stack<double> _forward = new();

    // Text selection (single-page for v1).
    private TextSelection? _selection;
    private int _selectionPage = -1;
    private int _selectionAnchorChar = -1;
    private bool _isSelecting;

    // Search highlights, grouped by page, plus the active hit.
    private readonly Dictionary<int, List<SearchHit>> _searchByPage = [];
    private SearchHit? _activeHit;

    public event EventHandler<int>? CurrentPageChanged;
    public event EventHandler<double>? ZoomChanged;
    public event EventHandler? HistoryChanged;

    /// <summary>Raised when the user clicks an external (URI) link. The shell decides whether to open it.</summary>
    public event EventHandler<string>? LinkActivated;

    public int CurrentPage => _currentPage;
    public double Zoom => _zoom;
    public int ViewRotation => _rotation;
    public int PageCount => _document?.PageCount ?? 0;
    public PdfDocument? Document => _document;

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Current scroll position as a 0–1 fraction of total document height (for session restore).</summary>
    public double ScrollFraction =>
        _layout is { TotalHeight: > 0 } ? Scroller.VerticalOffset / _layout.TotalHeight : 0;

    /// <summary>The currently selected text, or empty if nothing is selected.</summary>
    public string SelectedText => _selection?.Text ?? string.Empty;
    public bool HasSelection => (_selection?.Count ?? 0) > 0;

    /// <summary>Invert page colors for night reading. Draw-time effect; cheap to toggle.</summary>
    public bool NightMode
    {
        get => _nightMode;
        set
        {
            if (_nightMode != value)
            {
                _nightMode = value;
                Canvas.Invalidate();
            }
        }
    }

    public FitMode FitMode
    {
        get => _fitMode;
        set
        {
            _fitMode = value;
            ApplyFitMode();
        }
    }

    public PdfViewer()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _scheduler = new RenderScheduler(OnTileRendered);

        // Catch Ctrl+wheel even after the ScrollViewer has marked it handled.
        AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheelChanged), handledEventsToo: true);
        Canvas.PointerMoved += Canvas_PointerMoved;
        Canvas.PointerPressed += Canvas_PointerPressed;
        Canvas.PointerReleased += Canvas_PointerReleased;
        Canvas.PointerExited += (_, _) => SetLinkCursor(false);
        ActualThemeChanged += (_, _) => Canvas.Invalidate();
        Unloaded += (_, _) => _scheduler.Dispose();
    }

    private double DisplayScale => XamlRoot?.RasterizationScale ?? 1.0;
    private float RenderScale => (float)(_zoom * DisplayScale);

    // ---------------------------------------------------------------- document

    /// <summary>Shows a document (ownership stays with the caller) or clears the view.</summary>
    public void SetDocument(PdfDocument? document)
    {
        _document = document;
        _pageSizes = document is null
            ? []
            : [.. Enumerable.Range(0, document.PageCount).Select(document.GetPageSize)];
        _rotation = 0;
        _currentPage = 0;
        _fitMode = FitMode.FitWidth;

        _back.Clear();
        _forward.Clear();
        _links.Clear();
        _linksRequested.Clear();
        _selection = null;
        _selectionPage = -1;
        _selectionAnchorChar = -1;
        _isSelecting = false;
        _searchByPage.Clear();
        _activeHit = null;

        _scheduler.SetDocument(document);
        ClearCaches();
        ApplyFitMode();
        Scroller.ChangeView(0, 0, null, disableAnimation: true);
        Canvas.Invalidate();
        UpdateDesiredTiles();
        PrefetchVisibleLinks(); // ChangeView(0,0) on a fresh doc fires no ViewChanged
        CurrentPageChanged?.Invoke(this, 0);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Restores a saved reading position after the layout is ready.</summary>
    public void RestoreView(double zoom, int rotation, double scrollFraction)
    {
        _rotation = ((rotation % 4) + 4) % 4;
        _fitMode = FitMode.None;
        SetZoom(zoom <= 0 ? 1.0 : zoom);
        if (_layout is not null)
        {
            Scroller.ChangeView(null, scrollFraction * _layout.TotalHeight, null, disableAnimation: true);
        }
    }

    private void ClearCaches()
    {
        foreach (var (bitmap, _) in _tiles.Values)
        {
            bitmap.Dispose();
        }
        _tiles.Clear();
        _tileLru.Clear();
        _tileBytes = 0;

        foreach (var preview in _previews.Values)
        {
            preview.Dispose();
        }
        _previews.Clear();
        _previewLru.Clear();
    }

    // ---------------------------------------------------------------- zoom & fit

    public void ZoomIn() => SetZoom(_zoom * 1.25);
    public void ZoomOut() => SetZoom(_zoom / 1.25);

    /// <summary>
    /// Sets zoom, keeping the document point under <paramref name="viewportAnchor"/>
    /// (default: viewport center) stationary on screen.
    /// </summary>
    public void SetZoom(double newZoom, Point? viewportAnchor = null, FitMode fitMode = FitMode.None)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        _fitMode = fitMode;
        if (_layout is null || Math.Abs(newZoom - _zoom) < 0.0001)
        {
            _zoom = newZoom;
            RebuildLayout();
            return;
        }

        var anchor = viewportAnchor ?? new Point(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);
        double ratio = newZoom / _zoom;
        double docX = Scroller.HorizontalOffset + anchor.X;
        double docY = Scroller.VerticalOffset + anchor.Y;

        _zoom = newZoom;
        RebuildLayout();
        Scroller.ChangeView(docX * ratio - anchor.X, docY * ratio - anchor.Y, null, disableAnimation: true);

        Canvas.Invalidate();
        UpdateDesiredTiles();
        ZoomChanged?.Invoke(this, _zoom);
    }

    private void ApplyFitMode()
    {
        if (_document is null || _pageSizes.Length == 0)
        {
            return;
        }

        double viewportWidth = Scroller.ViewportWidth > 50 ? Scroller.ViewportWidth : 800;
        double viewportHeight = Scroller.ViewportHeight > 50 ? Scroller.ViewportHeight : 600;
        double usableWidth = viewportWidth - 2 * PageLayout.Margin;
        double usableHeight = viewportHeight - 2 * PageLayout.Margin;

        var size = _pageSizes[Math.Clamp(_currentPage, 0, _pageSizes.Length - 1)];
        double pageW = _rotation % 2 == 0 ? size.Width : size.Height;
        double pageH = _rotation % 2 == 0 ? size.Height : size.Width;

        double zoom = _fitMode switch
        {
            FitMode.FitWidth => usableWidth / _pageSizes.Max(s => _rotation % 2 == 0 ? s.Width : s.Height),
            FitMode.FitPage => Math.Min(usableWidth / pageW, usableHeight / pageH),
            _ => _zoom,
        };
        // Anchor at the viewport's top-left so the reading position scales
        // proportionally — center-anchoring would scroll the view away from
        // the document top when a fit re-applies (e.g. on window resize).
        SetZoom(zoom, viewportAnchor: new Point(0, 0), fitMode: _fitMode);
    }

    public void RotateClockwise()
    {
        _rotation = (_rotation + 1) % 4;

        // Previews are rendered per-rotation; drop them along with stale tiles.
        ClearCaches();

        double positionRatio = _layout is { TotalHeight: > 0 }
            ? Scroller.VerticalOffset / _layout.TotalHeight
            : 0;
        RebuildLayout();
        if (_layout is not null)
        {
            Scroller.ChangeView(null, positionRatio * _layout.TotalHeight, null, disableAnimation: true);
        }
        if (_fitMode != FitMode.None)
        {
            ApplyFitMode();
        }
        Canvas.Invalidate();
        UpdateDesiredTiles();
    }

    // ---------------------------------------------------------------- navigation

    /// <summary>Scrolls to a page. Prev/next paging passes recordHistory:false; jumps pass true.</summary>
    public void GoToPage(int pageIndex, bool recordHistory = false)
    {
        if (_layout is null || _document is null)
        {
            return;
        }
        if (recordHistory)
        {
            PushHistory();
        }
        pageIndex = Math.Clamp(pageIndex, 0, _document.PageCount - 1);
        var rect = _layout.GetPageRect(pageIndex);
        Scroller.ChangeView(null, Math.Max(0, rect.Y - PageLayout.PageGap / 2), null, disableAnimation: true);
    }

    /// <summary>Smooth line-scroll for keyboard navigation (vim j/k).</summary>
    public void ScrollByLines(int lines)
        => Scroller.ChangeView(null, Scroller.VerticalOffset + lines * 60, null);

    /// <summary>Scroll by a fraction of the viewport height (Space / Shift+Space).</summary>
    public void ScrollByViewport(double fraction)
        => Scroller.ChangeView(null, Scroller.VerticalOffset + Scroller.ViewportHeight * fraction, null);

    public void ScrollHorizontally(int steps)
        => Scroller.ChangeView(Scroller.HorizontalOffset + steps * 60, null, null);

    private void PushHistory()
    {
        _back.Push(Scroller.VerticalOffset);
        _forward.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void GoBack()
    {
        if (_back.Count == 0)
        {
            return;
        }
        _forward.Push(Scroller.VerticalOffset);
        Scroller.ChangeView(null, _back.Pop(), null, disableAnimation: true);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void GoForward()
    {
        if (_forward.Count == 0)
        {
            return;
        }
        _back.Push(Scroller.VerticalOffset);
        Scroller.ChangeView(null, _forward.Pop(), null, disableAnimation: true);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- layout

    private void RebuildLayout()
    {
        if (_document is null)
        {
            _layout = null;
            Canvas.Width = 0;
            Canvas.Height = 0;
            return;
        }

        _layout = new PageLayout(_pageSizes, _zoom, _rotation, Scroller.ViewportWidth);
        Canvas.Width = _layout.TotalWidth;
        Canvas.Height = _layout.TotalHeight;
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitMode != FitMode.None)
        {
            ApplyFitMode();
        }
        else
        {
            RebuildLayout();
        }
        UpdateDesiredTiles();
    }

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateDesiredTiles();
        UpdateCurrentPage();
        PrefetchVisibleLinks();
    }

    /// <summary>
    /// Kick off link extraction for pages entering the viewport so the FIRST
    /// click on a link works — extracting lazily on pointer contact loses the
    /// race against the click itself.
    /// </summary>
    private void PrefetchVisibleLinks()
    {
        if (_layout is null || _document is null || _rotation != 0)
        {
            return;
        }
        var (first, last) = _layout.PagesInVerticalRange(
            Scroller.VerticalOffset, Scroller.VerticalOffset + Scroller.ViewportHeight);
        for (int i = first; i <= last; i++)
        {
            EnsureLinks(i);
        }
    }

    private void UpdateCurrentPage()
    {
        if (_layout is null)
        {
            return;
        }
        int page = _layout.PageAt(Scroller.VerticalOffset + Scroller.ViewportHeight * 0.4);
        if (page != _currentPage)
        {
            _currentPage = page;
            CurrentPageChanged?.Invoke(this, page);
        }
    }

    // ---------------------------------------------------------------- input

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            return;
        }

        int delta = e.GetCurrentPoint(Scroller).Properties.MouseWheelDelta;
        double factor = Math.Pow(1.1, delta / 120.0);
        SetZoom(_zoom * factor, e.GetCurrentPoint(Scroller).Position);
        e.Handled = true;
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var doc = DocumentPointFromPointer(e);

        if (_isSelecting)
        {
            UpdateSelection(doc);
            e.Handled = true;
            return;
        }

        SetLinkCursor(HitTestLink(doc) is not null);
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Canvas).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var docPoint = DocumentPointFromPointer(e);

        // Links take precedence over starting a selection.
        var link = HitTestLink(docPoint);
        if (link is not null)
        {
            if (link.IsInternal)
            {
                GoToPage(link.TargetPageIndex, recordHistory: true);
            }
            else if (link.Uri is { } uri)
            {
                LinkActivated?.Invoke(this, uri);
            }
            e.Handled = true;
            return;
        }

        BeginSelection(docPoint, e.Pointer);
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isSelecting)
        {
            _isSelecting = false;
            Canvas.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- selection

    private void BeginSelection(Point docPoint, Pointer pointer)
    {
        if (_layout is null || _document is null || _rotation != 0)
        {
            return; // selection geometry only tracked for the unrotated view
        }

        int page = _layout.PageAt(docPoint.Y);
        var (localX, localY) = ToPageLocal(page, docPoint);
        int charIndex = _document.CharIndexAt(page, localX, localY);

        // Clear any previous selection regardless of whether we hit a glyph.
        bool hadSelection = HasSelection;
        _selection = null;
        _selectionPage = page;
        _selectionAnchorChar = charIndex;

        if (charIndex >= 0)
        {
            _isSelecting = true;
            Canvas.CapturePointer(pointer);
        }
        if (hadSelection)
        {
            Canvas.Invalidate();
        }
    }

    private void UpdateSelection(Point docPoint)
    {
        if (_document is null || _selectionPage < 0 || _selectionAnchorChar < 0)
        {
            return;
        }

        // Selection stays on the page it started on (single-page selection).
        var (localX, localY) = ToPageLocal(_selectionPage, docPoint);
        int focusChar = _document.CharIndexAt(_selectionPage, localX, localY);
        if (focusChar < 0)
        {
            return;
        }

        var document = _document;
        int page = _selectionPage;
        int anchor = _selectionAnchorChar;
        Task.Run(() =>
        {
            var selection = document.GetSelection(page, anchor, focusChar);
            _dispatcher.TryEnqueue(() =>
            {
                if (_document == document && _isSelecting)
                {
                    _selection = selection;
                    Canvas.Invalidate();
                }
            });
        });
    }

    /// <summary>Document (layout) point → page-local points (top-left origin, unscaled).</summary>
    private (double X, double Y) ToPageLocal(int page, Point docPoint)
    {
        var rect = _layout!.GetPageRect(page);
        return ((docPoint.X - rect.X) / _zoom, (docPoint.Y - rect.Y) / _zoom);
    }

    public void ClearSelection()
    {
        if (HasSelection)
        {
            _selection = null;
            _selectionPage = -1;
            _selectionAnchorChar = -1;
            Canvas.Invalidate();
        }
    }

    // ---------------------------------------------------------------- search

    /// <summary>Shows all hits as highlights (grouped by page). Does not scroll.</summary>
    public void SetSearchResults(IReadOnlyList<SearchHit> hits)
    {
        _searchByPage.Clear();
        foreach (var hit in hits)
        {
            if (!_searchByPage.TryGetValue(hit.PageIndex, out var list))
            {
                _searchByPage[hit.PageIndex] = list = [];
            }
            list.Add(hit);
        }
        Canvas.Invalidate();
    }

    /// <summary>Emphasizes one hit and scrolls it into view.</summary>
    public void HighlightHit(SearchHit hit)
    {
        _activeHit = hit;
        if (_layout is not null && hit.Rects.Count > 0)
        {
            var pageRect = _layout.GetPageRect(hit.PageIndex);
            double hitTop = pageRect.Y + hit.Rects[0].Y * _zoom;
            // Center the hit in the viewport where possible.
            double target = Math.Max(0, hitTop - Scroller.ViewportHeight / 2);
            Scroller.ChangeView(null, target, null, disableAnimation: true);
        }
        Canvas.Invalidate();
    }

    public void ClearSearch()
    {
        _searchByPage.Clear();
        _activeHit = null;
        Canvas.Invalidate();
    }

    /// <summary>Pointer position translated into document (layout) space, in DIPs.</summary>
    private Point DocumentPointFromPointer(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Canvas).Position;
        return new Point(p.X, p.Y);
    }

    private PdfLink? HitTestLink(Point documentPoint)
    {
        if (_layout is null || _document is null || _rotation != 0)
        {
            return null; // link geometry is only tracked for the unrotated view
        }

        int page = _layout.PageAt(documentPoint.Y);
        EnsureLinks(page);
        if (!_links.TryGetValue(page, out var links) || links.Count == 0)
        {
            return null;
        }

        var rect = _layout.GetPageRect(page);
        // Document point → page-local points (undo centering offset and zoom).
        double localX = (documentPoint.X - rect.X) / _zoom;
        double localY = (documentPoint.Y - rect.Y) / _zoom;
        foreach (var link in links)
        {
            if (localX >= link.X && localX <= link.X + link.Width &&
                localY >= link.Y && localY <= link.Y + link.Height)
            {
                return link;
            }
        }
        return null;
    }

    private void EnsureLinks(int pageIndex)
    {
        if (_document is null || !_linksRequested.Add(pageIndex))
        {
            return;
        }
        var document = _document;
        Task.Run(() =>
        {
            try
            {
                var links = document.GetLinks(pageIndex);
                _dispatcher.TryEnqueue(() =>
                {
                    if (_document == document)
                    {
                        _links[pageIndex] = links;
                    }
                });
            }
            catch
            {
                // Link extraction is best-effort; ignore failures.
            }
        });
    }

    private void SetLinkCursor(bool overLink)
    {
        if (overLink == _pointerOverLink)
        {
            return;
        }
        _pointerOverLink = overLink;
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(
            overLink ? Microsoft.UI.Input.InputSystemCursorShape.Hand
                     : Microsoft.UI.Input.InputSystemCursorShape.Arrow);
    }

    // ---------------------------------------------------------------- drawing

    private void Canvas_CreateResources(CanvasVirtualControl sender, object args)
    {
        // First device creation or device-lost recovery: cached bitmaps are
        // tied to the old device and must be rebuilt from scratch.
        ClearCaches();
        if (_document is not null)
        {
            UpdateDesiredTiles();
        }
    }

    private void Canvas_RegionsInvalidated(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        foreach (var region in args.InvalidatedRegions)
        {
            using var session = sender.CreateDrawingSession(region);
            DrawRegion(session, region);
        }
        UpdateDesiredTiles();
    }

    private void DrawRegion(CanvasDrawingSession session, Rect region)
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        session.Clear(dark ? Color.FromArgb(255, 25, 25, 28) : Color.FromArgb(255, 240, 240, 244));

        if (_layout is null || _document is null)
        {
            return;
        }

        var (first, last) = _layout.PagesInVerticalRange(region.Y, region.Bottom);
        if (last < first)
        {
            return;
        }

        float scale = RenderScale;
        int scaleKey = TileKey.ToScaleKey(scale);
        double rasterScale = DisplayScale;
        var borderColor = dark ? Color.FromArgb(255, 60, 60, 66) : Color.FromArgb(255, 200, 200, 205);

        for (int i = first; i <= last; i++)
        {
            var pageRect = _layout.GetPageRect(i);
            var pageXamlRect = new Rect(pageRect.X, pageRect.Y, pageRect.Width, pageRect.Height);

            session.FillRectangle(pageXamlRect, _nightMode ? Colors.Black : Colors.White);

            if (_previews.TryGetValue(i, out var preview))
            {
                DrawPageImage(session, preview, pageXamlRect);
            }

            var (pagePxW, pagePxH) = _document.GetPagePixelSize(i, scale, _rotation);
            var (cols, rows) = TileMath.GridFor(pagePxW, pagePxH);
            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    var key = new TileKey(i, scaleKey, _rotation, row, col, IsPreview: false);
                    if (_tiles.TryGetValue(key, out var entry))
                    {
                        var (srcX, srcY, w, h) = TileMath.TileWindow(pagePxW, pagePxH, row, col);
                        var dest = new Rect(
                            pageRect.X + srcX / rasterScale,
                            pageRect.Y + srcY / rasterScale,
                            w / rasterScale,
                            h / rasterScale);
                        DrawPageImage(session, entry.Bitmap, dest);
                        TouchTile(key);
                    }
                }
            }

            DrawHighlights(session, i, pageRect);
            session.DrawRectangle(pageXamlRect, borderColor, 1);
        }
    }

    /// <summary>
    /// Draws page content, inverting colors on the GPU in night mode. Cached
    /// tiles stay in normal colors, so toggling costs nothing but a repaint.
    /// </summary>
    private void DrawPageImage(CanvasDrawingSession session, CanvasBitmap bitmap, Rect dest)
    {
        if (_nightMode)
        {
            session.DrawImage(
                new Microsoft.Graphics.Canvas.Effects.InvertEffect { Source = bitmap },
                dest, bitmap.Bounds);
        }
        else
        {
            session.DrawImage(bitmap, dest);
        }
    }

    private static readonly Color SelectionColor = Color.FromArgb(90, 0, 120, 215);   // translucent blue
    private static readonly Color SearchColor = Color.FromArgb(110, 255, 210, 0);      // translucent yellow
    private static readonly Color ActiveSearchColor = Color.FromArgb(150, 255, 140, 0); // orange

    private void DrawHighlights(CanvasDrawingSession session, int pageIndex, DipRect pageRect)
    {
        // Search hits (only visible pages carry entries).
        if (_searchByPage.TryGetValue(pageIndex, out var hits))
        {
            foreach (var hit in hits)
            {
                bool active = ReferenceEquals(hit, _activeHit) ||
                              (_activeHit is not null && hit.PageIndex == _activeHit.PageIndex && hit.CharIndex == _activeHit.CharIndex);
                var color = active ? ActiveSearchColor : SearchColor;
                foreach (var r in hit.Rects)
                {
                    session.FillRectangle(HighlightRect(pageRect, r), color);
                }
            }
        }

        // Text selection (single page).
        if (_selection is { Count: > 0 } selection && selection.PageIndex == pageIndex)
        {
            foreach (var r in selection.Rects)
            {
                session.FillRectangle(HighlightRect(pageRect, r), SelectionColor);
            }
        }
    }

    private Rect HighlightRect(DipRect pageRect, TextRect r) =>
        new(pageRect.X + r.X * _zoom, pageRect.Y + r.Y * _zoom, r.Width * _zoom, r.Height * _zoom);

    // ---------------------------------------------------------------- tile pipeline

    /// <summary>
    /// Recomputes the full prioritized want-list (visible tiles â†’ previews â†’
    /// prefetch) and hands it to the render thread, replacing the old list.
    /// </summary>
    private void UpdateDesiredTiles()
    {
        if (_layout is null || _document is null)
        {
            _scheduler.SetDesired([]);
            return;
        }

        var viewport = new DipRect(Scroller.HorizontalOffset, Scroller.VerticalOffset, Scroller.ViewportWidth, Scroller.ViewportHeight);
        var extended = new DipRect(viewport.X, viewport.Y - viewport.Height * 0.5, viewport.Width, viewport.Height * 2);

        float scale = RenderScale;
        int scaleKey = TileKey.ToScaleKey(scale);
        var visible = new List<TileRequest>();
        var previews = new List<TileRequest>();
        var prefetch = new List<TileRequest>();

        var (first, last) = _layout.PagesInVerticalRange(extended.Y, extended.Bottom);
        for (int i = first; i <= last && last >= first; i++)
        {
            var pageRect = _layout.GetPageRect(i);
            var (pagePxW, pagePxH) = _document.GetPagePixelSize(i, scale, _rotation);
            var (cols, rows) = TileMath.GridFor(pagePxW, pagePxH);

            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    var (srcX, srcY, w, h) = TileMath.TileWindow(pagePxW, pagePxH, row, col);
                    var tileRect = new DipRect(
                        pageRect.X + srcX / DisplayScale,
                        pageRect.Y + srcY / DisplayScale,
                        w / DisplayScale,
                        h / DisplayScale);
                    if (!tileRect.Intersects(extended))
                    {
                        continue;
                    }

                    var key = new TileKey(i, scaleKey, _rotation, row, col, IsPreview: false);
                    if (_tiles.ContainsKey(key))
                    {
                        continue;
                    }

                    var request = new TileRequest(key, scale, srcX, srcY, w, h);
                    (tileRect.Intersects(viewport) ? visible : prefetch).Add(request);
                }
            }

            if (!_previews.ContainsKey(i))
            {
                var size = _pageSizes[i];
                float pageWidthPt = _rotation % 2 == 0 ? size.Width : size.Height;
                float previewScale = PreviewTargetWidthPx / pageWidthPt;
                var (pw, ph) = _document.GetPagePixelSize(i, previewScale, _rotation);
                previews.Add(new TileRequest(
                    new TileKey(i, TileKey.ToScaleKey(previewScale), _rotation, 0, 0, IsPreview: true),
                    previewScale, 0, 0, pw, ph));
            }
        }

        visible.AddRange(previews);
        visible.AddRange(prefetch);
        _scheduler.SetDesired(visible);
    }

    /// <summary>Called on the render thread; hop to the UI thread to touch caches.</summary>
    private void OnTileRendered(TileRequest request, PageBitmap bitmap)
    {
        bool queued = _dispatcher.TryEnqueue(() =>
        {
            try
            {
                HandleRenderedTile(request, bitmap);
            }
            finally
            {
                bitmap.Return(); // pixels are on the GPU (or discarded) — recycle the buffer
            }
        });
        if (!queued)
        {
            bitmap.Return();
        }
    }

    private void HandleRenderedTile(TileRequest request, PageBitmap bitmap)
    {
        if (_document is null || _layout is null || request.Key.Rotation != _rotation)
        {
            return;
        }

        // A zoom change while this tile was in flight makes it useless.
        if (!request.Key.IsPreview && request.Key.ScaleKey != TileKey.ToScaleKey(RenderScale))
        {
            return;
        }

        CanvasBitmap canvasBitmap;
        try
        {
            canvasBitmap = CanvasBitmap.CreateFromBytes(
                Canvas, bitmap.Pixels, bitmap.Width, bitmap.Height,
                DirectXPixelFormat.B8G8R8A8UIntNormalized);
        }
        catch (Exception)
        {
            return; // device lost mid-flight; CreateResources will rebuild
        }

        if (request.Key.IsPreview)
        {
            InsertPreview(request.Key.PageIndex, canvasBitmap);
        }
        else
        {
            InsertTile(request.Key, canvasBitmap);
        }

        // Repaint just this tile's area.
        var pageRect = _layout.GetPageRect(request.Key.PageIndex);
        double rs = DisplayScale;
        var dirty = request.Key.IsPreview
            ? new Rect(pageRect.X, pageRect.Y, pageRect.Width, pageRect.Height)
            : new Rect(
                pageRect.X + request.SrcX / rs,
                pageRect.Y + request.SrcY / rs,
                request.WidthPx / rs,
                request.HeightPx / rs);
        Canvas.Invalidate(dirty);
    }

    private void InsertTile(TileKey key, CanvasBitmap bitmap)
    {
        if (_tiles.TryGetValue(key, out var existing))
        {
            _tileBytes -= (long)existing.Bitmap.SizeInPixels.Width * existing.Bitmap.SizeInPixels.Height * 4;
            _tileLru.Remove(existing.Node);
            existing.Bitmap.Dispose();
        }

        var node = _tileLru.AddFirst(key);
        _tiles[key] = (bitmap, node);
        _tileBytes += (long)bitmap.SizeInPixels.Width * bitmap.SizeInPixels.Height * 4;

        while (_tileBytes > TileCacheBudgetBytes && _tileLru.Last is { } oldest)
        {
            var (evicted, evictedNode) = _tiles[oldest.Value];
            _tileBytes -= (long)evicted.SizeInPixels.Width * evicted.SizeInPixels.Height * 4;
            _tiles.Remove(oldest.Value);
            _tileLru.Remove(evictedNode);
            evicted.Dispose();
        }
    }

    private void TouchTile(TileKey key)
    {
        if (_tiles.TryGetValue(key, out var entry))
        {
            _tileLru.Remove(entry.Node);
            _tiles[key] = (entry.Bitmap, _tileLru.AddFirst(key));
        }
    }

    private void InsertPreview(int pageIndex, CanvasBitmap bitmap)
    {
        if (_previews.TryGetValue(pageIndex, out var existing))
        {
            existing.Dispose();
            _previewLru.Remove(pageIndex);
        }

        _previews[pageIndex] = bitmap;
        _previewLru.AddFirst(pageIndex);

        while (_previewLru.Count > PreviewCacheCapacity && _previewLru.Last is { } oldest)
        {
            _previews[oldest.Value].Dispose();
            _previews.Remove(oldest.Value);
            _previewLru.RemoveLast();
        }
    }
}
