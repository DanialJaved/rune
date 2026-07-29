using Rune.Engine;

namespace Rune.Tests;

public class ViewRotationMathTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(4, 0)]
    [InlineData(7, 3)]
    [InlineData(-1, 3)]   // rotate left from 0 — the case a naive % gets wrong
    [InlineData(-5, 3)]
    public void Normalize_MapsAnyTurnCountTo0Through3(int input, int expected) =>
        Assert.Equal(expected, ViewRotationMath.Normalize(input));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(-1, true)]
    public void SwapsAxes_IsTrueForQuarterTurns(int rotation, bool expected) =>
        Assert.Equal(expected, ViewRotationMath.SwapsAxes(rotation));
}

public class ThumbnailMetricsTests
{
    private const double Width = 168;
    private const double Min = 96;
    private const double Max = 320;

    private static double Height(double ptW, double ptH, int rotation = 0) =>
        ThumbnailMetrics.BoxHeight(Width, ptW, ptH, rotation, Min, Max);

    [Fact]
    public void Portrait_MatchesPageRatio()
    {
        Assert.Equal(217, Height(612, 792));   // US Letter
        Assert.Equal(238, Height(595, 842));   // A4
        Assert.Equal(277, Height(612, 1008));  // Legal
    }

    [Fact]
    public void LandscapeSlides_AreNotLetterboxed()
    {
        // The reported bug: a 4:3 deck was shown in a 210-tall portrait box.
        Assert.Equal(126, Height(720, 540));   // 4:3
        Assert.Equal(119, Height(842, 595));   // A4 landscape
    }

    [Fact]
    public void ExtremeRatios_AreClamped()
    {
        Assert.Equal(Max, Height(100, 1000));  // 1:10 poster would be ~1680
        Assert.Equal(Min, Height(1000, 100));  // 10:1 banner would be ~17
    }

    [Fact]
    public void QuarterTurn_SwapsTheAspect()
    {
        double portrait = Height(612, 792);
        double rotated = Height(612, 792, rotation: 1);

        Assert.Equal(130, rotated);            // 168 * 612/792
        Assert.NotEqual(portrait, rotated);
        // A half turn returns to the original shape.
        Assert.Equal(portrait, Height(612, 792, rotation: 2));
    }

    [Fact]
    public void NegativeRotation_MatchesItsPositiveEquivalent() =>
        Assert.Equal(Height(720, 540, rotation: 3), Height(720, 540, rotation: -1));

    [Theory]
    [InlineData(0, 792)]
    [InlineData(612, 0)]
    [InlineData(-1, -1)]
    public void DegenerateSizes_FallBackInsteadOfProducingNaN(double ptW, double ptH)
    {
        double h = Height(ptW, ptH);

        Assert.False(double.IsNaN(h), "a NaN height throws during XAML layout");
        Assert.InRange(h, Min, Max);
    }
}
