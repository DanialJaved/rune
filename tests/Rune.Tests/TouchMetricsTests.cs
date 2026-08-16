using Rune.Engine;

namespace Rune.Tests;

/// <summary>
/// Pins the numbers that decide whether a fingertip can hit anything.
///
/// The point of these is the mouse half as much as the touch half: every value
/// here has to stay exactly what it was for a mouse, because the whole touch
/// pass was meant to add a path rather than move one. A regression that widened
/// mouse hit-testing would make links and form fields start stealing clicks from
/// the text underneath them, and nothing else in the suite would notice.
/// </summary>
public class TouchMetricsTests
{
    [Theory]
    [InlineData(0.25)]
    [InlineData(1.0)]
    [InlineData(6.4)]
    public void Mouse_GetsNoSlopAtAnyZoom(double zoom)
    {
        Assert.Equal(0, TouchMetrics.SlopPt(touch: false, zoom));
    }

    [Fact]
    public void Touch_SlopIsTheSameDistanceOnGlassAtEveryZoom()
    {
        // 8 DIP on the screen, whatever that is in page points. Zooming in
        // makes the page bigger, so the same finger covers fewer page points.
        Assert.Equal(8, TouchMetrics.SlopPt(touch: true, zoom: 1.0), 6);
        Assert.Equal(32, TouchMetrics.SlopPt(touch: true, zoom: 0.25), 6);
        Assert.Equal(2, TouchMetrics.SlopPt(touch: true, zoom: 4.0), 6);
    }

    [Fact]
    public void Slop_CannotBlowUpOnADegenerateZoom()
    {
        // Clamped at 0.05 rather than dividing by zero and handing a hit test
        // an infinite tolerance, which would match every target on the page.
        Assert.Equal(TouchMetrics.SlopPt(true, 0.05), TouchMetrics.SlopPt(true, 0));
        Assert.True(double.IsFinite(TouchMetrics.SlopPt(true, 0)));
        Assert.True(double.IsFinite(TouchMetrics.HandleReachPt(true, 0)));
    }

    [Fact]
    public void HandleReach_IsUnchangedForAMouse()
    {
        // The value that shipped: (10 / zoom) * 0.9.
        Assert.Equal(9, TouchMetrics.HandleReachPt(touch: false, zoom: 1.0), 6);
        Assert.Equal(4.5, TouchMetrics.HandleReachPt(touch: false, zoom: 2.0), 6);
    }

    [Fact]
    public void HandleReach_IsFarWiderForAFinger()
    {
        Assert.Equal(21.6, TouchMetrics.HandleReachPt(touch: true, zoom: 1.0), 6);

        // The whole point: a finger has to be able to grab a handle it cannot
        // land on precisely, at every zoom.
        foreach (double zoom in new[] { 0.1, 0.5, 1.0, 2.0, 6.4 })
        {
            Assert.True(
                TouchMetrics.HandleReachPt(true, zoom) > TouchMetrics.HandleReachPt(false, zoom),
                $"touch reach must exceed mouse reach at zoom {zoom}");
        }
    }

    [Fact]
    public void HandleReach_StaysInsideTheDrawnSquareForAMouse()
    {
        // A mouse must not be able to grab a handle from visibly outside it,
        // which is what the 0.9 is for.
        Assert.True(TouchMetrics.HandleReachPt(false, 1.0) < TouchMetrics.HandleDrawDip);
    }

    [Fact]
    public void PlacementThreshold_IsLooserForAFinger()
    {
        Assert.Equal(8, TouchMetrics.PlacementDragThreshold(touch: false));
        Assert.Equal(16, TouchMetrics.PlacementDragThreshold(touch: true));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(7.9, false)]
    [InlineData(8.1, true)]
    public void PlacementThreshold_MouseVerdictIsUnchanged(double dragX, bool isDrag)
    {
        Assert.Equal(isDrag, dragX >= TouchMetrics.PlacementDragThreshold(touch: false));
    }

    [Fact]
    public void PlacementThreshold_FingerJitterCountsAsATapNotADrag()
    {
        // 12 px of wobble is an ordinary tap on glass. It used to size the
        // picture at whatever the wobble happened to be.
        const double jitter = 12;
        Assert.True(jitter >= TouchMetrics.PlacementDragThreshold(touch: false));
        Assert.False(jitter >= TouchMetrics.PlacementDragThreshold(touch: true));
    }
}
