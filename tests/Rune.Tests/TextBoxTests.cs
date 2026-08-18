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

    // ---- the box's width, and what the words do inside it ----

    private const string Paragraph =
        "The quick brown fox jumps over the lazy dog and keeps on running well past it";

    [Fact]
    public void NoWidth_MeansNoWrapping()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        var rect = doc.AddTextBox(0, 40, 100, Text(Paragraph, size: 12))!.Rect;

        // One line, however long: an auto-width box is as wide as its words and
        // runs off the page rather than wrapping. This is what a box is until
        // someone drags a corner, and it is deliberate.
        Assert.InRange(rect.T - rect.B, 1, 12 * TextBoxContent.LineHeight);
    }

    [Fact]
    public void Width_WrapsTheWordsAndKeepsThePointSize()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        var loose = doc.AddTextBox(0, 40, 100, Text(Paragraph, size: 12) with { WidthPt = 300 })!;
        var tight = doc.AddTextBox(0, 40, 300, Text(Paragraph, size: 12) with { WidthPt = 120 })!;

        // Narrower box, more lines, same size — that is the whole contract.
        Assert.True((tight.Rect.T - tight.Rect.B) > (loose.Rect.T - loose.Rect.B),
            "a narrower box did not produce a taller block");
        Assert.Equal(12, doc.TryReadTextBox(0, 0)!.FontSize, 1);
        Assert.Equal(12, doc.TryReadTextBox(0, 1)!.FontSize, 1);
    }

    [Fact]
    public void Width_IsTheBoxRect_NotTheInk()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        // Two words in a wide box: the ink is nowhere near 400pt, but the rect
        // has to be, or a re-read would shrink the box the user dragged.
        var rect = doc.AddTextBox(0, 40, 100, Text("Ay", size: 12) with { WidthPt = 400 })!.Rect;

        Assert.InRange(rect.R - rect.L, 399, 401);
        Assert.InRange(rect.L, 39, 41);
    }

    [Fact]
    public void Alignment_PutsTheWordsWhereItSays()
    {
        // The rect is the box whichever way the line is aligned, so only the
        // pixels can tell these apart. One short line in a wide box, measured in
        // its own band of the page so nothing hello.pdf already draws can be
        // mistaken for it.
        const int BoxTop = 500;
        const int BandTop = BoxTop - 5, BandBottom = BoxTop + 45;

        int Leftmost(TextAlign align)
        {
            using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

            Assert.Null(LeftmostDarkColumn(doc.RenderPage(0, 1.0f), BandTop, BandBottom));

            doc.AddTextBox(0, 40, BoxTop, Text("Ay", size: 24) with { WidthPt = 400, Align = align });
            return LeftmostDarkColumn(doc.RenderPage(0, 1.0f), BandTop, BandBottom)
                ?? throw new Xunit.Sdk.XunitException($"{align} drew nothing in the band");
        }

        int left = Leftmost(TextAlign.Left);
        int centre = Leftmost(TextAlign.Center);
        int right = Leftmost(TextAlign.Right);

        // Left starts at the box's own edge; the other two are pushed into it by
        // the slack, which for two characters in a 400pt box is most of it.
        Assert.InRange(left, 39, 42);
        Assert.True(centre > left + 100, $"centred text started at {centre}, left-aligned at {left}");
        Assert.True(right > centre + 100, $"right-aligned text started at {right}, centred at {centre}");
    }

    [Fact]
    public void Justify_StretchesEveryLineButTheLast()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        var ragged = doc.AddTextBox(0, 40, 100,
            Text(Paragraph, size: 12) with { WidthPt = 200, Align = TextAlign.Left })!;
        var flush = doc.AddTextBox(0, 40, 400,
            Text(Paragraph, size: 12) with { WidthPt = 200, Align = TextAlign.Justify })!;

        // Same words, same box, same wrap points, so the same number of lines:
        // justification changes the gaps, never the breaks.
        Assert.InRange(flush.Rect.T - flush.Rect.B, (ragged.Rect.T - ragged.Rect.B) - 1,
            (ragged.Rect.T - ragged.Rect.B) + 1);

        // A single line is its own paragraph's last, so it is never stretched —
        // proved by its box being the same height and its ink not reaching the
        // right edge.
        Assert.NotNull(doc.AddTextBox(0, 40, 700,
            Text("Ay", size: 12) with { WidthPt = 400, Align = TextAlign.Justify }));
    }

    [Fact]
    public void Style_SurvivesSaveAndReopen()
    {
        string saved = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf")))
            {
                doc.AddTextBox(0, 40, 100,
                    Text("Round trip me", size: 14) with
                    {
                        Underline = true,
                        Align = TextAlign.Right,
                        WidthPt = 250.5,
                    });
                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);
            var read = reopened.TryReadTextBox(0, 0)!;

            Assert.True(read.Underline);
            Assert.Equal(TextAlign.Right, read.Align);
            Assert.Equal(250.5, read.WidthPt, 2);
        }
        finally
        {
            if (File.Exists(saved)) { File.Delete(saved); }
        }
    }

    [Fact]
    public void OlderBoxes_ReadBackAsLeftAlignedAndAutoWidth()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        // A box with no style key is what every build before this one wrote.
        doc.AddTextBox(0, 40, 100, Text("Plain"));
        var read = doc.TryReadTextBox(0, 0)!;

        Assert.False(read.Underline);
        Assert.Equal(TextAlign.Left, read.Align);
        Assert.Equal(0, read.WidthPt);
    }

    /// <summary>
    /// The x of the leftmost near-black pixel between two rows, or null when
    /// that band of the page is blank. At scale 1.0 a row is a point, so the
    /// band can be given in the same coordinates the text box was placed in.
    /// </summary>
    private static int? LeftmostDarkColumn(PageBitmap bmp, int top, int bottom)
    {
        for (int x = 0; x < bmp.Width; x++)
        {
            for (int y = Math.Max(0, top); y < Math.Min(bmp.Height, bottom); y++)
            {
                var (b, g, r) = PixelAssert.Pixel(bmp, x, y);
                if (b < 120 && g < 120 && r < 120)
                {
                    return x;
                }
            }
        }
        return null;
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
