using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Rune.Engine;
using Rune.Services;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.UI;

namespace Rune.Controls;

/// <summary>
/// Captures a signature — drawn by hand or imported from a photo — and hands
/// back straight (non-premultiplied) BGRA pixels ready for
/// <c>PdfDocument.AddStamp</c>.
///
/// Input goes through <see cref="InkCanvas"/>; Win2D is used only offscreen to
/// rasterise the finished strokes, because a Win2D control hosted in a
/// ContentDialog's popup does not reliably get a device.
///
/// An imported photo runs through <see cref="SignatureMatte"/> so the paper
/// becomes transparent — without it a JPEG, which has no alpha at all, stamps
/// as an opaque rectangle over whatever it is placed on.
/// </summary>
public sealed partial class SignaturePad : UserControl
{
    private Color _ink = Colors.Black;

    /// <summary>An imported image, if one was chosen. Takes precedence over drawing.</summary>
    private SavedSignature? _imported;

    /// <summary>
    /// Reused across every re-key. Safe to hold because the preview always runs
    /// uncropped, so its dimensions never change while an import is loaded.
    /// </summary>
    private WriteableBitmap? _preview;

    /// <summary>Transparent margin left around the ink when the result is cropped.</summary>
    private const int CropPadding = 4;

    private const string DefaultCaption = "Draw your signature below, or import a photo of one.";
    private const string UnreadableCaption = "That image couldn't be read. Try a PNG or a JPEG.";

    /// <summary>The preview never crops, so the bitmap allocated at import stays the right size.</summary>
    private static readonly MatteOptions PreviewOptions = new() { Crop = false };

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

    /// <summary>Key the paper out of an imported photo. Seeded from settings, read back after Save.</summary>
    public bool RemoveBackground
    {
        get => RemoveBackgroundCheck.IsChecked == true;
        set => RemoveBackgroundCheck.IsChecked = value;
    }

    /// <summary>
    /// True once the user has ticked or unticked the box themselves.
    ///
    /// The pad also changes it on its own — off for a source that already has
    /// transparency, off again when the matte finds no ink — and those are
    /// decisions about ONE image. Persisting them as the global default would
    /// mean importing a single transparent PNG silently turned background
    /// removal off for every future import, which the user never asked for.
    /// </summary>
    public bool RemoveBackgroundChosenByUser { get; private set; }

    /// <summary>
    /// Why the last import failed, or null.
    ///
    /// A property rather than an event, deliberately: the host can only show
    /// this after the dialog closes (ShowNotice renders an InfoBar inside
    /// DocumentView, which sits BEHIND the modal dialog), and an event handed
    /// the host a message it then had to remember to clear — so importing a bad
    /// file and then a good one reported failure on top of a successful save.
    /// Reset at the top of every import, so it cannot go stale.
    /// </summary>
    public string? LastFailure { get; private set; }

    // ---- drawing ----

