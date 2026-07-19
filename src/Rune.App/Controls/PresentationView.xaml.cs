using Rune.Engine;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics.DirectX;

namespace Rune.Controls;

/// <summary>
/// Fullscreen presentation surface (Evince/Papers F5): one page at a time on
/// black, fit to the screen. Deliberately NOT a mode of PdfViewer — the
/// continuous-scroll machinery stays untouched; this is a simple overlay that
/// borrows the active viewer's render thread for its tiles.
///
/// Pages are rendered as ≤1024 px tiles (TileMath) because oversized bitmaps
/// silently fail to draw on some hardware (see PROJECT.md §7).
/// </summary>
public sealed partial class PresentationView : UserControl
{
    private PdfViewer? _viewer;
    private PdfDocument? _document;
    private int _page;
    private int _rotation;
    private bool _nightMode;

    private readonly Dictionary<(int Page, int Row, int Col), CanvasBitmap> _tiles = [];
    private readonly HashSet<(int Page, int Row, int Col)> _requested = [];
    private Microsoft.Graphics.Canvas.Effects.InvertEffect? _invert;

    /// <summary>Raised when the user asks to leave (Esc handled by the shell; click-past-last-page here).</summary>
    public event EventHandler? ExitRequested;

    public bool IsActive => _viewer is not null;
    public int CurrentPage => _page;

    public PresentationView()
    {
        InitializeComponent();
        PointerPressed += OnPointerPressed;
        SizeChanged += (_, _) =>
        {
            if (IsActive)
            {
                // Fit scale depends on our size; drop and re-request.
                ClearTiles();
                Canvas.Invalidate();
                RequestPageTiles(_page, PdfWorkPriority.Interactive);
            }
        };
    }

    public void Show(PdfViewer viewer, bool nightMode)
    {
        _viewer = viewer;
        _document = viewer.Document;
        _rotation = viewer.ViewRotation;
        _nightMode = nightMode;
        _page = Math.Clamp(viewer.CurrentPage, 0, Math.Max(0, (viewer.Document?.PageCount ?? 1) - 1));
        Visibility = Visibility.Visible;
        ClearTiles();
        Canvas.Invalidate();
        RequestPageTiles(_page, PdfWorkPriority.Interactive);
        PrefetchNeighbors();
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        _viewer = null;
        _document = null;
        ClearTiles();
    }

    public void Next()
    {
        if (_document is not null && _page < _document.PageCount - 1)
        {
            GoTo(_page + 1);
        }
    }

    public void Prev()
    {
        if (_page > 0)
        {
            GoTo(_page - 1);
        }
    }

    private void GoTo(int page)
    {
        if (_document is null)
        {
            return;
        }
        _page = Math.Clamp(page, 0, _document.PageCount - 1);
        EvictFarPages();
        Canvas.Invalidate();
        RequestPageTiles(_page, PdfWorkPriority.Interactive);
        PrefetchNeighbors();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Properties;
        if (point.IsRightButtonPressed)
        {
            Prev();
        }
        else if (_document is not null && _page >= _document.PageCount - 1)
        {
            ExitRequested?.Invoke(this, EventArgs.Empty); // click past the last page ends the show
        }
        else
        {
            Next();
        }
        e.Handled = true;
    }

    // ---------------------------------------------------------------- tiles

    private double Raster => XamlRoot?.RasterizationScale ?? 1.0;

    /// <summary>Device pixels per point that fits the page on this surface.</summary>
    private float ScaleFor(int page)
    {
        if (_document is null || ActualWidth < 1 || ActualHeight < 1)
        {
            return 1f;
        }
        var (ptW, ptH) = _document.GetPageSize(page);
        double pageW = _rotation % 2 == 0 ? ptW : ptH;
        double pageH = _rotation % 2 == 0 ? ptH : ptW;
        double pxW = ActualWidth * Raster;
        double pxH = ActualHeight * Raster;
        return (float)Math.Min(pxW / pageW, pxH / pageH);
    }

