using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Rune.Services;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.UI;

namespace Rune.Controls;

/// <summary>
/// Captures a signature — drawn by hand or imported from an image file — and
/// hands back straight (non-premultiplied) BGRA pixels ready for
/// <c>PdfDocument.AddStamp</c>.
///
/// Input goes through <see cref="InkCanvas"/>; Win2D is used only offscreen to
/// rasterise the finished strokes, because a Win2D control hosted in a
/// ContentDialog's popup does not reliably get a device.
/// </summary>
public sealed partial class SignaturePad : UserControl
{
    private Color _ink = Colors.Black;

    /// <summary>An imported image, if one was chosen. Takes precedence over drawing.</summary>
    private SavedSignature? _imported;

    /// <summary>Stroke width in pad pixels. Chosen to read like a pen at this size.</summary>
    private const double StrokeWidth = 3.4;

    /// <summary>Completed strokes, in pad coordinates.</summary>
    private readonly List<List<Point>> _strokes = [];
    private List<Point>? _current;
    private Polyline? _currentLine;

    public SignaturePad()
    {
        InitializeComponent();
        UpdateSwatches();
    }

    /// <summary>True when there is something worth saving.</summary>
    public bool HasContent => _imported is not null || _strokes.Any(s => s.Count > 1);

    // ---- drawing ----

    private void Pad_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Starting to draw replaces any imported image — one or the other.
        _imported = null;
        ImportedPreview.Source = null;

        _current = [e.GetCurrentPoint(Pad).Position];
        _strokes.Add(_current);

