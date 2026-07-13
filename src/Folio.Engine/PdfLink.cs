namespace Folio.Engine;

/// <summary>
/// A clickable region on a page. The rectangle is in page points with a
/// top-left origin (already normalized from PDF's bottom-left space), so it
/// scales trivially into layout space by multiplying by the zoom.
/// Exactly one of <see cref="TargetPageIndex"/> / <see cref="Uri"/> is set.
/// </summary>
public sealed record PdfLink(
    double X, double Y, double Width, double Height,
    int TargetPageIndex,
    string? Uri)
{
    public bool IsInternal => TargetPageIndex >= 0;
}
