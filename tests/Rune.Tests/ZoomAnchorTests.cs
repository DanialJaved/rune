using Rune.Engine;

namespace Rune.Tests;

/// <summary>
/// Zoom must keep the point under the cursor stationary.
///
/// The bug these tests exist for: the viewer used to scale the scroll offset by
/// the zoom ratio, which silently assumes document space scales purely with
/// zoom. It doesn't — PageLayout's Margin and PageGap are constant DIPs and
/// pages are centred — so the error accumulated once per page gap and the view
/// drifted further the deeper into the document you were.
/// <see cref="OldPureScaleFormula_DriftsDeepInTheDocument"/> pins that down.
/// </summary>
public class ZoomAnchorTests
{
    private const double ViewportW = 1200;
    private const double ViewportH = 800;

    private static PageLayout Layout(double zoom, int pages = 1000, float w = 612, float h = 792)
        => new([.. Enumerable.Repeat((w, h), pages)], zoom, rotation: 0, ViewportW, ViewportH);

    /// <summary>
    /// Zooms while holding a viewport point fixed, and returns where the
    /// anchored page point actually lands afterwards. Should be the point we
    /// started from.
    /// </summary>
    private static (double X, double Y) RoundTrip(
        double fromZoom, double toZoom, int page, double localX, double localY,
        double viewportX, double viewportY, int pages = 1000)
    {
        var before = Layout(fromZoom, pages);

        // Put the chosen page point exactly under the viewport position.
        var rect = before.GetPageRect(page);
        double scrollX = rect.X + localX * fromZoom - viewportX;
        double scrollY = rect.Y + localY * fromZoom - viewportY;

        var anchor = ZoomAnchor.Capture(before, scrollX, scrollY, viewportX, viewportY);

        var after = Layout(toZoom, pages);
        var (newScrollX, newScrollY) = ZoomAnchor.Restore(after, anchor, viewportX, viewportY);

        // Where does the page point now sit relative to the viewport position?
        var newRect = after.GetPageRect(page);
        return (newRect.X + localX * toZoom - newScrollX - viewportX,
                newRect.Y + localY * toZoom - newScrollY - viewportY);
    }

    [Theory]
    // Zoom pairs across in, out, and a non-integer ratio...
    [InlineData(1.0, 2.0, 0)]
    [InlineData(1.0, 2.0, 1)]
    [InlineData(1.0, 2.0, 40)]
    [InlineData(1.0, 2.0, 999)]
    [InlineData(0.5, 1.0, 40)]
    [InlineData(2.0, 0.75, 40)]
    [InlineData(2.0, 0.75, 999)]
    [InlineData(1.0, 6.4, 500)]
    [InlineData(6.4, 0.1, 500)]
    public void AnchoredPoint_StaysUnderTheCursor(double fromZoom, double toZoom, int page)
    {
        var (dx, dy) = RoundTrip(fromZoom, toZoom, page, localX: 300, localY: 400,
            viewportX: 640, viewportY: 380);

        Assert.True(Math.Abs(dx) < 0.5, $"horizontal drift {dx:0.###} DIP at page {page}");
        Assert.True(Math.Abs(dy) < 0.5, $"vertical drift {dy:0.###} DIP at page {page}");
    }

    [Theory]
    [InlineData(0, 0)]           // top-left corner of the viewport
    [InlineData(1200, 800)]      // bottom-right corner
    [InlineData(640, 380)]       // middle
    public void AnyViewportPosition_Anchors(double viewportX, double viewportY)
    {
        var (dx, dy) = RoundTrip(1.0, 2.5, page: 40, localX: 200, localY: 500, viewportX, viewportY);

        Assert.True(Math.Abs(dx) < 0.5, $"horizontal drift {dx:0.###}");
        Assert.True(Math.Abs(dy) < 0.5, $"vertical drift {dy:0.###}");
    }

    [Fact]
    public void ShortDocument_VerticalCentringDoesNotBreakTheAnchor()
    {
        // One page at low zoom is shorter than the viewport, so PageLayout
        // shifts every page down to centre it. That shift is a constant the
        // old ratio maths had no way to account for.
        var (dx, dy) = RoundTrip(0.3, 1.2, page: 0, localX: 300, localY: 400,
            viewportX: 500, viewportY: 300, pages: 1);

        Assert.True(Math.Abs(dx) < 0.5, $"horizontal drift {dx:0.###}");
        Assert.True(Math.Abs(dy) < 0.5, $"vertical drift {dy:0.###}");
    }

