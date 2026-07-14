using Rune.Engine;

namespace Rune.Tests;

public class PageLayoutTests
{
    private static readonly (float, float)[] ThreeLetterPages =
    [
        (612f, 792f), (612f, 792f), (612f, 792f),
    ];

    [Fact]
    public void Layout_StacksPagesWithGapsAndMargins()
    {
        var layout = new PageLayout(ThreeLetterPages, zoom: 1.0, rotation: 0);

        Assert.Equal(612 + 2 * PageLayout.Margin, layout.TotalWidth, precision: 3);
        Assert.Equal(2 * PageLayout.Margin + 3 * 792 + 2 * PageLayout.PageGap, layout.TotalHeight, precision: 3);

        var page1 = layout.GetPageRect(1);
        Assert.Equal(PageLayout.Margin, page1.X, precision: 3); // centered == margin when widths are uniform
        Assert.Equal(PageLayout.Margin + 792 + PageLayout.PageGap, page1.Y, precision: 3);
    }

    [Fact]
    public void Layout_Rotation90_SwapsPageDimensions()
    {
        var layout = new PageLayout(ThreeLetterPages, zoom: 1.0, rotation: 1);

        var page0 = layout.GetPageRect(0);
        Assert.Equal(792, page0.Width, precision: 3);
        Assert.Equal(612, page0.Height, precision: 3);
    }

    [Fact]
    public void Layout_Zoom_ScalesEverything()
    {
        var layout = new PageLayout(ThreeLetterPages, zoom: 2.0, rotation: 0);
        Assert.Equal(1224, layout.GetPageRect(0).Width, precision: 3);
    }

    [Fact]
    public void PagesInVerticalRange_FindsIntersectingPages()
    {
        var layout = new PageLayout(ThreeLetterPages, zoom: 1.0, rotation: 0);

        // A window straddling the boundary between page 0 and page 1.
        double boundary = layout.GetPageRect(0).Bottom + 1;
        var (first, last) = layout.PagesInVerticalRange(boundary - 100, boundary + 100);
        Assert.Equal(0, first);
        Assert.Equal(1, last);

        // A window entirely inside page 2.
        (first, last) = layout.PagesInVerticalRange(layout.GetPageRect(2).Y + 10, layout.GetPageRect(2).Y + 20);
        Assert.Equal(2, first);
        Assert.Equal(2, last);
    }

    [Fact]
    public void PageAt_ReturnsPageContainingY()
    {
        var layout = new PageLayout(ThreeLetterPages, zoom: 1.0, rotation: 0);
        Assert.Equal(0, layout.PageAt(100));
        Assert.Equal(1, layout.PageAt(layout.GetPageRect(1).Y + 10));
        Assert.Equal(2, layout.PageAt(layout.TotalHeight + 500)); // past the end clamps to last
    }
}

public class TileMathTests
{
    [Fact]
    public void SmallPage_IsSingleTile()
    {
        Assert.Equal((1, 1), TileMath.GridFor(800, 1000));
        Assert.Equal((0, 0, 800, 1000), TileMath.TileWindow(800, 1000, 0, 0));
    }

    [Fact]
    public void NoTileEverExceedsTheEdgeLimit()
    {
        // Regression: bitmaps wider than ~1.5k px silently fail to draw in
        // CanvasVirtualControl sessions on some devices, so every produced
        // tile must stay within TileSizePx in both dimensions.
        foreach (var (w, h) in new[] { (1200, 1600), (1763, 1362), (1025, 800), (5000, 4000) })
        {
            var (cols, rows) = TileMath.GridFor(w, h);
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    var (_, _, tw, th) = TileMath.TileWindow(w, h, r, c);
                    Assert.True(tw <= TileMath.TileSizePx && th <= TileMath.TileSizePx,
                        $"page {w}x{h} tile {r},{c} is {tw}x{th}");
                }
            }
        }
    }

    [Fact]
    public void LargePage_SplitsIntoTileGrid()
    {
        var (cols, rows) = TileMath.GridFor(3000, 2500);
        Assert.Equal(3, cols);
        Assert.Equal(3, rows);

        // Last column/row tiles are clipped to the page edge.
        Assert.Equal((2048, 2048, 952, 452), TileMath.TileWindow(3000, 2500, 2, 2));
        Assert.Equal((1024, 0, 1024, 1024), TileMath.TileWindow(3000, 2500, 0, 1));
    }
}

