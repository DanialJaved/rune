using Rune.Engine;

namespace Rune.Tests;

/// <summary>
/// <see cref="PdfDocument.AddTextBox"/>: where the text lands, how big its box
/// is, and whether undo can rebuild it.
///
/// Nothing here carries font metrics, and that is the design: the box comes from
/// measuring the objects PDFium actually built, so these assertions check the
/// renderer's own answer rather than a table that could drift from it.
/// </summary>
public class TextBoxTests
{
    private static string TempPdf() => Path.Combine(Path.GetTempPath(), $"rune-tb-{Guid.NewGuid():N}.pdf");

    private static TextBoxContent Text(string text, double size = 24) =>
        new(text, PdfStandardFont.Helvetica, size, 0, 0, 0);

    [Fact]
    public void Lines_SplitOnEveryFlavourOfLineBreak()
    {
        Assert.Equal(["a", "b", "c"], Text("a\nb\rc").Lines);
        Assert.Equal(["a", "b"], Text("a\r\nb").Lines);
        Assert.Equal(["one"], Text("one").Lines);
        // A trailing break really is an empty last line, and must stay one so
        // the block's height matches what the editor showed.
        Assert.Equal(["a", ""], Text("a\n").Lines);
    }

    [Fact]
    public void EmptyText_WritesNothingAndLeavesTheDocumentClean()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        Assert.Null(doc.AddTextBox(0, 50, 50, Text("")));
        Assert.Null(doc.AddTextBox(0, 50, 50, Text("\n\n")));
        Assert.Null(doc.AddTextBox(0, 50, 50, Text("x", size: 0)));
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void Box_IsMeasuredFromTheText_NotGuessedFromItsLength()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        var narrow = doc.AddTextBox(0, 40, 100, Text("iii"))!.Rect;
        var wide = doc.AddTextBox(0, 40, 200, Text("WWW"))!.Rect;

        double narrowWidth = narrow.R - narrow.L;
        double wideWidth = wide.R - wide.L;

        // Same character count, very different widths: proof the box follows the
        // glyphs. A length-times-size guess would make these identical.
        Assert.True(wideWidth > narrowWidth * 2,
            $"'WWW' measured {wideWidth:F1}pt against 'iii' at {narrowWidth:F1}pt");
    }

    [Fact]
    public void Box_GrowsWithEachLine()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        var one = doc.AddTextBox(0, 40, 100, Text("Ay"))!.Rect;
        var three = doc.AddTextBox(0, 40, 300, Text("Ay\nAy\nAy"))!.Rect;

        double oneHigh = one.T - one.B;
        double threeHigh = three.T - three.B;
        double leading = 24 * TextBoxContent.LineHeight;

        // Two extra baselines, so two extra leadings, give or take the glyphs'
        // own extent which is already counted once.
        Assert.InRange(threeHigh - oneHigh, leading * 2 - 2, leading * 2 + 2);
    }

    [Fact]
    public void BlankLine_StillTakesUpItsRoom()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        var tight = doc.AddTextBox(0, 40, 100, Text("a\nb"))!.Rect;
        var spaced = doc.AddTextBox(0, 40, 400, Text("a\n\nb"))!.Rect;

        Assert.True((spaced.T - spaced.B) > (tight.T - tight.B),
            "a blank line drew nothing and also took up no room");
    }

    [Fact]
    public void TopLeft_LandsWhereItWasAsked()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        var (_, pageHeight) = doc.GetPageSize(0);

        var high = doc.AddTextBox(0, 60, 100, Text("Ay"))!.Rect;
        var low = doc.AddTextBox(0, 60, 260, Text("Ay"))!.Rect;

        // Page space counts up from the bottom, so 160pt further down the page
        // is 160pt lower a top edge. Exact, because the block is measured and
        // then moved rather than positioned from an assumed ascent.
        Assert.InRange(high.T - low.T, 159, 161);
        Assert.InRange(high.L, 59, 61);
        Assert.InRange(pageHeight - high.T, 99, 101);
    }

    [Fact]
    public void Colour_ReachesThePixels()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        doc.AddTextBox(0, 60, 300, new TextBoxContent("Wwwwww", PdfStandardFont.Helvetica, 36, 210, 0, 0));

        Assert.True(CountRed(doc.RenderPage(0, 1.0f)) > 0, "the fill colour never reached the page");
    }

    [Fact]
    public void Undo_RebuildsTheTextFromItsWords()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        int blank = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));
        var spec = doc.AddTextBox(0, 60, 300, Text("Rebuild me\nboth lines"))!;
        int written = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));
        Assert.True(written > blank);

        // Erase it the way the eraser would, then put it back the way undo does.
        var annotations = doc.GetAnnotations(0);
        Assert.True(doc.RemoveAnnotation(0, annotations[^1].Index));
        Assert.Equal(blank, PixelAssert.CountDark(doc.RenderPage(0, 1.0f)));

        doc.AddAnnotationFromSpec(spec);

        // Re-rendered from the words rather than replayed from a raster, so it
        // has to come back pixel-identical, not merely similar.
        Assert.Equal(written, PixelAssert.CountDark(doc.RenderPage(0, 1.0f)));
    }

    [Fact]
    public void Spec_CarriesTheWordsAndTheStyle()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        var spec = doc.AddTextBox(0, 60, 300,
            new TextBoxContent("Styled", PdfStandardFont.Times, 18, 10, 20, 30, Bold: true))!;

        Assert.NotNull(spec.Text);
        Assert.Equal("Styled", spec.Text!.Text);
        Assert.Equal("Times-Bold", spec.Text.PostScriptName);
        Assert.Equal(18, spec.Text.FontSize);
        // /Contents too, so other readers and Rune's own hit-testing can see it.
        Assert.Equal("Styled", spec.Contents);
    }

    [Fact]
    public void MultiLine_SurvivesSaveAndReopen()
    {
        string saved = TempPdf();
        try
        {
            int expected;
            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf")))
            {
                doc.AddTextBox(0, 60, 240, Text("First line\nSecond line\nThird"));
                expected = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));
                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);
            Assert.Equal(expected, PixelAssert.CountDark(reopened.RenderPage(0, 1.0f)));
        }
        finally
        {
            if (File.Exists(saved)) { File.Delete(saved); }
        }
    }

    private static int CountRed(PageBitmap bmp)
    {
        int red = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                var (b, g, r) = PixelAssert.Pixel(bmp, x, y);
                if (r > 140 && g < 90 && b < 90) { red++; }
            }
        }
        return red;
    }
}