        _currentLine = new Polyline
        {
            Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(_ink),
            StrokeThickness = StrokeWidth,
            StrokeLineJoin = Microsoft.UI.Xaml.Media.PenLineJoin.Round,
            StrokeStartLineCap = Microsoft.UI.Xaml.Media.PenLineCap.Round,
            StrokeEndLineCap = Microsoft.UI.Xaml.Media.PenLineCap.Round,
        };
        _currentLine.Points.Add(_current[0]);
        Pad.Children.Add(_currentLine);
        Pad.CapturePointer(e.Pointer);
    }

    private void Pad_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_current is null || _currentLine is null)
        {
            return;
        }
        var p = e.GetCurrentPoint(Pad).Position;
        // Skip sub-pixel jitter, same as the page's ink capture.
        if (Math.Abs(p.X - _current[^1].X) + Math.Abs(p.Y - _current[^1].Y) >= 1.0)
        {
            _current.Add(p);
            _currentLine.Points.Add(p);
        }
    }

    private void Pad_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _current = null;
        _currentLine = null;
        Pad.ReleasePointerCapture(e.Pointer);
    }

    // ---- controls ----

    private void BlackButton_Click(object sender, RoutedEventArgs e) => SetInk(Colors.Black);

    private void BlueButton_Click(object sender, RoutedEventArgs e) => SetInk(Color.FromArgb(255, 20, 60, 160));

    private void SetInk(Color color)
    {
        _ink = color;
        UpdateSwatches();
        // Recolour what's already drawn: the user is choosing the signature's
        // colour, not painting two-tone.
        var brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        foreach (var line in Pad.Children.OfType<Polyline>())
        {
            line.Stroke = brush;
        }
    }

    private void UpdateSwatches()
    {
        bool black = _ink.Equals(Colors.Black);
        BlackButton.Content = Swatch(Colors.Black, black);
        BlueButton.Content = Swatch(Color.FromArgb(255, 20, 60, 160), !black);
    }

    private static Border Swatch(Color color, bool selected) => new()
    {
        Width = 22,
        Height = 22,
        CornerRadius = new CornerRadius(11),
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color),
        BorderThickness = new Thickness(selected ? 2 : 0),
        BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.White),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _strokes.Clear();
        _current = null;
        _currentLine = null;
        Pad.Children.Clear();
        _imported = null;
        ImportedPreview.Source = null;
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        // Mandatory in WinUI 3: a picker with no owner window never opens.
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        var loaded = await SignatureStore.TryLoadAsync(file.Path);
        if (loaded is null)
        {
            return;
        }

        // An import replaces anything drawn — one or the other, never both.
        _strokes.Clear();
        Pad.Children.Clear();
        _imported = loaded;

        var preview = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(loaded.Width, loaded.Height);
        using (var stream = preview.PixelBuffer.AsStream())
        {
            stream.Write(loaded.Bgra, 0, loaded.Bgra.Length);
        }
        preview.Invalidate();
        ImportedPreview.Source = preview;
    }

    // ---- output ----

    /// <summary>
    /// Rasterises what's on the pad to straight BGRA, cropped to the ink.
    ///
    /// Cropping matters: a signature drawn in the middle of the pad would
    /// otherwise carry a wide transparent margin, and the placed size would
    /// never match what was drawn. Returns null if the pad is empty.
    /// </summary>
    public (byte[] Bgra, int Width, int Height)? Rasterize()
    {
        if (_imported is { } img)
        {
            return (img.Bgra, img.Width, img.Height);
        }

        if (InkBounds() is not { } box)
        {
            return null;
        }

        // Render at 2x so the placed signature stays crisp when scaled up on
        // the page; capped well inside TileMath.MaxSingleTilePx.
        const float scale = 2f;
        const double pad = 4;
        int width = Math.Clamp((int)Math.Ceiling((box.Width + pad * 2) * scale), 1, 1024);
        int height = Math.Clamp((int)Math.Ceiling((box.Height + pad * 2) * scale), 1, 1024);

        using var target = new CanvasRenderTarget(
            CanvasDevice.GetSharedDevice(), width, height, 96,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
            CanvasAlphaMode.Premultiplied);

        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Colors.Transparent);
            // Shift the ink to the origin before scaling, so the crop is exact.
            session.Transform =
                System.Numerics.Matrix3x2.CreateTranslation((float)(-box.X + pad), (float)(-box.Y + pad))
                * System.Numerics.Matrix3x2.CreateScale(scale);

            var style = new Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle
            {
                StartCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Round,
                EndCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Round,
                LineJoin = Microsoft.Graphics.Canvas.Geometry.CanvasLineJoin.Round,
            };
            foreach (var stroke in _strokes.Where(s => s.Count > 1))
            {
                for (int i = 1; i < stroke.Count; i++)
                {
                    session.DrawLine(
                        (float)stroke[i - 1].X, (float)stroke[i - 1].Y,
                        (float)stroke[i].X, (float)stroke[i].Y,
                        _ink, (float)StrokeWidth, style);
                }
            }
        }

        return (ToStraightAlpha(target.GetPixelBytes()), width, height);
    }

    /// <summary>Bounding box of the drawn ink, in pad coordinates.</summary>
    private Rect? InkBounds()
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;

        foreach (var stroke in _strokes.Where(s => s.Count > 1))
        {
            foreach (var p in stroke)
            {
                any = true;
                minX = Math.Min(minX, p.X);
                minY = Math.Min(minY, p.Y);
                maxX = Math.Max(maxX, p.X);
                maxY = Math.Max(maxY, p.Y);
            }
        }
        return any
            ? new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY))
            : null;
    }

    /// <summary>
    /// Win2D renders premultiplied; PDFium composites the buffer as STRAIGHT
    /// alpha (pinned by StampTests.HalfAlphaGrey_CompositesAsStraightAlpha).
    /// Handing premultiplied pixels straight over would darken every
    /// antialiased edge into a halo, so undo the multiply here — the single
    /// place the two conventions meet.
    /// </summary>
    private static byte[] ToStraightAlpha(byte[] premultiplied)
    {
        for (int i = 0; i < premultiplied.Length; i += 4)
        {
            byte a = premultiplied[i + 3];
            if (a is 0 or 255)
            {
                continue; // fully transparent or fully opaque: identical either way
            }
            premultiplied[i] = (byte)Math.Min(255, premultiplied[i] * 255 / a);
            premultiplied[i + 1] = (byte)Math.Min(255, premultiplied[i + 1] * 255 / a);
            premultiplied[i + 2] = (byte)Math.Min(255, premultiplied[i + 2] * 255 / a);
        }
        return premultiplied;
    }
}
