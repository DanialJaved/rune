namespace Rune.Engine;

/// <summary>
/// Sizing for thumbnail boxes. Pure arithmetic, kept out of the UI so it can be
/// unit-tested: a wrong result here shows up as letterboxed slides or, worse, a
/// NaN reaching a XAML Height (which throws during layout).
/// </summary>
public static class ThumbnailMetrics
{
    /// <summary>US Letter, used when a page reports a nonsense size.</summary>
    private const double FallbackRatio = 792.0 / 612.0;

    /// <summary>
    /// Height for a thumbnail box of <paramref name="boxWidth"/> showing a page
    /// of <paramref name="ptWidth"/>×<paramref name="ptHeight"/> points at the
    /// given view rotation, so the box matches the page's shape instead of
    /// letterboxing it.
    /// </summary>
    public static double BoxHeight(
        double boxWidth, double ptWidth, double ptHeight,
        int rotationQuarterTurns, double minHeight, double maxHeight)
    {
        if (ViewRotationMath.SwapsAxes(rotationQuarterTurns))
        {
            (ptWidth, ptHeight) = (ptHeight, ptWidth);
        }

        double ratio = ptWidth > 0 && ptHeight > 0 ? ptHeight / ptWidth : FallbackRatio;
        // Whole DIPs: fractional heights accumulate visible drift down a list.
        return Math.Clamp(Math.Round(boxWidth * ratio), minHeight, maxHeight);
    }
}
