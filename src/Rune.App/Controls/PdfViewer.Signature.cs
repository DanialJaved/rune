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
        CancelSignaturePlacement();
    }

    public void ClearPendingSignature()
    {
        _signatureBgra = null;
        _signatureGhost?.Dispose();
        _signatureGhost = null;
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
        if (!_placingSignature || _signatureBgra is not { } bgra)
        {
            return;
        }

        _signatureGhost ??= CanvasBitmap.CreateFromBytes(
            Canvas, bgra, _signaturePixelW, _signaturePixelH,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Straight);

        var box = SignatureBox();
        session.DrawImage(_signatureGhost, box, _signatureGhost.Bounds, 0.75f);
        // A dashed outline reads as "not placed yet" even where the signature
        // itself is faint.
        session.DrawRectangle(box, Color.FromArgb(180, 0, 120, 215), 1,
            new Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle
            {
                DashStyle = Microsoft.Graphics.Canvas.Geometry.CanvasDashStyle.Dash,
            });
    }
}