    private void Pad_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Starting to draw replaces any imported image — one or the other.
        ClearImport();

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
        ClearImport();
    }

    private void ClearImport()
    {
        if (_imported is null)
        {
            return; // called on every stroke start; nothing to undo
        }
        _imported = null;
        _preview = null;
        ImportedPreview.Source = null;
        RemoveBackgroundCheck.IsEnabled = false;
        Caption.Text = DefaultCaption;
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e) => await StartImportAsync();

    /// <summary>
    /// Runs the picker and loads the chosen image. Public so the Sign flyout's
    /// "Import image…" can open this dialog straight into the picker.
    /// </summary>
    public async Task StartImportAsync()
    {
        LastFailure = null;
        try
        {
            await ImportAsync();
        }
        catch (Exception ex)
        {
            // Never allowed to escape. Both callers are async void — the button
            // handler and the dialog's Opened event — where an unhandled
            // exception takes the whole process down rather than failing the
            // import. PickSingleFileAsync alone can throw for a second picker
            // already open or a dead owner window.
            Rune.Services.ErrorLog.Default.Write(nameof(SignaturePad), ex);
            Fail(UnreadableCaption);
        }
    }

    private async Task ImportAsync()
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

        // Capped at decode time so WIC does the scaling. Past MaxSingleTilePx a
        // bitmap silently fails to draw inside a CanvasVirtualControl session,
        // which is where the on-page hover ghost lives — a 4000px phone photo
        // would arm a signature that simply never appears.
        //
        // OpenReadAsync rather than the path: a picked file can be a OneDrive
        // placeholder or a Phone Link item whose Path is empty.
        SavedSignature? loaded;
        using (var stream = await file.OpenReadAsync())
        {
            loaded = await SignatureStore.TryLoadAsync(stream, file.Path, TileMath.MaxSingleTilePx);
        }

        if (loaded is null)
        {
            Fail(UnreadableCaption);
            return;
        }

        // An import replaces anything drawn — one or the other, never both.
        _strokes.Clear();
        Pad.Children.Clear();
        _imported = loaded;
        _preview = new WriteableBitmap(loaded.Width, loaded.Height);
        ImportedPreview.Source = _preview;
        RemoveBackgroundCheck.IsEnabled = true;

        // A hand-made transparent PNG is already done, so arrive with the box
        // unticked rather than proposing to key something that has no paper.
        // Programmatic, so it raises no Click and never counts as a choice.
        RemoveBackground = !SignatureMatte.HasMeaningfulAlpha(loaded.Bgra, loaded.Width, loaded.Height);
        RefreshImportedPreview();
    }

    private void Fail(string message)
    {
        LastFailure = message;
        Caption.Text = message;
    }

    /// <summary>Only ever raised by a real click or keypress — see the XAML for why that matters.</summary>
    private void RemoveBackground_Click(object sender, RoutedEventArgs e)
    {
        RemoveBackgroundChosenByUser = true;
        RefreshImportedPreview();
    }

    /// <summary>Re-keys the imported image and shows the result over a checkerboard.</summary>
    private void RefreshImportedPreview()
    {
        if (_imported is not { } img || _preview is null)
        {
            return;
        }

        byte[] shown = img.Bgra;
        if (RemoveBackground)
        {
            var keyed = SignatureMatte.RemoveBackground(img.Bgra, img.Width, img.Height, PreviewOptions);
            switch (keyed.Outcome)
            {
                case MatteOutcome.Keyed:
                    shown = keyed.Bgra;
                    Caption.Text = DefaultCaption;
                    break;

                case MatteOutcome.SkippedHasAlpha:
                    Caption.Text = "That image already has a transparent background.";
                    break;

                case MatteOutcome.NoInkFound:
                    // Untick rather than leaving a checkbox on that does nothing.
                    // Programmatic, so it raises no Click: it neither re-enters
                    // here nor counts as the user choosing a default.
                    Caption.Text = "Nothing in that photo looks like ink, so the background was left alone. "
                        + "A brighter, flatter shot usually fixes it.";
                    RemoveBackground = false;
                    break;
            }
        }
        else
        {
            Caption.Text = DefaultCaption;
        }

        var composited = CompositeOverChecker(shown, img.Width, img.Height);
        using (var stream = _preview.PixelBuffer.AsStream())
        {
            stream.Write(composited, 0, composited.Length);
        }
        _preview.Invalidate();
    }

    /// <summary>
    /// Composites straight-alpha BGRA over a checkerboard, opaque.
    ///
    /// Two reasons for the checkerboard rather than a flat fill. The pad is
    /// deliberately white (see the XAML), so transparency composited over it is
    /// invisible and the user cannot tell whether the paper actually went away.
    /// And a WriteableBitmap's PixelBuffer is PREMULTIPLIED while everything
    /// downstream of decode here is straight — compositing to an opaque result
    /// makes the two conventions agree, because at alpha 255 they are the same
    /// bytes. (Writing straight pixels into the buffer directly, as this used
    /// to, previewed any soft-alpha PNG with bright edges and a colour halo.)
    /// </summary>
    private static byte[] CompositeOverChecker(byte[] straight, int width, int height)
    {
        // Scaled with the image rather than fixed at 8px: the preview is shown
        // Stretch="Uniform" in a box a few hundred pixels tall, so a fixed cell
        // on a 1024px import would render about two screen pixels wide and read
        // as noise.
        int cell = Math.Max(4, Math.Max(width, height) / 24);

        var output = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            bool evenRow = (y / cell % 2) == 0;
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = (row + x) * 4;
                byte back = ((x / cell % 2) == 0) == evenRow ? (byte)0xFF : (byte)0xD8;
                int a = straight[i + 3];
                int inverse = 255 - a;
                output[i] = (byte)((straight[i] * a + back * inverse) / 255);
                output[i + 1] = (byte)((straight[i + 1] * a + back * inverse) / 255);
                output[i + 2] = (byte)((straight[i + 2] * a + back * inverse) / 255);
                output[i + 3] = 255;
            }
        }
        return output;
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
            // Keyed here rather than reusing the preview's buffer, because the
            // preview deliberately runs uncropped and what gets saved must be
            // cropped — for exactly the reason the drawn path is cropped below.
            // ~17ms in Release on a 1024px import, so doing it synchronously as
            // the dialog closes costs nothing anyone can see.
            if (RemoveBackground)
            {
                var keyed = SignatureMatte.RemoveBackground(
                    img.Bgra, img.Width, img.Height, new MatteOptions { Padding = CropPadding });
                if (keyed.Outcome == MatteOutcome.Keyed)
                {
                    return (keyed.Bgra, keyed.Width, keyed.Height);
                }
            }

            // Not keyed — but a source that arrived with its own transparency
            // still wants the crop. Skipped when the bounds already cover the
            // whole frame, which is always the case for an opaque import: the
            // copy would be several megabytes to produce the input again.
            if (SignatureMatte.AlphaBounds(img.Bgra, img.Width, img.Height, CropPadding) is { } opaque
                && (opaque.Width != img.Width || opaque.Height != img.Height))
            {
                return SignatureMatte.CropTo(
                    img.Bgra, img.Width, img.Height, opaque.X, opaque.Y, opaque.Width, opaque.Height);
            }
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
