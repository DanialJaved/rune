using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Input;
using Rune.Engine;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace Rune.Controls;

// Placing a signature on the page.
//
// The gesture is deliberately dual: a plain click drops the signature at the
// size last used, which is what makes signing several fields in a row quick,
// while press-drag-release draws the box explicitly when a field is an odd
// size. Both keep the image's aspect ratio — a stretched signature looks wrong
// immediately.
public sealed partial class PdfViewer
{
    private byte[]? _signatureBgra;
    private int _signaturePixelW;
    private int _signaturePixelH;
    private CanvasBitmap? _signatureGhost;

    private bool _placingSignature;
    private int _signaturePage = -1;
    private Point _signatureStart;
    private Point _signatureEnd;

    /// <summary>
    /// Where the pointer is while the Sign tool is armed but nothing has been
    /// pressed yet, so the ghost can show the landing spot *before* you commit
    /// to it. Null when the pointer is outside the canvas.
    /// </summary>
    private Point? _signatureHover;

    /// <summary>Latched when the ghost bitmap can't be built, so it isn't retried every frame.</summary>
    private bool _ghostFailed;

    /// <summary>
    /// Width in page points used by a plain click. Remembered across placements
    /// (and persisted by the shell) so repeat signing is consistent.
    /// </summary>
    public double SignatureWidthPt { get; set; } = 180;

    /// <summary>Raised after a signature is placed, with the width actually used.</summary>
    public event EventHandler<double>? SignaturePlaced;

    public bool HasPendingSignature => _signatureBgra is not null;

    /// <summary>
    /// Arms a signature for placement. Pixels are straight BGRA, as produced by
    /// <see cref="SignaturePad"/> or <see cref="Services.SignatureStore"/>.
    /// </summary>
    public void SetPendingSignature(byte[] bgra, int width, int height)
    {
        _signatureBgra = bgra;
        _signaturePixelW = width;
        _signaturePixelH = height;
        _signatureGhost?.Dispose();
        _signatureGhost = null;
        _ghostFailed = false;
        CancelSignaturePlacement();
    }

    public void ClearPendingSignature()
    {
        _signatureBgra = null;
        _signatureGhost?.Dispose();
        _signatureGhost = null;
        _ghostFailed = false;
        CancelSignaturePlacement();
    }

    /// <summary>True when a placement drag was cancelled, so Esc can be consumed.</summary>
    public bool CancelSignaturePlacement()
    {
        if (!_placingSignature)
        {
            return false;
        }
        _placingSignature = false;
        _signaturePage = -1;
        Canvas.Invalidate();
        return true;
    }

    // ---- hover preview ----

    /// <summary>
    /// Tracks the pointer so the pending signature can be previewed under it.
    /// Returns true when the ghost needs redrawing.
    /// </summary>
    private bool UpdateSignatureHover(Point? docPoint)
    {
        if (!HasPendingSignature || _activeTool != AnnotationTool.Signature || _rotation != 0)
        {
            bool had = _signatureHover is not null;
            _signatureHover = null;
            return had;
        }
        _signatureHover = docPoint;
        return true;
    }

    /// <summary>
    /// Scales the pending signature before it is placed. Returns true when the
    /// wheel was consumed, which is what stops the page scrolling underneath a
    /// resize gesture.
    /// </summary>
    public bool TryResizePendingSignature(int wheelDelta)
    {
        if (!HasPendingSignature || _activeTool != AnnotationTool.Signature)
        {
            return false;
        }

        // Multiplicative so each notch feels the same at any size; the range is
        // wide enough for an initial box and a full-page flourish alike.
        double factor = wheelDelta > 0 ? 1.1 : 1 / 1.1;
        SignatureWidthPt = Math.Clamp(SignatureWidthPt * factor, 24, 720);
        SignatureResized?.Invoke(this, SignatureWidthPt);
        Canvas.Invalidate();
        return true;
    }

    /// <summary>Raised when the wheel resizes the pending signature, so the panel can show the size.</summary>
    public event EventHandler<double>? SignatureResized;

    // ---- gesture ----

    private void BeginSignaturePlacement(Point docPoint, Pointer pointer)
    {
        if (_layout is null || _document is null || _rotation != 0 || !HasPendingSignature)
        {
            return;
        }
        _signaturePage = _layout.PageAt(docPoint.Y);
        _signatureStart = ClampToPage(_signaturePage, docPoint);
        _signatureEnd = _signatureStart;
        _placingSignature = true;
        Canvas.CapturePointer(pointer);
    }

    private void UpdateSignaturePlacement(Point docPoint)
    {
        _signatureEnd = ClampToPage(_signaturePage, docPoint);
        Canvas.Invalidate();
    }

