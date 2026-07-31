using Rune.Engine;

namespace Rune.Tests;

/// <summary>
/// The page cache exists so PDFium's form-fill API can rely on a stable
/// FPDF_PAGE across keystrokes. Its failure modes are all use-after-free or
/// leak, neither of which shows up as a wrong answer — so assert the lifecycle
/// directly rather than trusting downstream behaviour.
/// </summary>
public class PageCacheTests
{
    private static string TempPdf() => Path.Combine(Path.GetTempPath(), $"rune-cache-{Guid.NewGuid():N}.pdf");

    [Fact]
    public void RepeatedUse_KeepsOnePageOpen()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        doc.GetPageText(0);
        doc.GetPageText(0);
        doc.GetLinks(0);

        Assert.Equal(1, doc.CachedPageCount);
    }

    [Fact]
    public void ManyPages_StayWithinCapacity()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("book-1000.pdf"));

        for (int i = 0; i < 40; i++)
        {
            doc.GetPageText(i);
        }

        // Capacity is 12; the exact number is an implementation detail, but an
        // unbounded cache would sit at 40 and eventually exhaust handles.
        Assert.InRange(doc.CachedPageCount, 1, 12);
    }

    [Fact]
    public void PinnedPage_SurvivesEvictionPressure()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("book-1000.pdf"));

        doc.GetPageText(0);
        doc.PinPage(0);
        for (int i = 1; i < 40; i++)
        {
            doc.GetPageText(i);
        }

        // Residency is the whole point — asserting on the text would pass even
        // if page 0 had been evicted and silently reloaded, which is exactly
        // the behaviour that breaks form editing.
        Assert.True(doc.IsPageCached(0), "pinned page was evicted");
        Assert.InRange(doc.CachedPageCount, 1, 13);
    }

    [Fact]
    public void UnpinnedColdPage_IsEvicted()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("book-1000.pdf"));

        doc.GetPageText(0);
        for (int i = 1; i < 40; i++)
        {
            doc.GetPageText(i);
        }

        // The negative case: without a pin, the coldest page must go, or the
        // "pinned survives" test above proves nothing.
        Assert.False(doc.IsPageCached(0), "cache is not evicting at all");
    }

    [Fact]
    public void DeletePages_ReleasesEveryHandle()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("book-1000.pdf"));

        for (int i = 0; i < 5; i++)
        {
            doc.GetPageText(i);
        }
        Assert.True(doc.CachedPageCount > 0);

        doc.DeletePages([0]);

        // Stale handles must not outlive the pages they point at.
        Assert.Equal(0, doc.CachedPageCount);
        Assert.Equal(999, doc.PageCount);
        Assert.Contains("Page 2", doc.GetPageText(0).Text); // old page 1 is now page 0
    }

    [Fact]
    public void InsertPages_ReleasesEveryHandle()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.GetPageText(0);

        doc.InsertPagesFromFile(PixelAssert.CorpusPath("slides.pdf"), 0);

        Assert.Equal(0, doc.CachedPageCount);
        Assert.Equal(5, doc.PageCount);
    }

    [Fact]
    public void SaveAs_ReleasesPages_AndRoundTrips()
    {
        string saved = TempPdf();
        try
        {
            using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
            doc.GetPageText(0);
            doc.GetPageText(1);

            doc.SaveAs(saved);
            Assert.Equal(0, doc.CachedPageCount);

            using var reopened = PdfDocument.Open(saved);
            Assert.Equal(2, reopened.PageCount);
        }
        finally
        {
            if (File.Exists(saved)) { File.Delete(saved); }
        }
    }

    [Fact]
    public void Dispose_WithPagesOpen_DoesNotThrow()
    {
        var doc = PdfDocument.Open(PixelAssert.CorpusPath("book-1000.pdf"));
        for (int i = 0; i < 8; i++)
        {
            doc.GetPageText(i);
        }

        // Pages must be closed before the document that owns them; getting the
        // order wrong is a double-free rather than an exception, so this is
        // really a crash guard.
        doc.Dispose();
        doc.Dispose();
    }

    [Fact]
    public void CachedTextPage_StaysValidAcrossCalls()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        // The text page is loaded once and kept for the page's cache lifetime.
        // If it were freed while the page stayed cached, the second read would
        // return empty (or crash).
        string first = doc.GetPageText(0).Text;
        string second = doc.GetPageText(0).Text;

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void AnnotationEdit_ThenRender_SeesTheSamePage()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        // Annotations and rendering now share one handle. If they didn't, the
        // render could be taken from a second handle that never saw the ink.
        int before = PixelAssert.CountNonWhite(doc.RenderPage(0, 1.0f));
        doc.AddInk(0, [(60, 400), (200, 405), (340, 402), (480, 408)], 226, 34, 34, 255, 4f);
        int after = PixelAssert.CountNonWhite(doc.RenderPage(0, 1.0f));

        Assert.True(after > before + 500, $"ink not visible through the shared page handle: {before} -> {after}");
    }
}