    private async void RequestPageTiles(int page, PdfWorkPriority priority)
    {
        if (_viewer is null || _document is null || page < 0 || page >= _document.PageCount)
        {
            return;
        }

        var viewer = _viewer;
        var document = _document;
        float scale = ScaleFor(page);
        var (pxW, pxH) = document.GetPagePixelSize(page, scale, _rotation);
        var (cols, rows) = TileMath.GridFor(pxW, pxH);

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                var key = (page, row, col);
                if (_tiles.ContainsKey(key) || !_requested.Add(key))
                {
                    continue;
                }

                var (srcX, srcY, w, h) = TileMath.TileWindow(pxW, pxH, row, col);
                try
                {
                    var bmp = await viewer.RunOnRenderThreadAsync(priority,
                        () => document.RenderRegion(page, scale, _rotation, srcX, srcY, w, h));
                    bool keep = _document == document && IsActive;
                    if (keep)
                    {
                        try
                        {
                            _tiles[key] = CanvasBitmap.CreateFromBytes(
                                Canvas, bmp.Pixels, bmp.Width, bmp.Height,
                                DirectXPixelFormat.B8G8R8A8UIntNormalized);
                            if (page == _page)
                            {
                                Canvas.Invalidate();
                            }
                        }
                        catch
                        {
                            _requested.Remove(key); // device lost; retried on next draw
                        }
                    }
                    bmp.Return();
                }
                catch
                {
                    _requested.Remove(key); // doc swapped/closed or corrupt page
                }
            }
        }
    }

    private void PrefetchNeighbors()
    {
        RequestPageTiles(_page + 1, PdfWorkPriority.Background);
        RequestPageTiles(_page - 1, PdfWorkPriority.Background);
    }

    private void EvictFarPages()
    {
        foreach (var key in _tiles.Keys.Where(k => Math.Abs(k.Page - _page) > 1).ToList())
        {
            _tiles[key].Dispose();
            _tiles.Remove(key);
            _requested.Remove(key);
        }
    }

    private void ClearTiles()
    {
        foreach (var bitmap in _tiles.Values)
        {
            bitmap.Dispose();
        }
        _tiles.Clear();
        _requested.Clear();
        _invert?.Dispose();
        _invert = null;
    }

    // ---------------------------------------------------------------- drawing

    private void Canvas_CreateResources(CanvasControl sender, object args)
    {
        ClearTiles(); // bitmaps belong to the old device
        if (IsActive)
        {
            RequestPageTiles(_page, PdfWorkPriority.Interactive);
        }
    }

    private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var session = args.DrawingSession;
        session.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0));

        if (_document is null)
        {
            return;
        }

        float scale = ScaleFor(_page);
        var (pxW, pxH) = _document.GetPagePixelSize(_page, scale, _rotation);
        double raster = Raster;
        double dipW = pxW / raster;
        double dipH = pxH / raster;
        double x0 = (ActualWidth - dipW) / 2;
        double y0 = (ActualHeight - dipH) / 2;

        var (cols, rows) = TileMath.GridFor(pxW, pxH);
        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                if (!_tiles.TryGetValue((_page, row, col), out var bitmap))
                {
                    continue;
                }
                var (srcX, srcY, w, h) = TileMath.TileWindow(pxW, pxH, row, col);
                var dest = new Windows.Foundation.Rect(
                    x0 + srcX / raster, y0 + srcY / raster, w / raster, h / raster);
                if (_nightMode)
                {
                    _invert ??= new Microsoft.Graphics.Canvas.Effects.InvertEffect();
                    _invert.Source = bitmap;
                    session.DrawImage(_invert, dest, bitmap.Bounds);
                }
                else
                {
                    session.DrawImage(bitmap, dest, bitmap.Bounds);
                }
            }
        }
    }
}