    /// <summary>
    /// The placement box in document space, aspect-locked to the image.
    /// A drag shorter than a few pixels in either axis counts as a click and
    /// uses the remembered width instead.
    /// </summary>
    private Rect SignatureBox()
    {
        double aspect = _signaturePixelH / (double)Math.Max(1, _signaturePixelW);
        double dragX = Math.Abs(_signatureEnd.X - _signatureStart.X);

        // A click (or a negligible drag) places at the remembered size, anchored
        // at the press point.
        if (dragX < 8)
        {
            double w = SignatureWidthPt * _zoom;
            return new Rect(_signatureStart.X, _signatureStart.Y, w, w * aspect);
        }

        // Width follows the drag; height follows the image's aspect, so the
        // signature is never stretched. Dragging left/up still works.
        double width = dragX;
        double height = width * aspect;
        double x = Math.Min(_signatureStart.X, _signatureEnd.X);
        double y = _signatureEnd.Y < _signatureStart.Y ? _signatureStart.Y - height : _signatureStart.Y;
        return new Rect(x, y, width, height);
    }

    private async void CommitSignaturePlacement()
    {
        _placingSignature = false;

        if (_document is not { } document || _layout is null || _signaturePage < 0
            || _signatureBgra is not { } bgra)
        {
            Canvas.Invalidate();
            return;
        }

        var box = SignatureBox();
        int page = _signaturePage;
        _signaturePage = -1;

        var pageRect = _layout.GetPageRect(page);
        // Document space → page-local top-left points (undo centering + zoom).
        double localX = (box.X - pageRect.X) / _zoom;
        double localY = (box.Y - pageRect.Y) / _zoom;
        double widthPt = box.Width / _zoom;
        double heightPt = box.Height / _zoom;

        int pw = _signaturePixelW, ph = _signaturePixelH;

        AnnotationSpec? spec;
        try
        {
            spec = await _scheduler.RunAsync(PdfWorkPriority.Interactive,
                () => document.AddStamp(page, localX, localY, widthPt, heightPt, bgra, pw, ph));
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

        SignatureWidthPt = widthPt;
        SignaturePlaced?.Invoke(this, widthPt);

        InvalidatePage(page);
        DocumentEdited?.Invoke(this, EventArgs.Empty);
        RaiseAnnotationAdded("signature", page, spec);
    }

    // ---- ghost ----

    /// <summary>
    /// Draws the pending signature where it would land. Called from DrawRegion
    /// alongside DrawLiveInk, so it sits above the page but below nothing else.
    /// </summary>
    private void DrawSignatureGhost(CanvasDrawingSession session)
    {
        if (_signatureBgra is not { } bgra)
        {
            return;
        }

        // Two states share one preview: hovering before the press (so you can
        // see where it will land and how big it will be), and mid-drag. Without
        // the first, the preview only ever appeared once the spot had already
        // been chosen, which made placement a guess.
        Rect box;
        if (_placingSignature)
        {
            box = SignatureBox();
        }
        else if (_signatureHover is { } hover)
        {
            double aspect = _signaturePixelH / (double)Math.Max(1, _signaturePixelW);
            double w = SignatureWidthPt * _zoom;
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
        if (_signatureGhost is { } ghost)
        {
            session.DrawImage(ghost, box, ghost.Bounds, 0.75f);
        }

        // A dashed outline reads as "not placed yet" even where the signature
        // itself is faint — and it is drawn outside the bitmap's try/catch so a
        // failed ghost degrades to an outline rather than to nothing at all.
        session.DrawRectangle(box, Color.FromArgb(180, 0, 120, 215), 1,
            new Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle
            {
                DashStyle = Microsoft.Graphics.Canvas.Geometry.CanvasDashStyle.Dash,
            });
    }

    /// <summary>
    /// Builds the ghost bitmap once per armed signature.
    ///
    /// PREMULTIPLIED, and converted here rather than stored that way: Direct2D
    /// rejects a straight-alpha bitmap outright with
    /// WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT (0x88982F80), while PDFium needs the
    /// straight buffer this converts from. Asking for CanvasAlphaMode.Straight
    /// is what made the hover preview show nothing at all — the throw escaped
    /// DrawSignatureGhost and took the dashed outline with it, so the whole
    /// preview vanished rather than degrading.
    ///
    /// Failures are swallowed after logging: a preview that cannot be built is
    /// never a reason to take the page's draw pass down.
    /// </summary>
    private void EnsureGhostBitmap(byte[] bgra)
    {
        if (_signatureGhost is not null || _ghostFailed)
        {
            return;
        }

        // Past MaxSingleTilePx a bitmap silently fails to draw inside a
        // CanvasVirtualControl session. Both callers cap their pixels at decode
        // time, so this should be unreachable.
        if (_signaturePixelW > TileMath.MaxSingleTilePx || _signaturePixelH > TileMath.MaxSingleTilePx)
        {
            _ghostFailed = true;
            return;
        }

        try
        {
            _signatureGhost = CanvasBitmap.CreateFromBytes(
                Canvas, SignatureMatte.ToPremultiplied(bgra), _signaturePixelW, _signaturePixelH,
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