public class RenderRegionTests
{
    private static string CorpusPath(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    [Fact]
    public void RenderRegion_MatchesWindowOfFullRender()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        var full = doc.RenderPage(0, scale: 1.0f);
        var tile = doc.RenderRegion(0, scale: 1.0f, rotation: 0, srcX: 200, srcY: 250, width: 100, height: 80);

        for (int y = 0; y < 80; y++)
        {
            var expected = full.Pixels.AsSpan((y + 250) * full.Stride + 200 * 4, 100 * 4);
            var actual = tile.Pixels.AsSpan(y * tile.Stride, 100 * 4);
            Assert.True(expected.SequenceEqual(actual), $"tile row {y} differs from full render");
        }
    }

    [Fact]
    public void RenderPage_Rotation90_SwapsOutputDimensions()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));
        var bmp = doc.RenderPage(0, scale: 1.0f, rotation: 1);
        Assert.Equal(792, bmp.Width);
        Assert.Equal(612, bmp.Height);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RenderPage_Rotated_ActuallyDrawsInk(int rotation)
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        var bmp = doc.RenderPage(0, scale: 1.0f, rotation);

        int nonWhite = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                int i = y * bmp.Stride + x * 4;
                if (bmp.Pixels[i] != 0xFF || bmp.Pixels[i + 1] != 0xFF || bmp.Pixels[i + 2] != 0xFF)
                {
                    nonWhite++;
                }
            }
        }
        Assert.True(nonWhite > 100, $"rotation {rotation}: expected text ink, found {nonWhite} non-white pixels");
    }

    [Fact]
    public void RenderRegion_RotatedTile_MatchesWindowOfRotatedFullRender()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        var full = doc.RenderPage(0, scale: 1.0f, rotation: 1);          // 792×612
        var tile = doc.RenderRegion(0, scale: 1.0f, rotation: 1, srcX: 100, srcY: 150, width: 120, height: 90);

        for (int y = 0; y < 90; y++)
        {
            var expected = full.Pixels.AsSpan((y + 150) * full.Stride + 100 * 4, 120 * 4);
            var actual = tile.Pixels.AsSpan(y * tile.Stride, 120 * 4);
            Assert.True(expected.SequenceEqual(actual), $"rotated tile row {y} differs from full render window");
        }
    }
}

public class RenderSchedulerTests
{
    private static string CorpusPath(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    [Fact]
    public void Scheduler_RendersDesiredTilesInOrder()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        var rendered = new List<TileKey>();
        using var done = new ManualResetEventSlim();
        using var scheduler = new RenderScheduler((request, bitmap) =>
        {
            lock (rendered)
            {
                rendered.Add(request.Key);
                if (rendered.Count == 2)
                {
                    done.Set();
                }
            }
        });

        scheduler.SetDocument(doc);
        scheduler.SetDesired(
        [
            MakeRequest(doc, pageIndex: 1),
            MakeRequest(doc, pageIndex: 0),
        ]);

        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "scheduler did not render both tiles in time");
        lock (rendered)
        {
            Assert.Equal(1, rendered[0].PageIndex); // priority order preserved
            Assert.Equal(0, rendered[1].PageIndex);
        }
    }

    [Fact]
    public void Scheduler_SurvivesDocumentSwapMidStream()
    {
        using var doc1 = PdfDocument.Open(CorpusPath("hello.pdf"));
        using var doc2 = PdfDocument.Open(CorpusPath("hello.pdf"));

        using var scheduler = new RenderScheduler((_, _) => { });
        scheduler.SetDocument(doc1);
        scheduler.SetDesired([MakeRequest(doc1, 0), MakeRequest(doc1, 1)]);
        scheduler.SetDocument(doc2); // must drop doc1's pending work without crashing
        scheduler.SetDesired([MakeRequest(doc2, 0)]);
        Thread.Sleep(200);
    }

    private static TileRequest MakeRequest(PdfDocument doc, int pageIndex)
    {
        const float scale = 0.5f;
        var (w, h) = doc.GetPagePixelSize(pageIndex, scale, rotation: 0);
        return new TileRequest(
            new TileKey(pageIndex, TileKey.ToScaleKey(scale), 0, 0, 0, IsPreview: false),
            scale, 0, 0, w, h);
    }
}

public class LargeDocumentTests
{
    private static string CorpusPath(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    [Fact]
    public void Open_1000PageBook_IsFastAndRendersMidDocument()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var doc = PdfDocument.Open(CorpusPath("book-1000.pdf"));
        stopwatch.Stop();

        Assert.Equal(1000, doc.PageCount);
        // Budget: cold open of a 1000-page file, including all page sizes,
        // must stay well under the 300 ms target for typical files.
        Assert.True(stopwatch.ElapsedMilliseconds < 1500, $"open took {stopwatch.ElapsedMilliseconds} ms");

        var bmp = doc.RenderPage(499, scale: 1.0f);
        Assert.Equal(612, bmp.Width);
    }
}