    [Fact]
    public void NarrowDocument_HorizontalCentringDoesNotBreakTheAnchor()
    {
        // At low zoom the page is narrower than the viewport, so TotalWidth is
        // pinned to the viewport and the page is centred inside it. Zooming in
        // past the viewport width switches which term wins — the transition the
        // ratio maths got most wrong.
        var (dx, dy) = RoundTrip(0.5, 3.0, page: 10, localX: 306, localY: 396,
            viewportX: 600, viewportY: 400);

        Assert.True(Math.Abs(dx) < 0.5, $"horizontal drift {dx:0.###}");
        Assert.True(Math.Abs(dy) < 0.5, $"vertical drift {dy:0.###}");
    }

    [Fact]
    public void LandscapePages_Anchor()
    {
        var before = new PageLayout([.. Enumerable.Repeat((720f, 540f), 50)], 1.0, 0, ViewportW, ViewportH);
        var rect = before.GetPageRect(20);
        var anchor = ZoomAnchor.Capture(before, rect.X + 100 - 400, rect.Y + 200 - 300, 400, 300);

        var after = new PageLayout([.. Enumerable.Repeat((720f, 540f), 50)], 2.2, 0, ViewportW, ViewportH);
        var (sx, sy) = ZoomAnchor.Restore(after, anchor, 400, 300);
        var newRect = after.GetPageRect(20);

        Assert.True(Math.Abs(newRect.X + 100 * 2.2 - sx - 400) < 0.5);
        Assert.True(Math.Abs(newRect.Y + 200 * 2.2 - sy - 300) < 0.5);
    }

    [Fact]
    public void AnchorPastEndOfDocument_IsClampedNotThrown()
    {
        // Pages can be deleted between capture and restore.
        var after = Layout(1.0, pages: 3);
        var stale = new PageAnchor(900, 100, 100);

        var (x, y) = ZoomAnchor.Restore(after, stale, 100, 100);
        Assert.True(double.IsFinite(x) && double.IsFinite(y));
    }

    /// <summary>
    /// Characterizes the bug this work fixes. The old code scaled the raw
    /// scroll offset by the zoom ratio; this reproduces that formula and shows
    /// it drifts by tens of DIPs deep in a document — far past anything a user
    /// would call "following the cursor".
    /// </summary>
    [Fact]
    public void OldPureScaleFormula_DriftsDeepInTheDocument()
    {
        const double fromZoom = 1.0, toZoom = 2.0;
        const double viewportX = 640, viewportY = 380;
        const double localX = 300, localY = 400;

        static double OldDrift(int page, double fromZoom, double toZoom, double localY, double viewportY)
        {
            var before = Layout(fromZoom);
            var rect = before.GetPageRect(page);
            double scrollY = rect.Y + localY * fromZoom - viewportY;

            // The old arithmetic: documentY * ratio - anchor.
            double ratio = toZoom / fromZoom;
            double newScrollY = (scrollY + viewportY) * ratio - viewportY;

            var after = Layout(toZoom);
            return after.GetPageRect(page).Y + localY * toZoom - newScrollY - viewportY;
        }

        double driftPage0 = OldDrift(0, fromZoom, toZoom, localY, viewportY);
        double driftPage40 = OldDrift(40, fromZoom, toZoom, localY, viewportY);
        double driftPage999 = OldDrift(999, fromZoom, toZoom, localY, viewportY);

        // Barely visible on page 1 — which is why this shipped.
        Assert.True(Math.Abs(driftPage0) < 30, $"page 0 drift {driftPage0:0.#}");
        // Clearly wrong by page 40, and hopeless deep in a long document.
        Assert.True(Math.Abs(driftPage40) > 100, $"page 40 drift was only {driftPage40:0.#}");
        Assert.True(Math.Abs(driftPage999) > Math.Abs(driftPage40) * 10,
            $"drift should grow with page index: page 40 {driftPage40:0.#}, page 999 {driftPage999:0.#}");

        // The replacement holds steady exactly where the old one fell apart.
        var (_, newDrift) = RoundTrip(fromZoom, toZoom, 999, localX, localY, viewportX, viewportY);
        Assert.True(Math.Abs(newDrift) < 0.5, $"new formula drift {newDrift:0.###}");
    }
}
