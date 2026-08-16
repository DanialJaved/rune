using Rune.Engine;

namespace Rune.Tests;

/// <summary>
/// The /DA grammar, and the half of it that matters more: whether a rewritten
/// /DA actually reaches the pixels.
///
/// Reading the string back proves nothing on its own. PDFium caches a widget's
/// appearance stream, so a /DA can read back exactly right while the page keeps
/// drawing the old one — the same class of trap as the stamp resize in v0.6.0,
/// where GetMatrix agreed with what had been asked for and the render did not.
/// So the tests that matter here count pixels.
/// </summary>
public class FieldAppearanceTests
{
    private const double NameX = 325, NameY = 80;

    private static string TempPdf() => Path.Combine(Path.GetTempPath(), $"rune-da-{Guid.NewGuid():N}.pdf");

    // ---- the grammar ----

    [Fact]
    public void Read_TakesSizeAndBlackFromTheCorpusForm()
    {
        var read = DefaultAppearance.TryRead("/Helv 12 Tf 0 g");

        Assert.NotNull(read);
        Assert.Equal(12, read!.Value.FontSize);
        Assert.Equal((byte)0, read.Value.R);
        Assert.False(read.Value.IsAutoSize);
    }

    [Fact]
    public void Read_HandlesRgbAndCmykAndAMissingColour()
    {
        Assert.Equal((byte)255, DefaultAppearance.TryRead("/Helv 9 Tf 1 0 0 rg")!.Value.R);
        Assert.Equal((byte)0, DefaultAppearance.TryRead("/Helv 9 Tf 1 0 0 rg")!.Value.G);

        // Pure cyan in CMYK composites to (0, 255, 255) over white.
        var cyan = DefaultAppearance.TryRead("/Helv 9 Tf 1 0 0 0 k")!.Value;
        Assert.Equal((byte)0, cyan.R);
        Assert.Equal((byte)255, cyan.G);

        // No colour operator is not a failure: PDF's default is black.
        var plain = DefaultAppearance.TryRead("/Helv 9 Tf")!.Value;
        Assert.Equal((byte)0, plain.R);
        Assert.Equal(9, plain.FontSize);
    }

    [Fact]
    public void Read_TreatsZeroSizeAsAutoFit()
    {
        Assert.True(DefaultAppearance.TryRead("/Helv 0 Tf 0 g")!.Value.IsAutoSize);
    }

    [Fact]
    public void Read_RefusesAStringWithNoTf()
    {
        // Nothing to anchor to, so nothing can be said about size or font.
        Assert.Null(DefaultAppearance.TryRead("0 g"));
        Assert.Null(DefaultAppearance.TryRead(""));
        Assert.Null(DefaultAppearance.TryRead(null));
    }

    [Fact]
    public void Write_KeepsTheFontResourceAndReplacesSizeAndColour()
    {
        string? written = DefaultAppearance.TryWrite("/Helv 12 Tf 0 g", new FieldAppearance(18, 255, 0, 0));

        // The resource name is a key into the AcroForm's /DR. Losing it renders
        // the field in no font at all.
        Assert.Equal("/Helv 18 Tf 1 0 0 rg", written);
    }

    [Fact]
    public void Write_DropsAnyExistingColourWhicheverOperatorItUsed()
    {
        foreach (string original in new[]
        {
            "/Helv 12 Tf 0 g",
            "/Helv 12 Tf 0 0 1 rg",
            "/Helv 12 Tf 0 0 0 1 k",
            "0 g /Helv 12 Tf",
        })
        {
            string? written = DefaultAppearance.TryWrite(original, new FieldAppearance(10, 0, 128, 0));
            Assert.Equal("/Helv 10 Tf 0 0.502 0 rg", written);
        }
    }

    [Fact]
    public void Write_RoundTripsThroughRead()
    {
        var wanted = new FieldAppearance(14, 12, 34, 56);
        string? written = DefaultAppearance.TryWrite("/Helv 12 Tf 0 g", wanted);
        var read = DefaultAppearance.TryRead(written)!.Value;

        Assert.Equal(wanted.FontSize, read.FontSize);
        // One part in 255 of rounding through the 0..1 operands, no more.
        Assert.InRange(read.R, wanted.R - 1, wanted.R + 1);
        Assert.InRange(read.G, wanted.G - 1, wanted.G + 1);
        Assert.InRange(read.B, wanted.B - 1, wanted.B + 1);
    }

