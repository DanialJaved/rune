using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Input;
using Rune.Engine;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace Rune.Controls;

// Placing a picture on the page: a signature, or any image the user picked.
//
// The two are the same gesture and the same file format, and only the source
// differs — a signature comes from the pad with its paper keyed out, an image
// comes from the picker exactly as it was taken. So one pipeline serves both,
// and what it was armed with survives only as the label the undo entry carries.
//
// The gesture is deliberately dual: a plain click drops the picture at the size
// last used, which is what makes signing several fields (or stamping a logo on
// several pages) quick, while press-drag-release draws the box explicitly when
// the space is an odd size. Both keep the aspect ratio — a stretched signature
// looks wrong immediately, and so does a stretched photograph.
//
// Ctrl+wheel resizes what is armed; the plain wheel is left alone to scroll,
// because you almost always have to scroll to the spot before you can drop
// anything on it.
public sealed partial class PdfViewer
{
    private byte[]? _pendingBgra;
    private int _pendingPixelW;
    private int _pendingPixelH;
    private CanvasBitmap? _pendingGhost;

    /// <summary>What the undo entry calls this placement: "signature" or "image".</summary>
    private string _pendingLabel = "signature";

    private bool _placingStamp;
    private int _placementPage = -1;
    private Point _placementStart;
    private Point _placementEnd;

    /// <summary>
    /// Where the pointer is while a placement tool is armed but nothing has been
    /// pressed yet, so the ghost can show the landing spot *before* you commit
    /// to it. Null when the pointer is outside the canvas.
    /// </summary>
    private Point? _placementHover;

    /// <summary>Latched when the ghost bitmap can't be built, so it isn't retried every frame.</summary>
    private bool _ghostFailed;

    /// <summary>True while a tool that places a picture is armed.</summary>
    private bool IsPlacementTool => _activeTool is AnnotationTool.Signature or AnnotationTool.Image;

    /// <summary>
    /// Width in page points used by a plain click. Remembered across placements
    /// (and persisted by the shell, per kind) so repeat placing is consistent.
    /// </summary>
    public double PendingWidthPt { get; set; } = 180;

    /// <summary>Raised after a picture is placed, with the width actually used.</summary>
    public event EventHandler<double>? StampPlaced;

    /// <summary>Raised when the wheel resizes the pending picture, so the panel can show the size.</summary>
    public event EventHandler<double>? StampResized;

    public bool HasPendingStamp => _pendingBgra is not null;

    /// <summary>
    /// Arms a picture for placement. Pixels are straight BGRA, as produced by
    /// <see cref="SignaturePad"/> or <see cref="Services.SignatureStore"/>.
    /// </summary>
    /// <param name="label">What undo should call it: "signature" or "image".</param>
    public void SetPendingStamp(byte[] bgra, int width, int height, string label)
    {
        _pendingBgra = bgra;
        _pendingPixelW = width;
        _pendingPixelH = height;
        _pendingLabel = label;
        _pendingGhost?.Dispose();
        _pendingGhost = null;
        _ghostFailed = false;
        CancelStampPlacement();
    }

    public void ClearPendingStamp()
    {
        _pendingBgra = null;
        _pendingGhost?.Dispose();
        _pendingGhost = null;
        _ghostFailed = false;
        CancelStampPlacement();
    }

    /// <summary>
    /// A width that suits the picture as it stands: its natural size at 96 dpi,
    /// but never more than half the page, so a phone photo does not arm wider
    /// than the paper it is going onto.
    /// </summary>
    public double NaturalWidthPt(int pixelWidth)
    {
        double natural = pixelWidth * 72.0 / 96.0;
        double half = _pageSizes.Length > 0
            ? _pageSizes[Math.Clamp(_currentPage, 0, _pageSizes.Length - 1)].Width / 2.0
            : natural;
        return Math.Clamp(Math.Min(natural, half), 24, 720);
    }

    /// <summary>True when a placement drag was cancelled, so Esc can be consumed.</summary>
    public bool CancelStampPlacement()
    {
        if (!_placingStamp)
        {
            return false;
        }
        _placingStamp = false;
        _placementPage = -1;
        Canvas.Invalidate();
        return true;
    }

    // ---- hover preview ----

    /// <summary>
    /// Tracks the pointer so the pending picture can be previewed under it.
    /// Returns true when the ghost needs redrawing.
    /// </summary>
    private bool UpdateStampHover(Point? docPoint)
    {
        if (!HasPendingStamp || !IsPlacementTool)
        {
            bool had = _placementHover is not null;
            _placementHover = null;
            return had;
        }
        _placementHover = docPoint;
        return true;
    }

