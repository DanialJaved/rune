using Rune.Engine;

namespace Rune.Tests;

public class OutlineTests
{
    private static string CorpusPath(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    [Fact]
    public void GetOutline_ReadsTitlesAndTargetPages()
    {
        using var doc = PdfDocument.Open(CorpusPath("linked.pdf"));

        var outline = doc.GetOutline();

        Assert.Equal(2, outline.Count);
        Assert.Equal("Chapter 1", outline[0].Title);
        Assert.Equal(0, outline[0].PageIndex);
        Assert.Equal("Chapter 2", outline[1].Title);
        Assert.Equal(2, outline[1].PageIndex);
    }

    [Fact]
    public void GetOutline_NoOutline_ReturnsEmpty()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));
        Assert.Empty(doc.GetOutline());
    }
}

public class LinkTests
{
    private static string CorpusPath(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    [Fact]
    public void GetLinks_FindsInternalAndUriLinks()
    {
        using var doc = PdfDocument.Open(CorpusPath("linked.pdf"));

        var links = doc.GetLinks(0);

        Assert.Equal(2, links.Count);

        var internalLink = Assert.Single(links, l => l.IsInternal);
        Assert.Equal(2, internalLink.TargetPageIndex);

        var uriLink = Assert.Single(links, l => !l.IsInternal);
        Assert.Equal("https://example.com/", uriLink.Uri);
    }

    [Fact]
    public void GetLinks_RectsUseTopLeftOrigin()
    {
        using var doc = PdfDocument.Open(CorpusPath("linked.pdf"));

        // The internal link's page-space rect is [72 680 300 720] on a 792-tall
        // page, i.e. near the TOP. In top-left origin its Y must be small.
        var internalLink = doc.GetLinks(0).Single(l => l.IsInternal);
        Assert.Equal(72, internalLink.X, precision: 0);
        Assert.Equal(792 - 720, internalLink.Y, precision: 0);
        Assert.Equal(228, internalLink.Width, precision: 0);  // 300 - 72
        Assert.Equal(40, internalLink.Height, precision: 0);  // 720 - 680
    }

    [Fact]
    public void GetLinks_PageWithoutLinks_ReturnsEmpty()
    {
        using var doc = PdfDocument.Open(CorpusPath("linked.pdf"));
        Assert.Empty(doc.GetLinks(1));
    }
}
