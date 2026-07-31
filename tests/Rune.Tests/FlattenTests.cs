using Rune.Engine;

namespace Rune.Tests;

/// <summary>
/// Flatten is irreversible, so its failure mode is silent data loss rather than
/// an exception: FPDFPage_Flatten can drop content on unusual resource
/// dictionaries, and Rune never calls FPDFAnnot_SetAP — its ink and markup rely
/// on PDFium generating appearances. Every test here therefore checks the
/// *pixels* survive, not just that the call returned success.
/// </summary>
public class FlattenTests
{
    private static string TempPdf() => Path.Combine(Path.GetTempPath(), $"rune-flat-{Guid.NewGuid():N}.pdf");

    [Fact]
    public void BarePage_ReportsNothingToDo()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        Assert.Equal(FlattenResult.NothingToDo, doc.FlattenPage(0));
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void HasFlattenableContent_IsFalseForAPlainDocument()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        Assert.False(doc.HasFlattenableContent());
    }

    [Fact]
    public void Ink_SurvivesFlattenAsPageContent()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        doc.AddInk(0, [(60, 400), (200, 405), (340, 402), (480, 408)], 226, 34, 34, 255, 4f);
        Assert.True(doc.HasFlattenableContent());
        int beforePixels = PixelAssert.CountNonWhite(doc.RenderPage(0, 1.0f));

        Assert.Equal(FlattenResult.Flattened, doc.FlattenPage(0));

        // The annotation object is gone...
        Assert.Empty(doc.GetAnnotations(0));
        // ...but the marks it made must still be on the page. This is the
        // assertion that catches PDFium dropping content during flatten.
        int afterPixels = PixelAssert.CountNonWhite(doc.RenderPage(0, 1.0f));
        Assert.True(afterPixels > beforePixels * 0.9,
            $"flatten lost page content: {beforePixels} -> {afterPixels} non-white pixels");
    }

    [Fact]
    public void Highlight_SurvivesFlatten()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        doc.AddMarkup(0, MarkupKind.Highlight, [new TextRect(70, 50, 240, 30)], 255, 210, 0, 102);
        int beforeTint = PixelAssert.CountTinted(doc.RenderPage(0, 1.0f));
        Assert.True(beforeTint > 1000);

        Assert.Equal(FlattenResult.Flattened, doc.FlattenPage(0));

        Assert.Empty(doc.GetAnnotations(0));
        int afterTint = PixelAssert.CountTinted(doc.RenderPage(0, 1.0f));
        Assert.True(afterTint > beforeTint * 0.9,
            $"highlight did not survive flatten: {beforeTint} -> {afterTint} tinted pixels");
    }

    [Fact]
    public void Flatten_SaveAs_Reopen_KeepsBakedContent()
    {
        string saved = TempPdf();
        try
        {
            int flattenedPixels;
            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf")))
            {
                doc.AddInk(0, [(60, 400), (200, 405), (340, 402), (480, 408)], 226, 34, 34, 255, 4f);
                doc.FlattenPage(0);
                flattenedPixels = PixelAssert.CountNonWhite(doc.RenderPage(0, 1.0f));
                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);
            Assert.Empty(reopened.GetAnnotations(0));
            int reopenedPixels = PixelAssert.CountNonWhite(reopened.RenderPage(0, 1.0f));
            Assert.True(reopenedPixels > flattenedPixels * 0.9,
                $"baked content lost on save/reopen: {flattenedPixels} -> {reopenedPixels}");
        }
        finally
        {
            if (File.Exists(saved)) { File.Delete(saved); }
        }
    }

    [Fact]
    public void FilledForm_Flattens_ToPlainPageContent()
    {
        string saved = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf")))
            {
                doc.FormClick(0, 325, 80);
                foreach (char c in "Flattened")
                {
                    doc.FormChar(0, c);
                }
                doc.FormKillFocus();

                Assert.Equal(FlattenResult.Flattened, doc.FlattenPage(0));
                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);

            // No widgets left to fill, and the text is now page content.
            Assert.Empty(reopened.GetFormFields(0));
            Assert.DoesNotContain(reopened.GetAnnotations(0), a => a.Subtype == 20);
            Assert.Contains("Flattened", reopened.GetPageText(0).Text);
        }
        finally
        {
            if (File.Exists(saved)) { File.Delete(saved); }
        }
    }

    [Fact]
    public void FlattenAllPages_ReportsProgressForEveryPage()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.AddInk(0, [(60, 400), (200, 405), (340, 402)], 0, 0, 0, 255, 3f);

        var seen = new List<int>();
        int changed = doc.FlattenAllPages(onProgress: (done, _) => seen.Add(done));

        Assert.Equal(1, changed);              // only page 0 had anything
        Assert.Equal([1, 2], seen);            // but both pages are visited
    }
}