    /// <summary>
    /// Scales the pending picture before it is placed. Returns true when the
    /// wheel was consumed, which is what stops the page ZOOMING underneath a
    /// resize gesture — the caller only offers this the modifier-held wheel, so
    /// a plain wheel scrolls the page whether or not a picture is armed.
    /// </summary>
    public bool TryResizePendingStamp(int wheelDelta)
    {
        if (!HasPendingStamp || !IsPlacementTool)
        {
            return false;
        }

        // Multiplicative so each notch feels the same at any size; the range is
        // wide enough for an initial box and a full-page flourish alike.
        double factor = wheelDelta > 0 ? 1.1 : 1 / 1.1;
        PendingWidthPt = Math.Clamp(PendingWidthPt * factor, 24, 720);
        StampResized?.Invoke(this, PendingWidthPt);
        Canvas.Invalidate();
        return true;
    }

    // ---- gesture ----

    /// <summary>
    /// True while the placement in flight was begun by a finger, which decides
    /// how far it has to travel before it counts as a drag rather than a tap.
    /// </summary>
    private bool _placementFromTouch;

    private void BeginStampPlacement(Point docPoint, Pointer pointer, bool touch)
    {
        if (_layout is null || _document is null || !HasPendingStamp)
        {
            return;
        }
        _placementPage = _layout.PageAt(docPoint.Y);
        _placementStart = ClampToPage(_placementPage, docPoint);
        _placementEnd = _placementStart;
        _placingStamp = true;
        _placementFromTouch = touch;
        BeginExclusiveGesture(pointer);

        // Draw the ghost from the moment of contact. A mouse has already been
        // previewing it on hover for as long as it took to choose the spot; a
        // finger has no hover, so without this the first thing the user sees of
        // the picture is it landing.
        Canvas.Invalidate();
    }

    private void UpdateStampPlacement(Point docPoint)
    {
        _placementEnd = ClampToPage(_placementPage, docPoint);
        Canvas.Invalidate();
    }

    /// <summary>
    /// The placement box in document space, aspect-locked to the picture.
    /// A drag shorter than a few pixels in either axis counts as a click and
    /// uses the remembered width instead.
    /// </summary>
    private Rect PlacementBox()
    {
        double aspect = _pendingPixelH / (double)Math.Max(1, _pendingPixelW);
        double dragX = Math.Abs(_placementEnd.X - _placementStart.X);

        // A click (or a negligible drag) places at the remembered size, anchored
        // at the press point. A finger never lands as still as a mouse click, so
        // 8 px of jitter used to read as a deliberate drag and size the picture
        // at whatever the wobble happened to be.
        if (dragX < TouchMetrics.PlacementDragThreshold(_placementFromTouch))
        {
            double w = PendingWidthPt * _zoom;
            return new Rect(_placementStart.X, _placementStart.Y, w, w * aspect);
        }

        // Width follows the drag; height follows the picture's aspect, so it is
        // never stretched. Dragging left/up still works.
        double width = dragX;
        double height = width * aspect;
        double x = Math.Min(_placementStart.X, _placementEnd.X);
        double y = _placementEnd.Y < _placementStart.Y ? _placementStart.Y - height : _placementStart.Y;
        return new Rect(x, y, width, height);
    }

    private async void CommitStampPlacement()
    {
        _placingStamp = false;

        if (_document is not { } document || _layout is null || _placementPage < 0
            || _pendingBgra is not { } bgra)
        {
            Canvas.Invalidate();
            return;
        }

        var box = PlacementBox();
        int page = _placementPage;
        _placementPage = -1;

        // The box is where the picture was drawn; the file wants unrotated
        // page-local. Both corners go through the transform and are normalized,
        // so on a quarter turn the width and height swap along with the axes.
        var (cornerAX, cornerAY) = ToPageLocal(page, new Point(box.X, box.Y));
        var (cornerBX, cornerBY) = ToPageLocal(page, new Point(box.Right, box.Bottom));
        double localX = Math.Min(cornerAX, cornerBX);
        double localY = Math.Min(cornerAY, cornerBY);
        double widthPt = Math.Abs(cornerBX - cornerAX);
        double heightPt = Math.Abs(cornerBY - cornerAY);

        // Turn the pixels the other way so the view's rotation cancels out and
        // the picture reads upright against the content the user was looking at.
        var (stampBgra, pw, ph) = SignatureMatte.RotateQuarterTurns(
            bgra, _pendingPixelW, _pendingPixelH, 4 - _rotation);

        AnnotationSpec? spec;
        try
        {
            spec = await _scheduler.RunAsync(PdfWorkPriority.Interactive,
                () => document.AddStamp(page, localX, localY, widthPt, heightPt, stampBgra, pw, ph));
        }
        catch
        {
            Canvas.Invalidate(); // document swapped or closed mid-placement
            return;
        }
        if (_document != document)
        {
            return;
        }

        // The remembered width is the on-screen one, so a later placement at a
        // different rotation still comes back the size the user last chose.
        double onScreenWidthPt = box.Width / _zoom;
        PendingWidthPt = onScreenWidthPt;
        StampPlaced?.Invoke(this, onScreenWidthPt);

        InvalidatePage(page);
        DocumentEdited?.Invoke(this, EventArgs.Empty);
        RaiseAnnotationAdded(_pendingLabel, page, spec);
    }

