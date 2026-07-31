using Rune.Engine;

namespace Rune.Tests;

/// <summary>
/// Guards tests/corpus/form.pdf itself. The fixture is hand-assembled by
/// tools/gen-corpus.ps1 with byte-accurate xref offsets, so a small edit there
/// can silently produce a file PDFium parses but reads as empty. Every form
/// test downstream assumes these four widgets exist — assert it once, here.
/// </summary>
public class FormFixtureTests
{
    /// <summary>FPDF_ANNOT_SUBTYPE_WIDGET from fpdf_annot.h.</summary>
    private const int AnnotWidget = 20;

    [Fact]
    public void FormPdf_Opens_WithOnePage()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));
        Assert.Equal(1, doc.PageCount);
    }

    [Fact]
    public void FormPdf_HasFourWidgetAnnotations()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));
        var annots = doc.GetAnnotations(0);

        Assert.Equal(4, annots.Count);
        Assert.All(annots, a => Assert.Equal(AnnotWidget, a.Subtype));
    }

    [Fact]
    public void FormPdf_WidgetRectsAreOnPage_WithSaneGeometry()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));

        foreach (var a in doc.GetAnnotations(0))
        {
            // Top-left origin, page is 612x792pt. A widget that lands off-page
            // or collapses to zero means the /Rect or the coord conversion is wrong.
            Assert.InRange(a.X, 0, 612);
            Assert.InRange(a.Y, 0, 792);
            Assert.True(a.Width > 5, $"widget {a.Index} width was {a.Width}");
            Assert.True(a.Height > 5, $"widget {a.Index} height was {a.Height}");
        }
    }

    [Fact]
    public void FormPdf_RendersLabelText()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));
        var bmp = doc.RenderPage(0, 1.0f);

        // The three field labels are real page content and must rasterize even
        // before any form-fill environment exists.
        Assert.True(PixelAssert.CountNonWhite(bmp) > 200,
            $"expected label glyphs in the render, got {PixelAssert.CountNonWhite(bmp)} non-white pixels");
    }
}
