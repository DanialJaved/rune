using Rune.Engine;

namespace Rune.Tests;

/// <summary>
/// Page geometry has to survive a rotated view.
///
/// The bug these tests exist for: <c>PdfViewer.ToPageLocal</c> subtracted the
/// layout rect origin and divided by zoom, which yields a point in the *drawn*
/// box — while every consumer (<see cref="PageText.CharBoxes"/>, form field
/// rects, annotation rects, <c>AddStamp</c>) works in unrotated page-local
/// points. Rather than hand back the wrong answer, fourteen call sites across
/// the viewer early-returned on <c>_rotation != 0</c>, which is why rotating the
/// page silently killed selection, annotation, links, form filling and the whole
/// signature flow. <see cref="OldFormula_LandsOnTheWrongPartOfThePage"/> pins
/// down what those guards were hiding.
/// </summary>
public class PageRotationTests
{
    // Deliberately non-square: a square page hides every axis-swap error.
    private const double W = 612;
    private const double H = 792;

    private static PageRotationTransform T(int rotation) => new(rotation, W, H);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PointRoundTrips(int rotation)
    {
        var t = T(rotation);

        foreach (var (x, y) in new[] { (0.0, 0.0), (W, 0.0), (0.0, H), (W, H), (137.5, 604.25) })
        {
            var (dx, dy) = t.ToDrawn(x, y);
            var (bx, by) = t.ToUnrotated(dx, dy);

            Assert.Equal(x, bx, 9);
            Assert.Equal(y, by, 9);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryDrawnPointLandsInsideTheDrawnBox(int rotation)
    {
        var t = T(rotation);
        var (boxW, boxH) = t.DrawnSize;

        for (double x = 0; x <= W; x += W / 8)
        {
            for (double y = 0; y <= H; y += H / 8)
            {
                var (dx, dy) = t.ToDrawn(x, y);
                Assert.InRange(dx, 0, boxW);
                Assert.InRange(dy, 0, boxH);
            }
        }
    }

    [Theory]
    [InlineData(0, W, H)]
    [InlineData(1, H, W)]
    [InlineData(2, W, H)]
    [InlineData(3, H, W)]
    public void QuarterTurnsSwapTheDrawnSize(int rotation, double expectedW, double expectedH)
    {
        var (w, h) = T(rotation).DrawnSize;

        Assert.Equal(expectedW, w, 9);
        Assert.Equal(expectedH, h, 9);
    }

    /// <summary>
    /// The corner mapping, spelled out. These are the values that decide whether
    /// a highlight lands on the words or beside them, so they are asserted
    /// literally rather than derived — a transform that is self-consistently
    /// wrong would pass the round-trip test above.
    /// </summary>
    [Theory]
    // rotation 1 (90° clockwise): the page's top-left corner is drawn top-right.
    [InlineData(1, 0, 0, H, 0)]
    [InlineData(1, W, 0, H, W)]
    [InlineData(1, 0, H, 0, 0)]
    // rotation 2: opposite corner.
    [InlineData(2, 0, 0, W, H)]
    [InlineData(2, W, H, 0, 0)]
    // rotation 3 (90° counter-clockwise): top-left is drawn bottom-left.
    [InlineData(3, 0, 0, 0, W)]
    [InlineData(3, W, 0, 0, 0)]
    [InlineData(3, 0, H, H, W)]
    public void CornersMapWhereThePageIsActuallyDrawn(
        int rotation, double x, double y, double expectedDrawnX, double expectedDrawnY)
    {
        var (dx, dy) = T(rotation).ToDrawn(x, y);

        Assert.Equal(expectedDrawnX, dx, 9);
        Assert.Equal(expectedDrawnY, dy, 9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RectsKeepPositiveSizeAndSwapOnAQuarterTurn(int rotation)
    {
        var t = T(rotation);
        var r = new TextRect(100, 200, 40, 12);

        var drawn = t.ToDrawn(r);

        Assert.True(drawn.Width > 0 && drawn.Height > 0,
            $"rotation {rotation} produced {drawn.Width}x{drawn.Height}");

        if (ViewRotationMath.SwapsAxes(rotation))
        {
            Assert.Equal(r.Height, drawn.Width, 9);
            Assert.Equal(r.Width, drawn.Height, 9);
        }
        else
        {
            Assert.Equal(r.Width, drawn.Width, 9);
            Assert.Equal(r.Height, drawn.Height, 9);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ARectStaysInsideTheDrawnBox(int rotation)
    {
        var t = T(rotation);
        var (boxW, boxH) = t.DrawnSize;

        // A rect hugging each edge of the page must still hug an edge of the box.
        foreach (var r in new[]
        {
            new TextRect(0, 0, 50, 10),
            new TextRect(W - 50, 0, 50, 10),
            new TextRect(0, H - 10, 50, 10),
            new TextRect(W - 50, H - 10, 50, 10),
        })
        {
            var d = t.ToDrawn(r);
            Assert.InRange(d.X, 0, boxW - d.Width);
            Assert.InRange(d.Y, 0, boxH - d.Height);
        }
    }

    /// <summary>
    /// A rotation the caller never normalized (rotate-left from 0 gives -1)
    /// must not index off the end or reach PDFium negative — the reason
    /// <see cref="ViewRotationMath"/> exists in the first place.
    /// </summary>
    [Theory]
    [InlineData(-1, 3)]
    [InlineData(4, 0)]
    [InlineData(7, 3)]
    [InlineData(-4, 0)]
    public void UnnormalizedRotationsBehaveLikeTheirNormalForm(int raw, int normalized)
    {
        var (rawX, rawY) = T(raw).ToDrawn(137.5, 604.25);
        var (normX, normY) = T(normalized).ToDrawn(137.5, 604.25);

        Assert.Equal(normX, rawX, 9);
        Assert.Equal(normY, rawY, 9);
    }

    /// <summary>
    /// Characterizes what the rotation guards were protecting against. The old
    /// arithmetic treated a drawn-box point as if it were already page-local, so
    /// on a quarter turn it reported a point that is not merely off by a few
    /// points but on a different part of the page entirely — and for a portrait
    /// page it can fall outside the page's own width, which is how a click ended
    /// up hitting no field and no character at all.
    /// </summary>
    [Fact]
    public void OldFormula_LandsOnTheWrongPartOfThePage()
    {
        var t = T(1);

        // A click near the top-left of what the user sees, at 90° clockwise.
        const double drawnX = 700, drawnY = 60;

        // What the old code did: pass the drawn point straight through.
        (double X, double Y) old = (drawnX, drawnY);
        var correct = t.ToUnrotated(drawnX, drawnY);

        // The correct answer is near the page's top edge; the old one is 700
        // points across a 612-point-wide page, i.e. off the page.
        Assert.True(old.X > W, $"expected the old answer to fall off the page, got x={old.X}");
        Assert.InRange(correct.X, 0, W);
        Assert.InRange(correct.Y, 0, H);

        // And the two are nowhere near each other.
        double error = Math.Sqrt(Math.Pow(old.X - correct.X, 2) + Math.Pow(old.Y - correct.Y, 2));
        Assert.True(error > 600, $"expected a gross error, got {error:0.#} points");
    }

    /// <summary>
    /// The transform has to agree with <see cref="PageLayout"/> about how big the
    /// drawn page is, or highlights would scale correctly and sit in the wrong
    /// box. PageLayout swaps independently, so this pins the two together.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DrawnSizeAgreesWithPageLayout(int rotation)
    {
        const double zoom = 1.75;
        var layout = new PageLayout([((float)W, (float)H)], zoom, rotation, 1200, 800);
        var rect = layout.GetPageRect(0);
        var (w, h) = T(rotation).DrawnSize;

        Assert.Equal(rect.Width, w * zoom, 6);
        Assert.Equal(rect.Height, h * zoom, 6);
    }
}
