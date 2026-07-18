using Rune.Engine;

namespace Rune.Tests;

public class AnnotationTests
{
    private static string CorpusPath(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    private static string TempPdf() => Path.Combine(Path.GetTempPath(), $"rune-annot-{Guid.NewGuid():N}.pdf");

    [Fact]
    public void AddHighlight_SaveAs_Reopen_AnnotationPersists()
    {
        string saved = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(CorpusPath("hello.pdf")))
            {
                Assert.False(doc.IsDirty);
                // Rects over the "Hello from Rune!" line (top-left page points).
                doc.AddMarkup(0, MarkupKind.Highlight, [new TextRect(70, 50, 240, 30)], 255, 210, 0, 102);
                Assert.True(doc.IsDirty);
                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);
            var annots = reopened.GetAnnotations(0);
            var highlight = Assert.Single(annots);
            Assert.Equal((int)MarkupKind.Highlight, highlight.Subtype);
            // Rect round-trips to roughly where we put it (top-left origin).
            Assert.InRange(highlight.X, 60, 80);
            Assert.InRange(highlight.Y, 40, 60);
        }
        finally
        {
            File.Delete(saved);
        }
    }

    [Fact]
    public void AddNote_ContentsRoundTrip()
    {
        string saved = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(CorpusPath("hello.pdf")))
            {
                doc.AddNote(0, 300, 400, "Remember to cite this passage.");
                // In-process read-back isolates set-failure from save-loss.
                Assert.Equal("Remember to cite this passage.", doc.GetAnnotations(0)[0].Contents);
                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);
            var note = Assert.Single(reopened.GetAnnotations(0));
            Assert.True(note.IsNote);
            Assert.Equal("Remember to cite this passage.", note.Contents);
        }
        finally
        {
            File.Delete(saved);
        }
    }

    [Fact]
    public void RemoveAnnotation_Works()
    {
        string saved = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(CorpusPath("hello.pdf")))
            {
                doc.AddMarkup(0, MarkupKind.Underline, [new TextRect(70, 50, 100, 20)], 200, 0, 0, 255);
                doc.AddNote(0, 300, 300, "note");
                doc.SaveAs(saved);
            }

            string resaved = TempPdf();
            try
            {
                using (var doc = PdfDocument.Open(saved))
                {
                    Assert.Equal(2, doc.GetAnnotations(0).Count);
                    Assert.True(doc.RemoveAnnotation(0, 0));
                    Assert.Single(doc.GetAnnotations(0));
                    doc.SaveAs(resaved);
                }

                using var final = PdfDocument.Open(resaved);
                Assert.Single(final.GetAnnotations(0));
            }
            finally
            {
                File.Delete(resaved);
            }
        }
        finally
        {
            File.Delete(saved);
        }
    }

    [Fact]
    public void AddInk_SaveAs_Reopen_InkAnnotationPersists()
    {
        string saved = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(CorpusPath("hello.pdf")))
            {
                // A short diagonal stroke in top-left page points.
                var stroke = new (double X, double Y)[] { (100, 100), (150, 130), (200, 110), (260, 160) };
                doc.AddInk(0, stroke, 226, 34, 34, 255, 2.5f);
                Assert.True(doc.IsDirty);
                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);
            var ink = Assert.Single(reopened.GetAnnotations(0));
            Assert.Equal(15, ink.Subtype); // FPDF_ANNOT_SUBTYPE_INK
        }
        finally
        {
            File.Delete(saved);
        }
    }

    [Fact]
    public void AddInk_ShortStroke_IsIgnored()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));
        doc.AddInk(0, [(10.0, 10.0)], 0, 0, 0, 255, 2f); // single point → no annotation
        Assert.False(doc.IsDirty);
        Assert.Empty(doc.GetAnnotations(0));
    }

    [Fact]
    public void AddInk_ChangesRenderedPixels()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));
        var before = doc.RenderPage(0, 1.0f);
        int beforeInk = CountNonWhite(before);

        var stroke = new (double X, double Y)[] { (60, 400), (200, 405), (340, 402), (480, 408) };
        doc.AddInk(0, stroke, 226, 34, 34, 255, 4f);
        var after = doc.RenderPage(0, 1.0f);
        int afterInk = CountNonWhite(after);

        Assert.True(afterInk > beforeInk + 500, $"expected ink pixels: before={beforeInk}, after={afterInk}");
    }

    private static int CountNonWhite(PageBitmap bmp)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                int i = y * bmp.Stride + x * 4;
                if (bmp.Pixels[i] != 0xFF || bmp.Pixels[i + 1] != 0xFF || bmp.Pixels[i + 2] != 0xFF)
                {
                    n++;
                }
            }
        }
        return n;
    }

    [Fact]
    public void Highlight_ActuallyChangesRenderedPixels()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));
        var before = doc.RenderPage(0, 1.0f);
        int beforeYellowish = CountTinted(before);

        doc.AddMarkup(0, MarkupKind.Highlight, [new TextRect(70, 50, 240, 30)], 255, 210, 0, 102);
        var after = doc.RenderPage(0, 1.0f);
        int afterYellowish = CountTinted(after);

        Assert.True(afterYellowish > beforeYellowish + 1000,
            $"expected highlight tint in render: before={beforeYellowish}, after={afterYellowish}");
    }

    private static int CountTinted(PageBitmap bmp)
    {
        int tinted = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                int i = y * bmp.Stride + x * 4;
                byte b = bmp.Pixels[i], g = bmp.Pixels[i + 1], r = bmp.Pixels[i + 2];
                if (r > 200 && g > 150 && b < 200 && !(r > 250 && g > 250 && b > 250))
                {
                    tinted++;
                }
            }
        }
        return tinted;
    }
}