    // ---- ghost ----

    /// <summary>
    /// Draws the pending picture where it would land. Called from DrawRegion
    /// alongside DrawLiveInk, so it sits above the page but below nothing else.
    /// </summary>
    private void DrawStampGhost(CanvasDrawingSession session)
    {
        if (_pendingBgra is not { } bgra)
        {
            return;
        }

        // Two states share one preview: hovering before the press (so you can
        // see where it will land and how big it will be), and mid-drag. Without
        // the first, the preview only ever appeared once the spot had already
        // been chosen, which made placement a guess.
        Rect box;
        if (_placingStamp)
        {
            box = PlacementBox();
        }
        else if (_placementHover is { } hover)
        {
            double aspect = _pendingPixelH / (double)Math.Max(1, _pendingPixelW);
            double w = PendingWidthPt * _zoom;
            box = new Rect(hover.X, hover.Y, w, w * aspect);
        }
        else
        {
            return;
        }

        // Past MaxSingleTilePx a bitmap silently fails to draw inside a
        // CanvasVirtualControl session, taking the dashed outline below down
        // with it and leaving no preview at all. Both callers cap their pixels
        // at decode time so this should be unreachable; degrade to an
        // outline-only preview rather than to nothing if one ever slips past.
        EnsureGhostBitmap(bgra);
        if (_pendingGhost is { } ghost)
        {
            session.DrawImage(ghost, box, ghost.Bounds, 0.75f);
        }

        // A dashed outline reads as "not placed yet" even where the picture
        // itself is faint — and it is drawn outside the bitmap's try/catch so a
        // failed ghost degrades to an outline rather than to nothing at all.
        session.DrawRectangle(box, Color.FromArgb(180, 0, 120, 215), 1,
            new Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle
            {
                DashStyle = Microsoft.Graphics.Canvas.Geometry.CanvasDashStyle.Dash,
            });
    }

    /// <summary>
    /// Builds the ghost bitmap once per armed picture.
    ///
    /// PREMULTIPLIED, and converted here rather than stored that way: Direct2D
    /// rejects a straight-alpha bitmap outright with
    /// WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT (0x88982F80), while PDFium needs the
    /// straight buffer this converts from. Asking for CanvasAlphaMode.Straight
    /// is what made the hover preview show nothing at all — the throw escaped
    /// DrawStampGhost and took the dashed outline with it, so the whole preview
    /// vanished rather than degrading.
    ///
    /// Failures are swallowed after logging: a preview that cannot be built is
    /// never a reason to take the page's draw pass down.
    /// </summary>
    private void EnsureGhostBitmap(byte[] bgra)
    {
        if (_pendingGhost is not null || _ghostFailed)
        {
            return;
        }

        // Past MaxSingleTilePx a bitmap silently fails to draw inside a
        // CanvasVirtualControl session. Both callers cap their pixels at decode
        // time, so this should be unreachable.
        if (_pendingPixelW > TileMath.MaxSingleTilePx || _pendingPixelH > TileMath.MaxSingleTilePx)
        {
            _ghostFailed = true;
            return;
        }

        try
        {
            _pendingGhost = CanvasBitmap.CreateFromBytes(
                Canvas, SignatureMatte.ToPremultiplied(bgra), _pendingPixelW, _pendingPixelH,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        }
        catch (Exception ex)
        {
            // Latched so a broken buffer doesn't retry on every single frame.
            _ghostFailed = true;
            Rune.Services.ErrorLog.Default.Write(nameof(PdfViewer), ex);
        }
    }
}
