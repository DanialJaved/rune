namespace Rune.Engine;

/// <summary>
/// Maps between the two coordinate spaces a rotated page has, both in PDF points
/// with a <b>top-left origin</b>:
///
/// <list type="bullet">
/// <item><b>Unrotated page-local</b> — the space every consumer of page geometry
/// works in: <see cref="PageText.CharBoxes"/>, <see cref="PdfLink"/> rects, form
/// field rects, annotation rects, and the x/y <c>PdfDocument.AddStamp</c> takes.
/// It never changes when the view rotates, because it describes the file.</item>
/// <item><b>Drawn</b> — where the page actually lands on screen once
/// <c>FPDF_RenderPageBitmap</c> has rotated it, sized to match
/// <see cref="PageLayout.GetPageRect"/> (unscaled: multiply by zoom for layout
/// space).</item>
/// </list>
///
/// Rotation is quarter turns clockwise (0–3), matching PDFium's <c>rotate</c>
/// parameter and <see cref="PageLayout.Rotation"/>.
///
/// <para>
/// This is deliberately managed arithmetic rather than a call to
/// <c>FPDFNative.DeviceToPage</c>. Pointer moves hit it on every event, and
/// v0.4.0 moved selection hit-testing off PDFium precisely to stop that from
/// freezing the UI thread — routing it back through the render queue would
/// undo that fix. Being pure also means it tests without a document.
/// </para>
/// </summary>
public readonly struct PageRotationTransform
{
    private readonly int _rotation;
    private readonly double _width;
    private readonly double _height;

    /// <param name="rotation">Quarter turns clockwise; any integer, normalized.</param>
    /// <param name="unrotatedWidth">Page width in points, as the file declares it.</param>
    /// <param name="unrotatedHeight">Page height in points, as the file declares it.</param>
    public PageRotationTransform(int rotation, double unrotatedWidth, double unrotatedHeight)
    {
        _rotation = ViewRotationMath.Normalize(rotation);
        _width = unrotatedWidth;
        _height = unrotatedHeight;
    }

    public int Rotation => _rotation;

    /// <summary>The drawn box's size — width and height swapped on a quarter turn.</summary>
    public (double Width, double Height) DrawnSize =>
        ViewRotationMath.SwapsAxes(_rotation) ? (_height, _width) : (_width, _height);

    /// <summary>Unrotated page-local point → where it is drawn.</summary>
    public (double X, double Y) ToDrawn(double x, double y) => _rotation switch
    {
        1 => (_height - y, x),
        2 => (_width - x, _height - y),
        3 => (y, _width - x),
        _ => (x, y),
    };

    /// <summary>Drawn point → unrotated page-local. The inverse of <see cref="ToDrawn(double, double)"/>.</summary>
    public (double X, double Y) ToUnrotated(double drawnX, double drawnY) => _rotation switch
    {
        1 => (drawnY, _height - drawnX),
        2 => (_width - drawnX, _height - drawnY),
        3 => (_width - drawnY, drawnX),
        _ => (drawnX, drawnY),
    };

    /// <summary>
    /// Unrotated page-local rect → drawn rect. Corners are mapped and then
    /// normalized rather than special-casing each rotation, so a quarter turn
    /// cannot silently produce a negative width.
    /// </summary>
    public DipRect ToDrawn(TextRect r)
    {
        var (ax, ay) = ToDrawn(r.X, r.Y);
        var (bx, by) = ToDrawn(r.X + r.Width, r.Y + r.Height);
        return new DipRect(Math.Min(ax, bx), Math.Min(ay, by), Math.Abs(bx - ax), Math.Abs(by - ay));
    }

    /// <summary>
    /// Unrotated page-local rect → drawn rect, for callers holding loose values
    /// (form fields, annotation rects) rather than a <see cref="TextRect"/>.
    /// </summary>
    public DipRect ToDrawn(double x, double y, double width, double height) =>
        ToDrawn(new TextRect(x, y, width, height));
}
