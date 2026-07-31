using Rune.Engine;

namespace Rune.Tests;

/// <summary>
/// Image stamping — the mechanism behind visible signatures.
///
/// <see cref="TransparentRegion_LetsPageContentShowThrough"/> is the gating
/// test: a signature is mostly transparent, and nothing in the codebase knew
/// whether alpha survives PDFium's image path, because every other pixel route
/// fills opaque white first. If that test fails, the whole signature
/// interaction has to be redesigned rather than patched.
/// </summary>
public class StampTests
{
    private static string TempPdf() => Path.Combine(Path.GetTempPath(), $"rune-stamp-{Guid.NewGuid():N}.pdf");

    /// <summary>FPDF_ANNOT_SUBTYPE_STAMP.</summary>
    private const int AnnotStamp = 13;

    /// <summary>
    /// A fully transparent BGRA block. Straight (non-premultiplied) alpha,
    /// which is what an imported PNG or a rasterised signature produces.
    /// </summary>
    private static byte[] FullyTransparent(int width, int height) => new byte[width * height * 4];

    private static byte[] SolidBlack(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 3] = 255; // opaque, BGR all zero
        }
        return pixels;
    }

    [Fact]
    public void AddStamp_CreatesAStampAnnotation()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        doc.AddStamp(0, 100, 100, 180, 60, SolidBlack(90, 30), 90, 30);

        var stamp = Assert.Single(doc.GetAnnotations(0));
        Assert.Equal(AnnotStamp, stamp.Subtype);
        Assert.True(doc.IsDirty);
    }

    [Fact]
    public void AddStamp_IsVisibleInTheRender()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        int before = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));

        // Well below the text so it can't be confused with it.
        doc.AddStamp(0, 80, 500, 300, 100, SolidBlack(150, 50), 150, 50);

        int after = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));
        Assert.True(after > before + 5000, $"stamp did not reach the raster: {before} -> {after}");
    }

    /// <summary>
    /// THE GATE. Stamps a fully transparent block straight over the page text.
    ///
    /// If alpha survives, the glyphs underneath are untouched. If PDFium
    /// flattens alpha to opaque, they are painted over and vanish — and a
    /// signature, which is mostly transparent, would arrive as a solid block.
    ///
    /// CountDark, not CountNonWhite: an opaque white box reads as "unchanged"
    /// against a white page while having destroyed the text.
    /// </summary>
    [Fact]
    public void TransparentRegion_LetsPageContentShowThrough()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        int textBefore = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));
        Assert.True(textBefore > 100, "fixture should have visible text to cover");

        // hello.pdf's text sits around x 72-280, y 60-90 in top-left points;
        // this covers all of it.
        doc.AddStamp(0, 40, 30, 540, 120, FullyTransparent(270, 60), 270, 60);

        int textAfter = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));

        Assert.True(textAfter >= textBefore * 0.95,
            $"transparency did not survive stamping: dark pixels {textBefore} -> {textAfter}. "
            + "If this fails, signature stamps cannot use a transparent bitmap.");
    }

    /// <summary>
    /// Pins whether PDFium wants straight or premultiplied alpha.
    ///
    /// The transparency gate above can't tell them apart: a fully transparent
    /// pixel is all-zero either way, and so is pure black at any alpha. A
    /// MID-GREY at 50% is what separates them —
    ///   straight:       0.5*128 + 0.5*255 = 191 over white
    ///   read as premul: 128/0.5 = 255 (white), so 0.5*255 + 0.5*255 = 255
    ///
    /// This matters because Win2D's CanvasRenderTarget.GetPixelBytes() returns
    /// premultiplied, so a drawn signature's antialiased edges depend on
    /// getting the conversion right.
    /// </summary>
    [Fact]
    public void HalfAlphaGrey_CompositesAsStraightAlpha()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        const int w = 60, h = 30;
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 128;      // B
            pixels[i + 1] = 128;  // G
            pixels[i + 2] = 128;  // R
            pixels[i + 3] = 128;  // A — half
        }

        // Blank white area, well below hello.pdf's single line of text.
        doc.AddStamp(0, 100, 400, 200, 100, pixels, w, h);

        var bmp = doc.RenderPage(0, 1.0f);
        var (b, g, r) = PixelAssert.Pixel(bmp, 200, 450); // inside the stamp

        Assert.True(Math.Abs(r - 191) < 24 && Math.Abs(g - 191) < 24 && Math.Abs(b - 191) < 24,
            $"expected ~191 (straight alpha over white), got ({b},{g},{r}). "
            + "~255 would mean PDFium read the buffer as premultiplied.");
    }

    [Fact]
    public void AddStamp_ReturnsASpecThatRecreatesIt()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        var spec = doc.AddStamp(0, 100, 400, 200, 80, SolidBlack(100, 40), 100, 40);
        Assert.NotNull(spec);
        Assert.Equal(AnnotStamp, spec.Subtype);
        Assert.NotNull(spec.Stamp);

        int placed = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));

        // Undo, then redo from the spec — the classic round-trip.
        doc.RemoveLastAnnotation(0);
        Assert.Empty(doc.GetAnnotations(0));

        doc.AddAnnotationFromSpec(spec);
        var restored = Assert.Single(doc.GetAnnotations(0));
        Assert.Equal(AnnotStamp, restored.Subtype);

        int again = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));
        Assert.True(Math.Abs(again - placed) < placed * 0.05,
            $"re-created stamp differs from the original: {placed} -> {again} dark pixels");
    }

    [Fact]
    public void AddStamp_SaveAs_Reopen_Persists()
    {
        string saved = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf")))
            {
                doc.AddStamp(0, 100, 400, 200, 80, SolidBlack(100, 40), 100, 40);
                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);
            var stamp = Assert.Single(reopened.GetAnnotations(0));
            Assert.Equal(AnnotStamp, stamp.Subtype);
            Assert.True(PixelAssert.CountDark(reopened.RenderPage(0, 1.0f)) > 1000,
                "stamp pixels did not survive save/reopen");
        }
        finally
        {
            if (File.Exists(saved)) { File.Delete(saved); }
        }
    }

    [Theory]
    [InlineData(0, 40)]   // zero width
    [InlineData(40, 0)]   // zero height
    public void DegenerateSize_IsIgnored(double widthPt, double heightPt)
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        doc.AddStamp(0, 50, 50, widthPt, heightPt, SolidBlack(20, 20), 20, 20);

        Assert.Empty(doc.GetAnnotations(0));
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void UndersizedBuffer_IsIgnored()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        // Claims 100x100 but supplies far fewer bytes — must not read past it.
        doc.AddStamp(0, 50, 50, 100, 100, new byte[16], 100, 100);

        Assert.Empty(doc.GetAnnotations(0));
        Assert.False(doc.IsDirty);
    }
}
