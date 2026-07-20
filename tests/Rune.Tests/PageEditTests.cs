using Rune.Engine;
using Rune.PdfiumInterop;

namespace Rune.Tests;

public class PageEditTests
{
    private static string CorpusPath(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    [Fact]
    public void DeletePages_RemovesAndReindexes()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf")); // 2 pages

        doc.DeletePages([0]);

        Assert.Equal(1, doc.PageCount);
        Assert.Contains("Page two", doc.ExtractText(0));
        Assert.True(doc.IsDirty);
    }

    [Fact]
    public void DeletePages_RefusesToDeleteEveryPage()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        Assert.Throws<InvalidOperationException>(() => doc.DeletePages([0, 1]));
        Assert.Equal(2, doc.PageCount);
    }

    [Fact]
    public void DeletePages_UpdatesPageMetrics()
    {
        using var doc = PdfDocument.Open(CorpusPath("book-1000.pdf"));

        doc.DeletePages([0, 1, 2]);

        Assert.Equal(997, doc.PageCount);
        // GetPageSize must not throw for the new last page and must throw past it.
        _ = doc.GetPageSize(996);
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.GetPageSize(997));
    }

    [Fact]
    public void MovePages_BlockLandsAtDestIndex()
    {
        using var doc = PdfDocument.Open(CorpusPath("book-1000.pdf"));

        // Move pages 0 and 1 so the block starts at final index 2.
        doc.MovePages([0, 1], 2);

        // Final order: 2, 3, 0, 1, 4, ... (per FPDF_MovePages / MovePermutation)
        Assert.Contains("Page 3", doc.ExtractText(0));
        Assert.Contains("Page 4", doc.ExtractText(1));
        Assert.Contains("Page 1", doc.ExtractText(2));
        Assert.Contains("Page 2", doc.ExtractText(3));
        Assert.Contains("Page 5", doc.ExtractText(4));
        Assert.Equal(1000, doc.PageCount);
    }

    [Fact]
    public void MovePages_MatchesBookmarkRemapModel()
    {
        using var doc = PdfDocument.Open(CorpusPath("book-1000.pdf"));
        int[] moved = [2, 5];
        const int dest = 0;

        var map = BookmarkRemap.MovePermutation(doc.PageCount, moved, dest);
        doc.MovePages(moved, dest);

        // Page originally at index 5 ("Page 6") must now be at map[5].
        Assert.Contains("Page 6", doc.ExtractText(map[5]));
        Assert.Contains("Page 3", doc.ExtractText(map[2]));
        Assert.Contains("Page 1", doc.ExtractText(map[0]));
    }

    [Fact]
    public void ExportPages_RoundTripsThroughInsert()
    {
        using var source = PdfDocument.Open(CorpusPath("hello.pdf"));
        using var target = PdfDocument.Open(CorpusPath("book-1000.pdf"));

        var bytes = source.ExportPages([1]); // "Page two."
        Assert.NotEmpty(bytes);

        int inserted = target.InsertPages(bytes, 0);

        Assert.Equal(1, inserted);
        Assert.Equal(1001, target.PageCount);
        Assert.Contains("Page two", target.ExtractText(0));
        Assert.Contains("Page 1", target.ExtractText(1)); // original content shifted down
    }

    [Fact]
    public void InsertPagesFromFile_InsertsAllPagesAtIndex()
    {
        using var doc = PdfDocument.Open(CorpusPath("book-1000.pdf"));

        int inserted = doc.InsertPagesFromFile(CorpusPath("hello.pdf"), 5);

        Assert.Equal(2, inserted);
        Assert.Equal(1002, doc.PageCount);
        Assert.Contains("Hello from Rune", doc.ExtractText(5));
        Assert.Contains("Page two", doc.ExtractText(6));
        Assert.Contains("Page 6", doc.ExtractText(7));
    }

    [Fact]
    public void ExportInsert_PreservesAnnotations()
    {
        using var source = PdfDocument.Open(CorpusPath("hello.pdf"));
        source.AddMarkup(0, MarkupKind.Highlight, [new TextRect(50, 50, 100, 20)], 255, 210, 0, 102);

        var bytes = source.ExportPages([0]);

        using var target = PdfDocument.Open(CorpusPath("book-1000.pdf"));
        target.InsertPages(bytes, 0);

        Assert.Contains(target.GetAnnotations(0),
            a => a.Subtype == (int)MarkupKind.Highlight);
    }

    [Fact]
    public void ExportedBytes_SurviveSourceDisposal()
    {
        byte[] bytes;
        using (var source = PdfDocument.Open(CorpusPath("hello.pdf")))
        {
            bytes = source.ExportPages([0, 1]);
        } // source closed — simulates cutting from a tab that then closes

        using var target = PdfDocument.Open(CorpusPath("linked.pdf"));
        int before = target.PageCount;
        int inserted = target.InsertPages(bytes, target.PageCount);

        Assert.Equal(2, inserted);
        Assert.Equal(before + 2, target.PageCount);
        Assert.Contains("Hello from Rune", target.ExtractText(before));
    }

    [Fact]
    public void PageOps_OutOfRangeIndicesAreIgnored()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        doc.DeletePages([5, -1]); // nothing valid to delete
        Assert.Equal(2, doc.PageCount);

        var bytes = doc.ExportPages([-3, 99]);
        Assert.Empty(bytes);
    }

    [Fact]
    public void InsertPages_EmptyBytes_IsANoOp()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        Assert.Equal(0, doc.InsertPages([], 0));
        Assert.Equal(2, doc.PageCount);
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void InsertPages_GarbageBytes_ThrowsPdfiumException()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        Assert.Throws<PdfiumException>(() => doc.InsertPages([1, 2, 3, 4, 5, 6, 7, 8], 0));
        Assert.Equal(2, doc.PageCount);
    }
}
