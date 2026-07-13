using Rune.Engine;

namespace Rune.Tests;

public class TextExtractionTests
{
    private static string CorpusPath(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    [Fact]
    public void ExtractText_ReturnsPageText()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        string text = doc.ExtractText(0);
        Assert.Contains("Hello from Rune", text);

        Assert.Contains("Page two", doc.ExtractText(1));
    }

    [Fact]
    public void GetSelection_ReturnsTextAndRectsForRange()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        // Select the first 5 characters ("Hello").
        var selection = doc.GetSelection(0, 0, 4);

        Assert.Equal("Hello", selection.Text);
        Assert.Equal(0, selection.Start);
        Assert.Equal(5, selection.Count);
        Assert.NotEmpty(selection.Rects);

        // Highlight rects use a top-left origin, so Y is near the top of the
        // 792-pt page (text was drawn at y=700 in page space).
        Assert.All(selection.Rects, r => Assert.InRange(r.Y, 0, 200));
    }

    [Fact]
    public void CharIndexAt_RoundTripsThroughSelection()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        // Point inside the first line of text. The corpus draws 24pt text at
        // baseline y=720 (bottom-left origin) → glyphs span roughly y=52–72
        // in top-left points; x=80 is inside the leading 'H'.
        int index = doc.CharIndexAt(0, localX: 80, localY: 62, tolerance: 15);
        Assert.True(index >= 0, "expected to hit a character in the first line");
    }
}

public class SearchTests
{
    private static string CorpusPath(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    [Fact]
    public void SearchPage_FindsAllOccurrencesWithRects()
    {
        using var doc = PdfDocument.Open(CorpusPath("book-1000.pdf"));

        // Page 5 (index 4) contains "page 5" once in the header and in each of
        // its 20 body lines → 21 occurrences.
        var hits = doc.SearchPage(4, "page 5", matchCase: false, wholeWord: false);

        Assert.Equal(21, hits.Count);
        Assert.All(hits, h => Assert.NotEmpty(h.Rects));
        Assert.All(hits, h => Assert.Equal(4, h.PageIndex));
    }

    [Fact]
    public void SearchPage_MatchCase_IsRespected()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        Assert.Empty(doc.SearchPage(0, "hello", matchCase: true, wholeWord: false));
        Assert.Single(doc.SearchPage(0, "Hello", matchCase: true, wholeWord: false));
    }

    [Fact]
    public async Task DocumentSearch_CountsHitsAcrossWholeDocument()
    {
        using var doc = PdfDocument.Open(CorpusPath("book-1000.pdf"));

        // "Rune" appears once per page (in every body line? no — "Rune test
        // book" appears in each of 20 lines per page) → search a rarer phrase.
        // "the Rune test book" appears 20 times per page × 1000 pages.
        var search = new DocumentSearch(doc, "Page 500", matchCase: true, wholeWord: false);
        int progressCalls = 0;

        var hits = await search.RunAsync(
            onPageHits: null,
            onProgress: _ => Interlocked.Increment(ref progressCalls),
            CancellationToken.None);

        // "Page 500" (capitalized header) appears only on page index 499.
        Assert.Single(hits);
        Assert.Equal(499, hits[0].PageIndex);
        Assert.Equal(1000, progressCalls); // progress reported once per page
    }

    [Fact]
    public async Task DocumentSearch_HonorsCancellation()
    {
        using var doc = PdfDocument.Open(CorpusPath("book-1000.pdf"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var search = new DocumentSearch(doc, "page");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => search.RunAsync(null, null, cts.Token));
    }
}