    [Fact]
    public void Write_LeavesAStringItCannotAnchorAlone()
    {
        Assert.Null(DefaultAppearance.TryWrite("0 g", FieldAppearance.Default));
        Assert.Null(DefaultAppearance.TryWrite("", FieldAppearance.Default));
    }

    // ---- the pixels ----

    [Fact]
    public void GetFieldAppearance_ReadsWhatTheCorpusFormDeclares()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));

        var appearance = doc.GetFieldAppearance(0, "name");

        Assert.NotNull(appearance);
        Assert.Equal(12, appearance!.Value.FontSize);
        Assert.Equal((byte)0, appearance.Value.R);
    }

    [Fact]
    public void SetFieldAppearance_RepaintsTheWidget_NotJustTheDictionary()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));

        doc.FormClick(0, NameX, NameY);
        foreach (char c in "Wwwww")
        {
            doc.FormChar(0, c);
        }
        doc.FormKillFocus();

        int redBefore = CountRed(doc.RenderPage(0, 1.0f));
        Assert.Equal(0, redBefore); // the corpus form types in black

        Assert.True(doc.SetFieldAppearance(0, "name", new FieldAppearance(12, 255, 0, 0)));

        int redAfter = CountRed(doc.RenderPage(0, 1.0f));
        Assert.True(redAfter > 0,
            "the /DA was rewritten but the widget still renders in the old colour");
    }

    [Fact]
    public void SetFieldAppearance_BiggerTextCoversMorePage()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));

        doc.FormClick(0, NameX, NameY);
        foreach (char c in "Wwwww")
        {
            doc.FormChar(0, c);
        }
        doc.FormKillFocus();

        doc.SetFieldAppearance(0, "name", new FieldAppearance(8, 0, 0, 0));
        int small = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));

        doc.SetFieldAppearance(0, "name", new FieldAppearance(20, 0, 0, 0));
        int large = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));

        Assert.True(large > small, $"20pt drew {large} dark pixels, 8pt drew {small}");
    }

    [Fact]
    public void SetFieldAppearance_SurvivesSaveAndReopen_InTheFileAndOnThePage()
    {
        string saved = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf")))
            {
                doc.FormClick(0, NameX, NameY);
                foreach (char c in "Wwwww")
                {
                    doc.FormChar(0, c);
                }
                Assert.True(doc.SetFieldAppearance(0, "name", new FieldAppearance(17, 255, 0, 0)));
                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);
            var appearance = reopened.GetFieldAppearance(0, "name")!.Value;

            Assert.Equal(17, appearance.FontSize);
            Assert.Equal((byte)255, appearance.R);
            Assert.Equal((byte)0, appearance.B);

            // The string surviving is not enough. A /DA that only takes effect
            // because Rune re-runs the form layer would render as before in
            // every other reader; the saved file has to carry the appearance.
            Assert.True(CountRed(reopened.RenderPage(0, 1.0f)) > 0,
                "the reopened file renders the old colour, so the appearance did not persist");
        }
        finally
        {
            if (File.Exists(saved)) { File.Delete(saved); }
        }
    }

    [Fact]
    public void SetFieldAppearance_MarksTheDocumentDirty()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));
        Assert.False(doc.IsDirty);

        doc.SetFieldAppearance(0, "name", new FieldAppearance(15, 0, 0, 0));

        Assert.True(doc.IsDirty);
    }

    [Fact]
    public void SetFieldAppearance_ReportsFalseForAFieldThatIsNotThere()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));

        Assert.False(doc.SetFieldAppearance(0, "no-such-field", FieldAppearance.Default));
    }

    /// <summary>
    /// Strongly red pixels. The field sits under a blue highlight wash, so
    /// "not white" is saturated before the test starts and cannot see the text.
    /// </summary>
    private static int CountRed(PageBitmap bmp)
    {
        int red = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                var (b, g, r) = PixelAssert.Pixel(bmp, x, y);
                if (r > 140 && g < 90 && b < 90)
                {
                    red++;
                }
            }
        }
        return red;
    }
}
