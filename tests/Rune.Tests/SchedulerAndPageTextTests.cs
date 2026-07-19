using Rune.Engine;

namespace Rune.Tests;

public class SchedulerOpTests
{
    private static string CorpusPath(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    [Fact]
    public async Task RunAsync_ExecutesOnRenderThreadAndReturnsResult()
    {
        using var scheduler = new RenderScheduler((_, bmp) => bmp.Return());

        string? threadName = null;
        int result = await scheduler.RunAsync(PdfWorkPriority.Interactive, () =>
        {
            threadName = Thread.CurrentThread.Name;
            return 42;
        });

        Assert.Equal(42, result);
        Assert.Equal("Rune.RenderThread", threadName);
    }

    [Fact]
    public async Task RunAsync_InteractiveOutranksBackground()
    {
        using var scheduler = new RenderScheduler((_, bmp) => bmp.Return());
        var order = new List<string>();
        var gate = new ManualResetEventSlim(false);

        // Occupy the thread so both ops are queued before either runs.
        var blocker = scheduler.RunAsync(PdfWorkPriority.Interactive, () => gate.Wait());
        var background = scheduler.RunAsync(PdfWorkPriority.Background, () =>
        {
            lock (order) { order.Add("background"); }
        });
        var interactive = scheduler.RunAsync(PdfWorkPriority.Interactive, () =>
        {
            lock (order) { order.Add("interactive"); }
        });

        gate.Set();
        await Task.WhenAll(blocker, background, interactive);

        Assert.Equal(["interactive", "background"], order);
    }

    [Fact]
    public async Task RunAsync_PropagatesExceptions()
    {
        using var scheduler = new RenderScheduler((_, bmp) => bmp.Return());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scheduler.RunAsync<int>(PdfWorkPriority.Interactive,
                () => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task SetDocument_CancelsPendingOps()
    {
        using var scheduler = new RenderScheduler((_, bmp) => bmp.Return());
        var gate = new ManualResetEventSlim(false);

        var blocker = scheduler.RunAsync(PdfWorkPriority.Interactive, () => gate.Wait());
        var pending = scheduler.RunAsync(PdfWorkPriority.Background, () => 1);

        scheduler.SetDocument(null); // swap drops queued (not-yet-started) ops
        gate.Set();

        await blocker;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Dispose_CancelsPendingOps_AndRejectsNewOnes()
    {
        var scheduler = new RenderScheduler((_, bmp) => bmp.Return());
        var gate = new ManualResetEventSlim(false);
        var blocker = scheduler.RunAsync(PdfWorkPriority.Interactive, () => gate.Wait());
        var pending = scheduler.RunAsync(PdfWorkPriority.Thumbnail, () => 1);

        var disposal = Task.Run(scheduler.Dispose); // Dispose joins the thread; unblock it
        gate.Set();
        await disposal;

        await blocker;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scheduler.RunAsync(PdfWorkPriority.Interactive, () => 2));
    }

    [Fact]
    public async Task DocumentSearch_WorksThroughWorkQueue()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));
        using var scheduler = new RenderScheduler((_, bmp) => bmp.Return());

        var search = new DocumentSearch(doc, "Hello", matchCase: true, wholeWord: false,
            workQueue: scheduler);
        var hits = await search.RunAsync(null, null, CancellationToken.None);

        Assert.Single(hits);
        Assert.Equal(0, hits[0].PageIndex);
    }
}

public class PageTextTests
{
    private static string CorpusPath(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    [Fact]
    public void GetPageText_MatchesExtractText()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        var pageText = doc.GetPageText(0);

        Assert.Equal(doc.ExtractText(0), pageText.Text);
        Assert.Equal(pageText.Text.Length, pageText.Count);
    }

    [Fact]
    public void CharIndexAt_AgreesWithPdfium()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));
        var pageText = doc.GetPageText(0);

        // Sample a grid over the text area; managed hit-testing must agree
        // with FPDFText_GetCharIndexAtPos on every hit (both -1 or same char).
        for (int x = 60; x <= 400; x += 20)
        {
            for (int y = 40; y <= 90; y += 10)
            {
                int native = doc.CharIndexAt(0, x, y, tolerance: 0.5);
                int managed = pageText.CharIndexAt(x, y, tolerance: 0.5);
                if (native >= 0)
                {
                    Assert.Equal(native, managed);
                }
            }
        }
    }

    [Fact]
    public void GetSelection_MatchesPdfiumSelection()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));
        var pageText = doc.GetPageText(0);

        var native = doc.GetSelection(0, 0, 4);
        var managed = pageText.GetSelection(0, 4);

        Assert.Equal(native.Text, managed.Text);
        Assert.Equal(native.Start, managed.Start);
        Assert.Equal(native.Count, managed.Count);
        Assert.NotEmpty(managed.Rects);

        // The managed line-merged rect must cover PDFium's rect closely:
        // compare the bounding boxes of both rect sets.
        static (double X1, double Y1, double X2, double Y2) Bounds(IReadOnlyList<TextRect> rects)
        {
            double x1 = double.MaxValue, y1 = double.MaxValue, x2 = double.MinValue, y2 = double.MinValue;
            foreach (var r in rects)
            {
                x1 = Math.Min(x1, r.X);
                y1 = Math.Min(y1, r.Y);
                x2 = Math.Max(x2, r.X + r.Width);
                y2 = Math.Max(y2, r.Y + r.Height);
            }
            return (x1, y1, x2, y2);
        }

        var nb = Bounds(native.Rects);
        var mb = Bounds(managed.Rects);
        Assert.InRange(Math.Abs(nb.X1 - mb.X1), 0, 3);
        Assert.InRange(Math.Abs(nb.Y1 - mb.Y1), 0, 3);
        Assert.InRange(Math.Abs(nb.X2 - mb.X2), 0, 3);
        Assert.InRange(Math.Abs(nb.Y2 - mb.Y2), 0, 3);
    }

    [Fact]
    public void GetSelection_ReversedAnchorAndFocus_IsSameRange()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));
        var pageText = doc.GetPageText(0);

        Assert.Equal(pageText.GetSelection(0, 4).Text, pageText.GetSelection(4, 0).Text);
    }

    [Fact]
    public void MultiLineSelection_ProducesOneRectPerLine()
    {
        using var doc = PdfDocument.Open(CorpusPath("book-1000.pdf"));
        var pageText = doc.GetPageText(0);

        // Select everything on the page: the corpus draws 21 separate lines.
        var selection = pageText.GetSelection(0, pageText.Count - 1);

        Assert.True(selection.Rects.Count > 1, "expected multiple line rects");
        // Lines must not overlap vertically (each is a separate rect).
        var sorted = selection.Rects.OrderBy(r => r.Y).ToList();
        for (int i = 1; i < sorted.Count; i++)
        {
            Assert.True(sorted[i].Y >= sorted[i - 1].Y + sorted[i - 1].Height * 0.5,
                $"line rect {i} overlaps its predecessor");
        }
    }

    [Fact]
    public void EmptyPage_ProducesEmptyPageText()
    {
        var empty = PageText.Empty(3);

        Assert.Equal(3, empty.PageIndex);
        Assert.Equal(-1, empty.CharIndexAt(10, 10));
        Assert.Empty(empty.GetSelection(0, 5).Text);
    }
}
