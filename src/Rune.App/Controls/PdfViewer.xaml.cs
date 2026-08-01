using Rune.Engine;
using Rune.Styles;
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
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

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
    // A full 1024x1024 BGRA tile is 4 MB, so this is ~64 tiles. At maximum zoom
    // one 612pt page alone needs a 5x5 grid, and two pages can be visible — the
    // old 128 MB budget made those evict each other before they were ever drawn.
    private const long TileCacheBudgetBytes = 256L * 1024 * 1024;
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
    // ScaleKey travels with the bitmap so a page whose preview tier changed
    // (see PreviewWidthFor) re-requests a sharper one instead of keeping the
    // low-res placeholder forever.
    private readonly Dictionary<int, (CanvasBitmap Bitmap, int ScaleKey)> _previews = [];
    private readonly LinkedList<int> _previewLru = [];

    // Cached rasters that are out of date but still worth drawing until their
    // replacement arrives — see RefreshPageTiles.
    private readonly HashSet<TileKey> _staleTiles = [];
    private readonly HashSet<int> _stalePreviews = [];

    private double _zoom = 1.0;
    private int _rotation;
    private FitMode _fitMode = FitMode.FitWidth;
    private int _currentPage;
    private bool _nightMode;

    // A document can arrive before the ScrollViewer has been measured (fast
    // opens, lazy tabs). Building the layout against a 0-width viewport makes
    // PageLayout.TotalWidth collapse to the page width and the ScrollViewer
    // left-align the canvas ("page stuck to the left"). Instead, remember the
    // intent and apply it on the first real SizeChanged.
    private bool _pendingFit;
    private (double Zoom, int Rotation, double Fraction)? _pendingRestore;

    // ScrollViewer.ViewportWidth/Height are refreshed during the ScrollViewer's
    // OWN arrange pass, which runs *after* its SizeChanged event. Reading them
    // inside SizeChanged therefore yields the previous size — which laid the
    // document out against a stale width (page left-aligned, stale tiles) every
    // time the sidebar toggled. SizeChanged hands us the authoritative new size,
    // so we prefer it for the duration of that handler only.
    private Size _sizeChangedViewport;
    private double ViewportW => _sizeChangedViewport.Width > 0 ? _sizeChangedViewport.Width : Scroller.ViewportWidth;
    private double ViewportH => _sizeChangedViewport.Height > 0 ? _sizeChangedViewport.Height : Scroller.ViewportHeight;

    private bool ViewportReady => ViewportW > 50 && ViewportH > 50;

    // Link hit-testing: per-page links, extracted lazily off the UI thread.
    private readonly Dictionary<int, IReadOnlyList<PdfLink>> _links = [];
    private readonly HashSet<int> _linksRequested = [];
    private bool _pointerOverLink;

    // Per-page text maps (text + char boxes) so selection hit-testing never
    // calls PDFium on the pointer path. Prefetched for visible pages.
    private const int PageTextCacheCapacity = 16;
    private readonly Dictionary<int, PageText> _pageTexts = [];
    private readonly LinkedList<int> _pageTextLru = [];
    private readonly HashSet<int> _pageTextRequested = [];

    // Coalesces desired-tile recomputes during scrolling (they're O(visible
    // tiles) with list allocations — running them on every scroll tick is
    // measurable churn).
    private readonly DispatcherQueueTimer _desiredTimer;

    // One reusable invert effect for night mode (allocating per tile per draw
    // churns the GC during scrolling).
    private Microsoft.Graphics.Canvas.Effects.InvertEffect? _invertEffect;

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

    // Annotation tools.
    private AnnotationTool _activeTool = AnnotationTool.None;

    // Freehand ink.
    private bool _isDrawingInk;
    private int _inkPage = -1;
    private readonly List<Point> _inkPoints = [];   // document (layout) space
    private Color _inkColor = Color.FromArgb(255, 226, 34, 34);
    private double _inkWidthPt = 2.5;

    // Text markup (the highlighter tool, which also does underline/strikeout).
    private MarkupKind _markupKind = MarkupKind.Highlight;
    private Color _markupColor = Color.FromArgb(255, 255, 210, 0);
    private byte _markupAlpha = 102;

    public event EventHandler<int>? CurrentPageChanged;
    public event EventHandler<double>? ZoomChanged;

    /// <summary>Raised after a rotation, with the new quarter-turn count (0–3).</summary>
    public event EventHandler<int>? RotationChanged;

    /// <summary>Raised when the user presses to start drawing, so transient UI (the pen panel) can get out of the way.</summary>
    public event EventHandler? InkStrokeStarted;

    /// <summary>Raised when the armed tool changes, so the toolbar can re-sync its buttons.</summary>
    public event EventHandler<AnnotationTool>? ActiveToolChanged;
    public event EventHandler? HistoryChanged;

    /// <summary>Raised when the user clicks an external (URI) link. The shell decides whether to open it.</summary>
    public event EventHandler<string>? LinkActivated;

    /// <summary>Raised after any annotation edit (dirty state changed).</summary>
    public event EventHandler? DocumentEdited;

    /// <summary>Raised when night mode toggles, so sidebar thumbnails can re-render inverted.</summary>
    public event EventHandler? NightModeChanged;

    /// <summary>Raised after an annotation add/delete with its undo/redo actions (consumed by the undo stack).</summary>
    public event EventHandler<AnnotationEditEventArgs>? AnnotationEdited;

    /// <summary>Raised when the user asks for a note at (pageIndex, x, y in top-left page points).</summary>
    public event EventHandler<(int PageIndex, double X, double Y)>? NoteRequested;

    public int CurrentPage => _currentPage;
    public double Zoom => _zoom;
    public int ViewRotation => _rotation;
    public int PageCount => _document?.PageCount ?? 0;
    public PdfDocument? Document => _document;

    /// <summary>
    /// The render-thread work queue. ALL PDFium work for this document must go
    /// through it (or the desired-tile list) — never call the document
    /// directly from the UI thread or the thread pool.
    /// </summary>
    internal IPdfWorkQueue WorkQueue => _scheduler;

    internal Task<T> RunOnRenderThreadAsync<T>(PdfWorkPriority priority, Func<T> operation)
        => _scheduler.RunAsync(priority, operation);

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Current scroll position as a 0–1 fraction of total document height (for session restore).</summary>
    public double ScrollFraction =>
        _layout is { TotalHeight: > 0 } ? Scroller.VerticalOffset / _layout.TotalHeight : 0;

    /// <summary>The currently selected text, or empty if nothing is selected.</summary>
    public string SelectedText => _selection?.Text ?? string.Empty;
    public bool HasSelection => (_selection?.Count ?? 0) > 0;

    /// <summary>
    /// The armed annotation tool. <see cref="AnnotationTool.None"/> is ordinary
    /// reading: drag selects text. Anything else changes what a drag does, and
    /// stays armed until the user picks another tool or presses Esc.
    /// </summary>
    public AnnotationTool ActiveTool
    {
        get => _activeTool;
        set
        {
            if (_activeTool == value)
            {
                return;
            }
            _activeTool = value;
            if (_isDrawingInk && value != AnnotationTool.Pen)
            {
                CancelInkStroke();
            }
            ClearSelectionState();
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(CursorFor(value));
            ActiveToolChanged?.Invoke(this, value);
        }
    }

    private static Microsoft.UI.Input.InputSystemCursorShape CursorFor(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Pen or AnnotationTool.Highlighter => Microsoft.UI.Input.InputSystemCursorShape.Cross,
        // A note is placed at a point, and the eraser and signature both act on
        // whatever is under the pointer — a hand reads as "click a thing".
        AnnotationTool.Note or AnnotationTool.Signature => Microsoft.UI.Input.InputSystemCursorShape.Cross,
        AnnotationTool.Eraser => Microsoft.UI.Input.InputSystemCursorShape.Hand,
        _ => Microsoft.UI.Input.InputSystemCursorShape.Arrow,
    };

    /// <summary>True while the freehand pen is armed. Kept for the ink drawing paths.</summary>
    private bool IsInkMode => _activeTool == AnnotationTool.Pen;

    /// <summary>Sets the pen color (#RRGGBB) and width (points) for new ink strokes.</summary>
    public void SetInkStyle(string hexColor, double widthPoints)
    {
        _inkColor = ParseHexColor(hexColor, _inkColor);
        _inkWidthPt = widthPoints > 0 ? widthPoints : _inkWidthPt;
    }

    /// <summary>
    /// Style for the text-markup tool: which mark to make, its colour, and how
    /// opaque. The engine has always taken a full alpha byte on
    /// <c>AddMarkup</c>; only the viewer hardcoded it.
    /// </summary>
    public void SetMarkupStyle(MarkupKind kind, string hexColor, double opacity)
    {
        _markupKind = kind;
        _markupColor = ParseHexColor(hexColor, _markupColor);
        _markupAlpha = (byte)Math.Clamp(Math.Round(opacity * 255), 1, 255);
    }

    private static Color ParseHexColor(string hex, Color fallback)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6 &&
            byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out byte r) &&
            byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out byte g) &&
            byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out byte b))
        {
            return Color.FromArgb(255, r, g, b);
        }
        return fallback;
    }

    /// <summary>Invert page colors for night reading. Draw-time effect; cheap to toggle.</summary>
    public bool NightMode
    {
        get => _nightMode;
        set
        {
            if (_nightMode != value)
            {
                _nightMode = value;
                // The surround follows night mode too, so the background brush
                // has to be re-applied here and not only on a theme change.
                ApplyViewerBackground();
                Canvas.Invalidate();
                NightModeChanged?.Invoke(this, EventArgs.Empty);
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

        _desiredTimer = _dispatcher.CreateTimer();
        _desiredTimer.Interval = TimeSpan.FromMilliseconds(50);
        _desiredTimer.IsRepeating = false;
        _desiredTimer.Tick += (_, _) =>
        {
            UpdateDesiredTiles();
            UpdateCurrentPage();
            PrefetchVisiblePageData();
        };

        Canvas.PointerMoved += Canvas_PointerMoved;
        Canvas.PointerPressed += Canvas_PointerPressed;
        Canvas.PointerReleased += Canvas_PointerReleased;
        Canvas.RightTapped += Canvas_RightTapped;
        Canvas.PointerExited += (_, _) => SetLinkCursor(false);

        // On the Canvas, not the ScrollViewer. PointerWheelChanged bubbles from
        // the hit-test target upward, so handling it on the child runs BEFORE
        // the ScrollViewer's own Ctrl+wheel zoom and marking it handled stops
        // that from happening at all. Attaching to the ScrollViewer instead
        // (with handledEventsToo) meant both zoomed: the real zoom stepped by
        // 1.1 and the ScrollViewer's ZoomFactor was then folded in on top, so
        // every notch zoomed roughly twice as far as asked.
        Canvas.PointerWheelChanged += Canvas_PointerWheelChanged;

        // Tracks whether a pinch is genuinely in flight, so the ZoomFactor
        // self-heal can tell "still gesturing" from "gesture ended and its
        // settling event never arrived".
        Scroller.DirectManipulationStarted += (_, _) => _manipulating = true;
        Scroller.DirectManipulationCompleted += (_, _) =>
        {
            _manipulating = false;
            UpdateDesiredTiles(); // picks up the rebase if ViewChanged didn't
        };

        // Form fields need real keyboard focus, which needs a tab stop. Set on
        // the control rather than the canvas so CharacterReceived bubbles here.
        IsTabStop = true;
        UseSystemFocusVisuals = false; // the field draws its own focus ring
        CharacterReceived += PdfViewer_CharacterReceived;
        KeyDown += PdfViewer_KeyDown;
        LostFocus += (_, _) => KillFormFocus();

        ApplyViewerBackground();
        ActualThemeChanged += (_, _) =>
        {
            ApplyViewerBackground();
            Canvas.Invalidate();
        };
        Unloaded += (_, _) => _scheduler.Dispose();
    }

    private double DisplayScale => XamlRoot?.RasterizationScale ?? 1.0;
    private float RenderScale => (float)(_zoom * DisplayScale);

    // TEMP diagnostics: set RUNE_DEBUG=1 to trace the tile pipeline to %TEMP%\rune-debug.log
    private static readonly bool DebugLogEnabled = Environment.GetEnvironmentVariable("RUNE_DEBUG") == "1";
    private static void DebugLog(string message)
    {
        if (DebugLogEnabled)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "rune-debug.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- document

    /// <summary>Shows a document (ownership stays with the caller) or clears the view.</summary>
    public void SetDocument(PdfDocument? document)
    {
        // Commit any in-flight field edit before letting go of the old document.
        if (_document is not null && IsFormFieldFocused)
        {
            _document.FormKillFocus();
            _formFocusPage = -1;
        }
        if (_document is not null)
        {
            _document.FormPageInvalidated -= OnFormPageInvalidated;
        }

        _document = document;
        ClearFormState();
        if (document is not null)
        {
            document.FormPageInvalidated += OnFormPageInvalidated;
        }

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
        ClearPageTextCache();
        _selection = null;
        _selectionPage = -1;
        _selectionAnchorChar = -1;
        _isSelecting = false;
        _searchByPage.Clear();
        _activeHit = null;

        _scheduler.SetDocument(document);
        ClearCaches();
        _pendingRestore = null;
        _pendingFit = false;
        if (document is null || ViewportReady)
        {
            ApplyFitMode();      // no-op for null; RebuildLayout below clears the layout
            if (document is null)
            {
                RebuildLayout();
            }
        }
        else
        {
            // Not measured yet: drop the old layout and wait for SizeChanged.
            _layout = null;
            Canvas.Width = 0;
            Canvas.Height = 0;
            _pendingFit = true;
        }
        Scroller.ChangeView(0, 0, null, disableAnimation: true);
        Canvas.Invalidate();
        UpdateDesiredTiles();
        PrefetchVisiblePageData(); // ChangeView(0,0) on a fresh doc fires no ViewChanged
        CurrentPageChanged?.Invoke(this, 0);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Restores a saved reading position after the layout is ready.</summary>
    public void RestoreView(double zoom, int rotation, double scrollFraction)
    {
        if (!ViewportReady)
        {
            _pendingRestore = (zoom, rotation, scrollFraction);
            _pendingFit = false;
            return; // replayed on the first real SizeChanged
        }
        _rotation = ViewRotationMath.Normalize(rotation);
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

        foreach (var (preview, _) in _previews.Values)
        {
            preview.Dispose();
        }
        _previews.Clear();
        _previewLru.Clear();

        // Nothing is cached any more, so nothing can be stale. Leaving these
        // populated would make UpdateDesiredTiles re-request tiles that no
        // longer exist, forever — the sets grew on every rotation, device-loss
        // and document swap.
        _staleTiles.Clear();
        _stalePreviews.Clear();

        // The effect holds a reference to a (possibly device-lost) bitmap.
        _invertEffect?.Dispose();
        _invertEffect = null;
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

        var viewportPoint = viewportAnchor ?? new Point(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);

        // Capture in PAGE space before the layout changes. Scaling the scroll
        // offset by the zoom ratio instead would assume document space scales
        // purely with zoom — it doesn't, and the error accumulates once per
        // page gap (see ZoomAnchor).
        var anchor = ZoomAnchor.Capture(
            _layout, Scroller.HorizontalOffset, Scroller.VerticalOffset, viewportPoint.X, viewportPoint.Y);

        _zoom = newZoom;
        RebuildLayout();
        ScrollToAnchor(anchor, viewportPoint);

        Canvas.Invalidate();
        UpdateDesiredTiles();
        ZoomChanged?.Invoke(this, _zoom);
    }

    /// <summary>
    /// Scrolls so a page-space anchor lands back under a viewport position.
    ///
    /// <c>UpdateLayout()</c> first is load-bearing: <see cref="RebuildLayout"/>
    /// has only just assigned <c>Canvas.Width/Height</c>, and until the
    /// ScrollViewer re-measures, <c>ChangeView</c> clamps the requested offset
    /// against the OLD extent — so zooming in asks to scroll further than the
    /// previous layout allowed and silently lands short.
    /// </summary>
    private void ScrollToAnchor(PageAnchor anchor, Point viewportPoint, float? zoomFactor = null)
    {
        if (_layout is null)
        {
            return;
        }

        Scroller.UpdateLayout();
        var (x, y) = ZoomAnchor.Restore(_layout, anchor, viewportPoint.X, viewportPoint.Y);

        if (Scroller.ChangeView(Math.Max(0, x), Math.Max(0, y), zoomFactor, disableAnimation: true))
        {
            return;
        }

        // ChangeView refuses (and returns false) if the ScrollViewer isn't in a
        // state to move yet. Silently ignoring that leaves the view parked in
        // the wrong place, so try once more after the current layout pass.
        _dispatcher.TryEnqueue(() =>
        {
            if (_layout is not null)
            {
                var (retryX, retryY) = ZoomAnchor.Restore(_layout, anchor, viewportPoint.X, viewportPoint.Y);
                Scroller.ChangeView(Math.Max(0, retryX), Math.Max(0, retryY), zoomFactor, disableAnimation: true);
            }
        });
    }

    private void ApplyFitMode()
    {
        if (_document is null || _pageSizes.Length == 0)
        {
            return;
        }

        if (!ViewportReady)
        {
            _pendingFit = true; // never fit against a guessed viewport size
            return;
        }
        double usableWidth = ViewportW - 2 * PageLayout.Margin;
        double usableHeight = ViewportH - 2 * PageLayout.Margin;

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

    public void RotateClockwise() => Rotate(+1);

    public void RotateCounterClockwise() => Rotate(-1);

    /// <summary>
    /// Rotates the view by whole quarter turns (negative = counter-clockwise).
    /// Everything derived from page geometry is dropped and rebuilt — that
    /// invalidation is load-bearing (it is the fix for the v0.2 "rotate shows
    /// blank pages" bug), so keep it together.
    /// </summary>
    public void Rotate(int quarterTurns)
    {
        int next = ViewRotationMath.Normalize(_rotation + quarterTurns);
        if (next == _rotation)
        {
            return;
        }
        _rotation = next;

        // Selection, search highlights, and link rects are all in unrotated
        // text coordinates — stale after rotation. Drop them.
        _selection = null;
        _selectionPage = -1;
        _selectionAnchorChar = -1;
        _isSelecting = false;
        _searchByPage.Clear();
        _activeHit = null;
        _links.Clear();
        _linksRequested.Clear();
        ClearPageTextCache();

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
        RotationChanged?.Invoke(this, _rotation);
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

        _layout = new PageLayout(_pageSizes, _zoom, _rotation, ViewportW, ViewportH);
        Canvas.Width = _layout.TotalWidth;
        Canvas.Height = _layout.TotalHeight;
        // Pages just moved (re-centered / re-scaled). Without this, whatever the
        // virtual canvas already painted stays on screen as ghost fragments —
        // only newly-invalidated regions would be redrawn.
        Canvas.Invalidate();
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Use the size the event carries, not the ScrollViewer's own (still
        // stale) ViewportWidth/Height — see _sizeChangedViewport.
        _sizeChangedViewport = e.NewSize;
        try
        {
            if (!ViewportReady)
            {
                return;
            }

            if (_pendingRestore is { } restore)
            {
                // A session position arrived before the first measure; replay it
                // now that the viewport is real (zoom → layout → scroll ordering).
                _pendingRestore = null;
                _pendingFit = false;
                RestoreView(restore.Zoom, restore.Rotation, restore.Fraction);
            }
            else if (_pendingFit)
            {
                _pendingFit = false;
                ApplyFitMode();
            }
            else if (_fitMode != FitMode.None)
            {
                ApplyFitMode();
            }
            else
            {
                // Free zoom: keep the zoom, but re-centre for the new width.
                RebuildLayout();
            }
            UpdateDesiredTiles();
            PrefetchVisiblePageData();
        }
        finally
        {
            // Later calls must read the live (by then correct) viewport.
            _sizeChangedViewport = default;
        }
    }

    private bool _rebasingZoom;

    /// <summary>True while a touch/touchpad pinch is actually in flight.</summary>
    private bool _manipulating;

    /// <summary>Reentry guard for the ZoomFactor self-heal in UpdateDesiredTiles.</summary>
    private bool _healingZoom;

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_rebasingZoom)
        {
            return;
        }

        // Pinch (touch/touchpad) and Ctrl+wheel zoom arrive as ScrollViewer
        // ZoomFactor changes: the canvas raster-scales during the gesture
        // (instant, slightly blurry), and when the gesture settles we fold
        // the factor into the real zoom and re-render crisp ("rebase").
        float factor = Scroller.ZoomFactor;
        if (Math.Abs(factor - 1f) > 0.001f)
        {
            if (!e.IsIntermediate)
            {
                RebaseZoom(factor);
            }
            return;
        }

        if (e.IsIntermediate)
        {
            // Mid-scroll: coalesce the (allocating) want-list recompute; the
            // 50 ms timer keeps tiles flowing without per-tick churn.
            RequestDesiredUpdate();
        }
        else
        {
            UpdateDesiredTiles();
            UpdateCurrentPage();
            PrefetchVisiblePageData();
        }
    }

    /// <summary>Coalesced <see cref="UpdateDesiredTiles"/> (plus page/prefetch upkeep).</summary>
    private void RequestDesiredUpdate()
    {
        if (!_desiredTimer.IsRunning)
        {
            _desiredTimer.Start();
        }
    }

    private void RebaseZoom(float factor)
    {
        if (_layout is null)
        {
            return;
        }

        _rebasingZoom = true;
        try
        {
            double newZoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);

            // Mid-gesture the offsets are in ZoomFactor-scaled space, so divide
            // the factor out to get the content point at the viewport origin in
            // layout units, then capture it in page space. Anchoring on the
            // scaled offset directly is what used to make a pinch drift.
            var anchor = ZoomAnchor.Capture(
                _layout, Scroller.HorizontalOffset / factor, Scroller.VerticalOffset / factor, 0, 0);

            _zoom = newZoom;
            _fitMode = FitMode.None;
            RebuildLayout();
            // Resetting ZoomFactor to 1 in the same call is what re-enables the
            // tile pipeline — UpdateDesiredTiles refuses to run while it's off 1.
            ScrollToAnchor(anchor, new Point(0, 0), zoomFactor: 1f);
        }
        finally
        {
            _rebasingZoom = false;
        }

        Canvas.Invalidate();
        UpdateDesiredTiles();
        UpdateCurrentPage();
        ZoomChanged?.Invoke(this, _zoom);
    }

    /// <summary>
    /// Kick off link + text-map extraction for pages entering the viewport so
    /// the FIRST click (on a link, or starting a selection) works — extracting
    /// lazily on pointer contact loses the race against the click itself.
    /// </summary>
    private void PrefetchVisiblePageData()
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
            EnsurePageText(i);
            EnsureFormFields(i);
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

    /// <summary>
    /// Ctrl+wheel zoom, handled here rather than by the ScrollViewer.
    ///
    /// The ScrollViewer's own Ctrl+wheel zoom raster-scales the canvas and only
    /// becomes crisp again when <see cref="RebaseZoom"/> folds the factor back
    /// in — which meant every notch flashed blurry, and if the settling
    /// ViewChanged was ever missed the page stayed blurry indefinitely, because
    /// UpdateDesiredTiles refuses to request tiles while ZoomFactor is off 1.
    /// Zooming directly keeps ZoomFactor at 1 throughout, so tiles are
    /// requested at the new scale immediately and the cursor stays anchored
    /// exactly.
    ///
    /// Pinch still goes through the ScrollViewer, which genuinely needs the
    /// live continuous scaling.
    /// </summary>
    private void Canvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Scroller);
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl || point.Properties.MouseWheelDelta == 0 || _layout is null)
        {
            return; // plain wheel: let it bubble to the ScrollViewer and scroll
        }
        DebugLog($"WHEEL delta={point.Properties.MouseWheelDelta} pos=({point.Position.X:0},{point.Position.Y:0}) zoom={_zoom:0.###}");

        // One notch is 120. Multiplicative so each step feels the same at any
        // zoom level, and fractional deltas (precision touchpads) scale down.
        double steps = point.Properties.MouseWheelDelta / 120.0;
        double factor = Math.Pow(1.1, steps);

        SetZoom(_zoom * factor, viewportAnchor: point.Position);
        e.Handled = true;
    }

    // ---------------------------------------------------------------- annotations

    /// <summary>
    /// Applies a markup annotation to the current text selection. The PDFium
    /// write runs on the render thread (never block the UI on the PDFium lock);
    /// the page refreshes when it completes.
    /// </summary>
    public void MarkupSelection(MarkupKind kind)
    {
        // The classic defaults, used by Ctrl+H, the palette and the right-click
        // menu: semi-transparent yellow for highlights, solid red for line
        // markup. The highlighter tool passes its own style instead.
        var (color, alpha) = kind == MarkupKind.Highlight
            ? (Color.FromArgb(255, 255, 210, 0), (byte)102)
            : (Color.FromArgb(255, 220, 30, 30), (byte)255);
        MarkupSelection(kind, color, alpha);
    }

    /// <inheritdoc cref="MarkupSelection(MarkupKind)"/>
    public async void MarkupSelection(MarkupKind kind, Color color, byte alpha)
    {
        if (_document is null || _selection is not { Count: > 0 } selection || _rotation != 0)
        {
            return;
        }

        var document = _document;
        int page = selection.PageIndex;
        var rects = selection.Rects;
        ClearSelectionState();
        Canvas.Invalidate();

        AnnotationSpec? spec;
        try
        {
            spec = await _scheduler.RunAsync(PdfWorkPriority.Interactive, () =>
            {
                document.AddMarkup(page, kind, rects, color.R, color.G, color.B, alpha);
                return document.CaptureLastAnnotation(page);
            });
        }
        catch
        {
            return; // document swapped or closed mid-edit
        }
        if (_document != document)
        {
            return;
        }
        InvalidatePage(page);
        DocumentEdited?.Invoke(this, EventArgs.Empty);
        RaiseAnnotationAdded(kind.ToString().ToLowerInvariant(), page, spec);
    }

    /// <summary>Pushes an undoable "add annotation" edit (undo removes it; redo re-creates from spec).</summary>
    private void RaiseAnnotationAdded(string label, int page, AnnotationSpec? spec)
    {
        if (spec is null)
        {
            return;
        }
        AnnotationEdited?.Invoke(this, new AnnotationEditEventArgs
        {
            Label = label,
            PageIndex = page,
            UndoAction = d => d.RemoveLastAnnotation(page),
            RedoAction = d => d.AddAnnotationFromSpec(spec),
            // Only a stamp retains anything worth counting.
            SnapshotBytes = spec.Stamp?.ByteCount ?? 0,
        });
    }

    /// <summary>Adds a note (already collected by the shell) and refreshes the page.</summary>
    public async void AddNote(int pageIndex, double x, double y, string text)
    {
        if (_document is null)
        {
            return;
        }
        var document = _document;
        AnnotationSpec? spec;
        try
        {
            spec = await _scheduler.RunAsync(PdfWorkPriority.Interactive, () =>
            {
                document.AddNote(pageIndex, x, y, text);
                return document.CaptureLastAnnotation(pageIndex);
            });
        }
        catch
        {
            return;
        }
        if (_document != document)
        {
            return;
        }
        InvalidatePage(pageIndex);
        DocumentEdited?.Invoke(this, EventArgs.Empty);
        RaiseAnnotationAdded("note", pageIndex, spec);
    }

    private void ClearSelectionState()
    {
        _selection = null;
        _selectionPage = -1;
        _selectionAnchorChar = -1;
        _isSelecting = false;
    }

    /// <summary>Stops tile production ahead of a page mutation (stale requests would be out of range).</summary>
    internal void PreparePageMutation() => _scheduler.SetDesired([]);

    /// <summary>
    /// Rebuilds every page-derived cache after pages were deleted/moved/
    /// inserted: sizes, layout (preserving the scroll fraction), tiles,
    /// previews, links, text maps, selection, and search are all stale.
    /// </summary>
    internal void HandleDocumentMutated()
    {
        if (_document is null)
        {
            return;
        }

        _pageSizes = [.. Enumerable.Range(0, _document.PageCount).Select(_document.GetPageSize)];
        _links.Clear();
        _linksRequested.Clear();
        ClearPageTextCache();
        ClearSelectionState();
        _searchByPage.Clear();
        _activeHit = null;
        ClearCaches();

        double fraction = _layout is { TotalHeight: > 0 } ? Scroller.VerticalOffset / _layout.TotalHeight : 0;
        RebuildLayout();
        if (_layout is not null)
        {
            Scroller.ChangeView(null, fraction * _layout.TotalHeight, null, disableAnimation: true);
        }
        _currentPage = Math.Clamp(_currentPage, 0, Math.Max(0, _document.PageCount - 1));

        Canvas.Invalidate();
        UpdateDesiredTiles();
        UpdateCurrentPage();
        PrefetchVisiblePageData();
        CurrentPageChanged?.Invoke(this, _currentPage);
    }

    /// <summary>
    /// Marks one page's cached tiles for re-render while leaving them on
    /// screen, so the page is never blank in between.
    ///
    /// This is the difference between form typing that feels finished and form
    /// typing that looks broken: <see cref="InvalidatePage"/> disposes the
    /// tiles and the preview, so every keystroke would flash white and then
    /// cycle blurry-to-crisp. Here the old raster keeps drawing until its
    /// replacement is uploaded over it.
    /// </summary>
    public void RefreshPageTiles(int pageIndex)
    {
        foreach (var key in _tiles.Keys)
        {
            if (key.PageIndex == pageIndex)
            {
                _staleTiles.Add(key);
            }
        }
        if (_previews.ContainsKey(pageIndex))
        {
            _stalePreviews.Add(pageIndex);
        }
        UpdateDesiredTiles();
    }

    /// <summary>Drops one page's cached tiles + preview and re-renders it (after an edit).</summary>
    public void InvalidatePage(int pageIndex)
    {
        foreach (var key in _tiles.Keys.Where(k => k.PageIndex == pageIndex).ToList())
        {
            var (bitmap, node) = _tiles[key];
            _tileBytes -= (long)bitmap.SizeInPixels.Width * bitmap.SizeInPixels.Height * 4;
            _tileLru.Remove(node);
            _tiles.Remove(key);
            bitmap.Dispose();
        }
        if (_previews.Remove(pageIndex, out var preview))
        {
            _previewLru.Remove(pageIndex);
            _stalePreviews.Remove(pageIndex);
            preview.Bitmap.Dispose();
        }

        if (_layout is not null)
        {
            var rect = _layout.GetPageRect(pageIndex);
            Canvas.Invalidate(new Rect(rect.X, rect.Y, rect.Width, rect.Height));
        }
        UpdateDesiredTiles();
    }

    private async void Canvas_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_document is null || _layout is null)
        {
            return;
        }
        e.Handled = true; // must be set before any await — the event returns at the first one

        var position = e.GetPosition(Canvas);
        int page = _layout.PageAt(position.Y);
        var (localX, localY) = ToPageLocal(page, new Point(position.X, position.Y));
        var document = _document;

        var menu = new MenuFlyout();

        bool annotationsAllowed = _rotation == 0;
        if (annotationsAllowed && _selection is { Count: > 0 } sel && sel.PageIndex == page)
        {
            AddMenuItem(menu, "Highlight", Symbol.Highlight, () => MarkupSelection(MarkupKind.Highlight));
            AddMenuItem(menu, "Underline", Symbol.Underline, () => MarkupSelection(MarkupKind.Underline));
            AddMenuItem(menu, "Strikeout", Symbol.Font, () => MarkupSelection(MarkupKind.Strikeout));
        }
        if (HasSelection)
        {
            AddMenuItem(menu, "Copy", Symbol.Copy, CopySelectionToClipboard);
        }

        if (annotationsAllowed)
        {
            if (menu.Items.Count > 0)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
            }
            AddMenuItem(menu, "Add note here", Symbol.Comment,
                () => NoteRequested?.Invoke(this, (page, localX, localY)));

            // Offer deletion when the click lands on an annotation. The query
            // runs on the render thread so a slow tile render can't freeze the
            // UI while the menu is being built.
            AnnotationInfo? hit = null;
            try
            {
                var annotations = await _scheduler.RunAsync(
                    PdfWorkPriority.Interactive, () => document.GetAnnotations(page));
                hit = HitTestAnnotation(annotations, localX, localY);
            }
            catch
            {
                // Document swapped/closed; show the menu without a delete entry.
            }
            if (_document != document)
            {
                return;
            }
            if (hit is not null)
            {
                int annotIndex = hit.Index;
                AddMenuItem(menu, hit.IsNote ? $"Delete note{FormatNotePreview(hit.Contents)}" : "Delete annotation",
                    Symbol.Delete, async () =>
                    {
                        (bool Removed, AnnotationSpec? Spec) result;
                        try
                        {
                            // Capture the annotation BEFORE deleting so undo can rebuild it.
                            result = await _scheduler.RunAsync(PdfWorkPriority.Interactive, () =>
                            {
                                var captured = document.CaptureAnnotation(page, annotIndex);
                                bool ok = document.RemoveAnnotation(page, annotIndex);
                                return (ok, captured);
                            });
                        }
                        catch
                        {
                            return;
                        }
                        if (result.Removed && _document == document)
                        {
                            InvalidatePage(page);
                            DocumentEdited?.Invoke(this, EventArgs.Empty);
                            if (result.Spec is { } spec)
                            {
                                AnnotationEdited?.Invoke(this, new AnnotationEditEventArgs
                                {
                                    Label = spec.Subtype == Rune.PdfiumInterop.PdfiumNative.AnnotText ? "delete note" : "delete annotation",
                                    PageIndex = page,
                                    UndoAction = d => d.AddAnnotationFromSpec(spec),
                                    RedoAction = d => d.RemoveLastAnnotation(page),
                                });
                            }
                        }
                    });
            }
        }

        if (menu.Items.Count > 0)
        {
            menu.ShowAt(Canvas, position);
        }
    }

    private static string FormatNotePreview(string contents) =>
        string.IsNullOrWhiteSpace(contents) ? "" : $" (“{contents[..Math.Min(24, contents.Length)]}…”)";

    private static void AddMenuItem(MenuFlyout menu, string text, Symbol icon, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new SymbolIcon(icon) };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void CopySelectionToClipboard()
    {
        if (HasSelection)
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(SelectedText);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var doc = DocumentPointFromPointer(e);

        if (_isDrawingInk)
        {
            AppendInkPoint(doc);
            e.Handled = true;
            return;
        }

        if (_placingSignature)
        {
            UpdateSignaturePlacement(doc);
            e.Handled = true;
            return;
        }

        if (_isSelecting)
        {
            UpdateSelection(doc);
            e.Handled = true;
            return;
        }

        // Only offer the link cursor while reading; an armed tool owns the pointer.
        if (_activeTool == AnnotationTool.None)
        {
            SetLinkCursor(HitTestLink(doc) is not null);
        }
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Canvas).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var docPoint = DocumentPointFromPointer(e);

        if (IsInkMode)
        {
            BeginInkStroke(docPoint, e.Pointer);
            e.Handled = true;
            return;
        }

        // An armed tool owns the click, ahead of forms, links and selection.
        switch (_activeTool)
        {
            case AnnotationTool.Highlighter:
                // Reuses the ordinary selection machinery; the mark is applied
                // on release (see Canvas_PointerReleased), so dragging over text
                // highlights it in one gesture.
                BeginSelection(docPoint, e.Pointer);
                e.Handled = true;
                return;

            case AnnotationTool.Note:
                RequestNoteAt(docPoint);
                e.Handled = true;
                return;

            case AnnotationTool.Eraser:
                _ = EraseAnnotationAt(docPoint);
                e.Handled = true;
                return;

            case AnnotationTool.Signature when HasPendingSignature:
                BeginSignaturePlacement(docPoint, e.Pointer);
                e.Handled = true;
                return;
        }

        // Form fields come before links and selection: a widget sitting on top
        // of page text must take the click, not start a drag-select inside it.
        if (TryHandleFormPress(docPoint))
        {
            e.Handled = true;
            return;
        }

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

    private void PdfViewer_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (TryHandleFormCharacter(e.Character))
        {
            e.Handled = true;
        }
    }

    private void PdfViewer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (TryHandleFormKey(e.Key))
        {
            e.Handled = true;
        }
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDrawingInk)
        {
            CommitInkStroke();
            Canvas.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }
        if (_placingSignature)
        {
            CommitSignaturePlacement();
            Canvas.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }
        if (_isSelecting)
        {
            _isSelecting = false;
            Canvas.ReleasePointerCapture(e.Pointer);
            e.Handled = true;

            // The highlighter turns the drag into a mark and clears the
            // selection, so the whole thing reads as one gesture rather than
            // leaving text selected behind it.
            if (_activeTool == AnnotationTool.Highlighter && HasSelection)
            {
                MarkupSelection(_markupKind, _markupColor, _markupAlpha);
                ClearSelectionState();
                Canvas.Invalidate();
            }
        }
    }

    // ------------------------------------------------------- tool gestures

    /// <summary>Note tool: ask the shell for the note text at this point.</summary>
    private void RequestNoteAt(Point docPoint)
    {
        if (_layout is null || _document is null || _rotation != 0)
        {
            return;
        }
        int page = _layout.PageAt(docPoint.Y);
        var local = ToPageLocal(page, docPoint);
        NoteRequested?.Invoke(this, (page, local.X, local.Y));
    }

    /// <summary>Eraser tool: delete the annotation under the pointer, undoably.</summary>
    private async Task EraseAnnotationAt(Point docPoint)
    {
        if (_layout is null || _document is not { } document || _rotation != 0)
        {
            return;
        }

        int page = _layout.PageAt(docPoint.Y);
        var local = ToPageLocal(page, docPoint);

        IReadOnlyList<AnnotationInfo> annotations;
        try
        {
            annotations = await _scheduler.RunAsync(
                PdfWorkPriority.Interactive, () => document.GetAnnotations(page));
        }
        catch
        {
            return;
        }
        if (_document != document)
        {
            return;
        }

        var hit = HitTestAnnotation(annotations, local.X, local.Y);
        if (hit is null)
        {
            return;
        }

        AnnotationSpec? spec;
        try
        {
            spec = await _scheduler.RunAsync(PdfWorkPriority.Interactive, () =>
            {
                // Capture before removing so undo can rebuild it.
                var captured = document.CaptureAnnotation(page, hit.Index);
                document.RemoveAnnotation(page, hit.Index);
                return captured;
            });
        }
        catch
        {
            return;
        }
        if (_document != document)
        {
            return;
        }

        InvalidatePage(page);
        DocumentEdited?.Invoke(this, EventArgs.Empty);
        if (spec is not null)
        {
            AnnotationEdited?.Invoke(this, new AnnotationEditEventArgs
            {
                Label = "erase",
                PageIndex = page,
                UndoAction = d => d.AddAnnotationFromSpec(spec),
                RedoAction = d => d.RemoveAnnotation(page, hit.Index),
            });
        }
    }

    /// <summary>
    /// The topmost editable annotation containing a page-local point, or null.
    /// Shared by the eraser and the right-click "Delete annotation" entry so the
    /// two can never disagree about what is under the pointer.
    /// </summary>
    private static AnnotationInfo? HitTestAnnotation(IReadOnlyList<AnnotationInfo> annotations, double localX, double localY)
        => annotations.LastOrDefault(annot =>
            annot.Subtype != 2 /* links aren't editable */ &&
            localX >= annot.X - 4 && localX <= annot.X + annot.Width + 4 &&
            localY >= annot.Y - 4 && localY <= annot.Y + annot.Height + 4);

    // ---------------------------------------------------------------- ink capture

    private void BeginInkStroke(Point docPoint, Pointer pointer)
    {
        // Raised before the rotation guard: the user pressed to draw, so the
        // pen panel should get out of the way either way.
        InkStrokeStarted?.Invoke(this, EventArgs.Empty);

        if (_layout is null || _document is null || _rotation != 0)
        {
            return; // ink geometry only tracked for the unrotated view
        }
        _inkPage = _layout.PageAt(docPoint.Y);
        _inkPoints.Clear();
        _inkPoints.Add(ClampToPage(_inkPage, docPoint));
        _isDrawingInk = true;
        Canvas.CapturePointer(pointer);
    }

    private void AppendInkPoint(Point docPoint)
    {
        var p = ClampToPage(_inkPage, docPoint);
        // Skip sub-pixel jitter to keep the stroke light.
        if (_inkPoints.Count == 0 || Math.Abs(p.X - _inkPoints[^1].X) + Math.Abs(p.Y - _inkPoints[^1].Y) >= 1.0)
        {
            _inkPoints.Add(p);
            InvalidateInkArea();
        }
    }

    private async void CommitInkStroke()
    {
        _isDrawingInk = false;
        if (_document is null || _inkPage < 0 || _inkPoints.Count < 2)
        {
            _inkPoints.Clear();
            Canvas.Invalidate();
            return;
        }

        var rect = _layout!.GetPageRect(_inkPage);
        // Document space → page-local top-left points (undo centering + zoom).
        var stroke = _inkPoints.Select(p => ((p.X - rect.X) / _zoom, (p.Y - rect.Y) / _zoom)).ToList();

        var document = _document;
        int page = _inkPage;
        var (cr, cg, cb) = (_inkColor.R, _inkColor.G, _inkColor.B);
        float width = (float)_inkWidthPt;
        _inkPoints.Clear();
        _inkPage = -1;

        AnnotationSpec? spec;
        try
        {
            spec = await _scheduler.RunAsync(PdfWorkPriority.Interactive, () =>
            {
                document.AddInk(page, stroke, cr, cg, cb, 255, width);
                return document.CaptureLastAnnotation(page);
            });
        }
        catch
        {
            Canvas.Invalidate(); // stroke is lost (doc swapped/closed); clear the live line
            return;
        }
        if (_document != document)
        {
            return;
        }
        InvalidatePage(page);
        DocumentEdited?.Invoke(this, EventArgs.Empty);
        RaiseAnnotationAdded("ink", page, spec);
    }

    private void CancelInkStroke()
    {
        _isDrawingInk = false;
        _inkPoints.Clear();
        _inkPage = -1;
        Canvas.Invalidate();
    }

    private Point ClampToPage(int page, Point docPoint)
    {
        if (_layout is null)
        {
            return docPoint;
        }
        var r = _layout.GetPageRect(page);
        return new Point(Math.Clamp(docPoint.X, r.X, r.Right), Math.Clamp(docPoint.Y, r.Y, r.Bottom));
    }

    private void InvalidateInkArea()
    {
        // Repaint a small box around the last segment (cheap live feedback).
        if (_inkPoints.Count < 2)
        {
            return;
        }
        var a = _inkPoints[^2];
        var b = _inkPoints[^1];
        double pad = _inkWidthPt * _zoom + 4;
        Canvas.Invalidate(new Rect(
            Math.Min(a.X, b.X) - pad, Math.Min(a.Y, b.Y) - pad,
            Math.Abs(a.X - b.X) + 2 * pad, Math.Abs(a.Y - b.Y) + 2 * pad));
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

        // Hit-test against the cached text map — never PDFium on the pointer
        // path. If the map hasn't arrived yet (racing the very first press),
        // kick off extraction; the next press will hit.
        if (!_pageTexts.TryGetValue(page, out var pageText))
        {
            EnsurePageText(page);
        }
        int charIndex = pageText?.CharIndexAt(localX, localY) ?? -1;

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
        // Selection stays on the page it started on (single-page selection).
        // Everything here is a managed lookup on the cached text map — this
        // runs on every PointerMoved during a drag and must never touch PDFium.
        if (_document is null || _selectionPage < 0 || _selectionAnchorChar < 0 ||
            !_pageTexts.TryGetValue(_selectionPage, out var pageText))
        {
            return;
        }

        var (localX, localY) = ToPageLocal(_selectionPage, docPoint);
        int focusChar = pageText.CharIndexAt(localX, localY);
        if (focusChar < 0)
        {
            return;
        }

        _selection = pageText.GetSelection(_selectionAnchorChar, focusChar);
        Canvas.Invalidate();
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

    private async void EnsureLinks(int pageIndex)
    {
        if (_document is null || !_linksRequested.Add(pageIndex))
        {
            return;
        }
        var document = _document;
        try
        {
            var links = await _scheduler.RunAsync(PdfWorkPriority.Interactive, () => document.GetLinks(pageIndex));
            if (_document == document)
            {
                _links[pageIndex] = links;
            }
        }
        catch
        {
            // Best-effort; also covers cancellation when the document swaps.
        }
    }

    private async void EnsurePageText(int pageIndex)
    {
        if (_document is null || !_pageTextRequested.Add(pageIndex))
        {
            return;
        }
        var document = _document;
        try
        {
            var text = await _scheduler.RunAsync(PdfWorkPriority.Interactive, () => document.GetPageText(pageIndex));
            if (_document == document)
            {
                InsertPageText(pageIndex, text);
            }
        }
        catch
        {
            _pageTextRequested.Remove(pageIndex); // retry on the next prefetch
        }
    }

    private void InsertPageText(int pageIndex, PageText text)
    {
        if (_pageTexts.ContainsKey(pageIndex))
        {
            _pageTextLru.Remove(pageIndex);
        }
        _pageTexts[pageIndex] = text;
        _pageTextLru.AddFirst(pageIndex);

        while (_pageTextLru.Count > PageTextCacheCapacity && _pageTextLru.Last is { } oldest)
        {
            _pageTexts.Remove(oldest.Value);
            _pageTextRequested.Remove(oldest.Value);
            _pageTextLru.RemoveLast();
        }
    }

    private void ClearPageTextCache()
    {
        _pageTexts.Clear();
        _pageTextLru.Clear();
        _pageTextRequested.Clear();
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
        // Safety net for regions the virtual control materializes without a
        // ViewChanged (e.g. fast flicks) — coalesced, not per-draw.
        RequestDesiredUpdate();
    }

    /// <summary>
    /// Whether the area *around* the pages should be dark.
    ///
    /// Night mode counts, not just the app theme: inverted black pages sitting
    /// on a light grey field is the worst-looking combination in the app, and
    /// it is reachable simply by using night mode in the light theme.
    /// </summary>
    private bool DarkSurround => _nightMode || ActualTheme == ElementTheme.Dark;

    private void ApplyViewerBackground()
    {
        var brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(RuneColors.ViewerBackground(DarkSurround));
        Background = brush;
        Scroller.Background = brush;
    }

    private void DrawRegion(CanvasDrawingSession session, Rect region)
    {
        bool darkSurround = DarkSurround;
        session.Clear(RuneColors.ViewerBackground(darkSurround));

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
        DebugLog($"DRAW region=({region.X:0},{region.Y:0},{region.Width:0}x{region.Height:0}) pages={first}-{last} rot={_rotation} scaleKey={scaleKey} tiles={_tiles.Count} previews={_previews.Count}");
        double rasterScale = DisplayScale;
        var borderColor = RuneColors.PageBorder(darkSurround);

        for (int i = first; i <= last; i++)
        {
            var pageRect = _layout.GetPageRect(i);
            var pageXamlRect = new Rect(pageRect.X, pageRect.Y, pageRect.Width, pageRect.Height);

            session.FillRectangle(pageXamlRect, RuneColors.PageFill(_nightMode));

            if (_previews.TryGetValue(i, out var preview))
            {
                DrawPageImage(session, preview.Bitmap, pageXamlRect);
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
                        DebugLog($"  HIT {key.PageIndex}/{key.Row},{key.Col} bmp={entry.Bitmap.SizeInPixels.Width}x{entry.Bitmap.SizeInPixels.Height} dest=({dest.X:0},{dest.Y:0},{dest.Width:0}x{dest.Height:0}) night={_nightMode}");
                        DrawPageImage(session, entry.Bitmap, dest);
                        TouchTile(key);
                    }
                    else if (DebugLogEnabled)
                    {
                        DebugLog($"  MISS {key}");
                    }
                }
            }

            DrawHighlights(session, i, pageRect);
            DrawFormFieldBorders(session, i, pageRect);
            session.DrawRectangle(pageXamlRect, borderColor, 1);
        }

        DrawLiveInk(session);
        DrawSignatureGhost(session);
    }

    /// <summary>Draws the in-progress ink stroke as a polyline (committed once released).</summary>
    private void DrawLiveInk(CanvasDrawingSession session)
    {
        if (!_isDrawingInk || _inkPoints.Count < 2)
        {
            return;
        }
        float strokeWidth = (float)(_inkWidthPt * _zoom);
        for (int i = 1; i < _inkPoints.Count; i++)
        {
            var a = _inkPoints[i - 1];
            var b = _inkPoints[i];
            session.DrawLine((float)a.X, (float)a.Y, (float)b.X, (float)b.Y, _inkColor, strokeWidth);
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
            _invertEffect ??= new Microsoft.Graphics.Canvas.Effects.InvertEffect();
            _invertEffect.Source = bitmap;
            session.DrawImage(_invertEffect, dest, bitmap.Bounds);
        }
        else
        {
            session.DrawImage(bitmap, dest, bitmap.Bounds);
        }
    }

    private void DrawHighlights(CanvasDrawingSession session, int pageIndex, DipRect pageRect)
    {
        // Overlays are painted after the tiles and are NOT put through the
        // invert effect, so their colours key on night mode directly.
        var searchColor = RuneColors.Search(_nightMode);
        var activeSearchColor = RuneColors.ActiveSearch(_nightMode);

        // Search hits (only visible pages carry entries).
        if (_searchByPage.TryGetValue(pageIndex, out var hits))
        {
            foreach (var hit in hits)
            {
                bool active = ReferenceEquals(hit, _activeHit) ||
                              (_activeHit is not null && hit.PageIndex == _activeHit.PageIndex && hit.CharIndex == _activeHit.CharIndex);
                var color = active ? activeSearchColor : searchColor;
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
                session.FillRectangle(HighlightRect(pageRect, r), RuneColors.Selection(_nightMode));
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
    /// <summary>
    /// Target width for the whole-page preview at a given render scale.
    ///
    /// 216 px is the right size for a page shown at 100%, but stretched over a
    /// page zoomed to 200%+ it is visibly mushy — and the preview is exactly
    /// what shows while crisp tiles are still rendering. Past a threshold, ask
    /// for a proportionally sharper one.
    ///
    /// Hard-capped at <see cref="TileMath.MaxSingleTilePx"/>: bitmaps much
    /// wider than ~1.5k px silently fail to draw inside CanvasVirtualControl
    /// sessions (the old "rotate shows a blank page" bug), so nothing the
    /// viewer creates is allowed past the tile edge.
    /// </summary>
    private static float PreviewWidthFor(float renderScale)
    {
        if (renderScale <= 1.5f)
        {
            return PreviewTargetWidthPx;
        }
        float scaled = PreviewTargetWidthPx * (renderScale / 1.5f);
        return Math.Min(scaled, TileMath.MaxSingleTilePx);
    }

    private void UpdateDesiredTiles()
    {
        if (_layout is null || _document is null)
        {
            _scheduler.SetDesired([]);
            return;
        }
        float zoomFactor = Scroller.ZoomFactor;
        if (Math.Abs(zoomFactor - 1f) > 0.001f)
        {
            // Mid-pinch the offsets are in scaled space, so a want-list built
            // now would be wrong — but never just give up. A pinch whose final
            // (non-intermediate) ViewChanged is missed used to leave ZoomFactor
            // off 1 permanently, and since this was an unconditional return, no
            // crisp tile was ever requested again: the page stayed blurry
            // forever. If we're not actively being manipulated, fold the factor
            // in ourselves and carry on.
            if (!_rebasingZoom && !_manipulating && !_healingZoom)
            {
                // RebaseZoom calls back into this method, so guard the reentry
                // or a ChangeView that keeps failing would spin forever.
                _healingZoom = true;
                try
                {
                    RebaseZoom(zoomFactor);
                }
                finally
                {
                    _healingZoom = false;
                }
            }
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
                    if (_tiles.ContainsKey(key) && !_staleTiles.Contains(key))
                    {
                        continue;
                    }

                    var request = new TileRequest(key, scale, srcX, srcY, w, h);
                    (tileRect.Intersects(viewport) ? visible : prefetch).Add(request);
                }
            }

            var previewSize = _pageSizes[i];
            float previewPageWidthPt = _rotation % 2 == 0 ? previewSize.Width : previewSize.Height;
            float wantedPreviewScale = PreviewWidthFor(scale) / previewPageWidthPt;
            int wantedPreviewKey = TileKey.ToScaleKey(wantedPreviewScale);

            // Re-request when the preview is missing, stale, or was rendered
            // for a different zoom tier — otherwise a page zoomed well past
            // 100% keeps showing the 216px placeholder stretched over it.
            if (!_previews.TryGetValue(i, out var cachedPreview)
                || cachedPreview.ScaleKey != wantedPreviewKey
                || _stalePreviews.Contains(i))
            {
                float previewScale = wantedPreviewScale;
                var (pw, ph) = _document.GetPagePixelSize(i, previewScale, _rotation);
                previews.Add(new TileRequest(
                    new TileKey(i, wantedPreviewKey, _rotation, 0, 0, IsPreview: true),
                    previewScale, 0, 0, pw, ph));
            }
        }

        visible.AddRange(previews);
        visible.AddRange(prefetch);
        DebugLog($"WANT rot={_rotation} zoom={_zoom:0.###} scale={scale:0.###} n={visible.Count} vp=({viewport.X:0},{viewport.Y:0},{viewport.Width:0},{viewport.Height:0}) zf={Scroller.ZoomFactor:0.###}");
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
        // Both drop paths clear the stale mark: the replacement is never
        // arriving, so leaving the key marked would make UpdateDesiredTiles
        // re-request this tile on every single pass, starving the tiles that
        // are actually on screen.
        if (_document is null || _layout is null || request.Key.Rotation != _rotation)
        {
            DebugLog($"DROP rot-mismatch key={request.Key} cur_rot={_rotation}");
            ForgetStale(request.Key);
            return;
        }

        // A zoom change while this tile was in flight makes it useless.
        if (!request.Key.IsPreview && request.Key.ScaleKey != TileKey.ToScaleKey(RenderScale))
        {
            DebugLog($"DROP scale-mismatch key={request.Key} cur={TileKey.ToScaleKey(RenderScale)}");
            ForgetStale(request.Key);
            return;
        }
        if (DebugLogEnabled)
        {
            int ink = 0;
            for (int y = 0; y < bitmap.Height; y += 7)
            {
                for (int x = 0; x < bitmap.Width; x += 7)
                {
                    int i = y * bitmap.Stride + x * 4;
                    if (bitmap.Pixels[i] != 0xFF || bitmap.Pixels[i + 1] != 0xFF || bitmap.Pixels[i + 2] != 0xFF)
                    {
                        ink++;
                    }
                }
            }
            DebugLog($"KEEP key={request.Key} {bitmap.Width}x{bitmap.Height} sampledInk={ink}");
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
            InsertPreview(request.Key.PageIndex, request.Key.ScaleKey, canvasBitmap);
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

    /// <summary>Drops a stale mark for a tile whose re-render will never land.</summary>
    private void ForgetStale(TileKey key)
    {
        if (key.IsPreview)
        {
            _stalePreviews.Remove(key.PageIndex);
        }
        else
        {
            _staleTiles.Remove(key);
        }
    }

    private void InsertTile(TileKey key, CanvasBitmap bitmap)
    {
        _staleTiles.Remove(key); // the replacement has arrived
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
            _staleTiles.Remove(oldest.Value); // gone from the cache; nothing left to refresh
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

    private void InsertPreview(int pageIndex, int scaleKey, CanvasBitmap bitmap)
    {
        _stalePreviews.Remove(pageIndex);
        if (_previews.TryGetValue(pageIndex, out var existing))
        {
            existing.Bitmap.Dispose();
            _previewLru.Remove(pageIndex);
        }

        _previews[pageIndex] = (bitmap, scaleKey);
        _previewLru.AddFirst(pageIndex);

        while (_previewLru.Count > PreviewCacheCapacity && _previewLru.Last is { } oldest)
        {
            _previews[oldest.Value].Bitmap.Dispose();
            _previews.Remove(oldest.Value);
            _stalePreviews.Remove(oldest.Value);
            _previewLru.RemoveLast();
        }
    }
}
