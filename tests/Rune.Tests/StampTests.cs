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

    /// <summary>Mid-grey at half alpha — the fixture that can tell straight from premultiplied.</summary>
    private static byte[] HalfAlphaGrey(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = 128;
            pixels[i + 3] = 128;
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

    // ---- moving a placed stamp ----
    //
    // The failure these guard against is subtle: setting only the annotation's
    // rect moves its bounding box while the image inside keeps its own matrix,
    // so the signature reports a new home and is still drawn at the old one.
    // Asserting on the rect alone would pass while the page looked unchanged,
    // which is why these count ink in the raster.

    /// <summary>Dark pixels inside a page-local rect, at 1 px per point.</summary>
    private static int DarkInside(PageBitmap bmp, int x, int y, int w, int h)
    {
        int dark = 0;
        for (int py = Math.Max(0, y); py < Math.Min(bmp.Height, y + h); py++)
        {
            for (int px = Math.Max(0, x); px < Math.Min(bmp.Width, x + w); px++)
            {
                var (b, g, r) = PixelAssert.Pixel(bmp, px, py);
                if (r < 100 && g < 100 && b < 100)
                {
                    dark++;
                }
            }
        }
        return dark;
    }

    [Fact]
    public void MoveAnnotation_TakesTheImageWithTheRect()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.AddStamp(0, 60, 400, 200, 80, SolidBlack(100, 40), 100, 40);

        var before = doc.RenderPage(0, 1.0f);
        Assert.True(DarkInside(before, 60, 400, 200, 80) > 5000, "stamp did not land where it was placed");

        var previous = doc.MoveAnnotation(0, 0, 60, 600);
        Assert.NotNull(previous);

        var after = doc.RenderPage(0, 1.0f);
        Assert.True(DarkInside(after, 60, 600, 200, 80) > 5000, "the image did not follow the rect");
        Assert.True(DarkInside(after, 60, 400, 200, 80) < 500, "ink was left behind at the old position");
    }

    [Fact]
    public void MoveAnnotation_KeepsTheStampsSize()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.AddStamp(0, 60, 400, 200, 80, SolidBlack(100, 40), 100, 40);
        var placed = doc.GetAnnotations(0)[0];

        doc.MoveAnnotation(0, 0, 300, 620);
        var moved = doc.GetAnnotations(0)[0];

        // A move must not silently rescale. PDFium translates a stamp's
        // appearance to the rect but never scales it to fit, so changing the
        // rect size here would report one size and draw another. Resizing goes
        // through ResizeStamp, which re-creates the stamp instead.
        Assert.Equal(placed.Width, moved.Width, precision: 1);
        Assert.Equal(placed.Height, moved.Height, precision: 1);
    }

    // ---- reading a stamp's pixels back ----

    /// <summary>
    /// The capability the whole resize feature rests on. Rune keeps a
    /// StampImage for stamps it placed this session but not for one that was
    /// already in the file, and re-creating a stamp needs the pixels. This
    /// proves PDFium hands them back, alpha included, even after a round trip
    /// through disk.
    /// </summary>
    [Fact]
    public void TryReadStampImage_RecoversPixelsAndAlphaAfterAReopen()
    {
        string path = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf")))
            {
                doc.AddStamp(0, 60, 400, 200, 80, HalfAlphaGrey(100, 40), 100, 40);
                doc.SaveAs(path);
            }

            using var reopened = PdfDocument.Open(path);
            var image = reopened.TryReadStampImage(0, 0);

            Assert.NotNull(image);
            Assert.True(image!.Width > 0 && image.Height > 0);

            // Straight alpha, not premultiplied and not composited over white:
            // grey 128 at alpha 128 has to come back as grey 128 at alpha 128,
            // or a re-created signature would stamp as a pale block.
            int mid = ((image.Height / 2) * image.Width + image.Width / 2) * 4;
            Assert.Equal(128, image.Bgra[mid], tolerance: 8);
            Assert.Equal(128, image.Bgra[mid + 1], tolerance: 8);
            Assert.Equal(128, image.Bgra[mid + 2], tolerance: 8);
            Assert.Equal(128, image.Bgra[mid + 3], tolerance: 8);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadStampImage_ReturnsNullForANonStamp()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.AddNote(0, 100, 100, "not a stamp");

        Assert.Null(doc.TryReadStampImage(0, 0));
    }

    // ---- resize ----

    [Fact]
    public void ResizeStamp_ScalesTheDrawnImageNotJustTheBox()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.AddStamp(0, 60, 400, 200, 80, SolidBlack(100, 40), 100, 40);

        var result = doc.ResizeStamp(0, 0, 60, 400, 400, 160);
        Assert.NotNull(result);

        var after = doc.RenderPage(0, 1.0f);
        // The whole new box has to be inked. If only the rect had grown, the
        // image would still be 200x80 and the outer band would be blank.
        Assert.True(DarkInside(after, 60, 400, 400, 160) > 50000,
            "the image did not scale to the new box");
        Assert.True(DarkInside(after, 280, 400, 180, 160) > 20000,
            "the right of the new box is empty, so only the rect grew");
    }

    /// <summary>
    /// The case that defeated the appearance-matrix approach. Editing the matrix
    /// in place was exact once and then compounded, because the regenerated
    /// appearance kept its old /BBox: growing then shrinking back landed at half
    /// the requested size. Re-creating builds a fresh appearance each time, so
    /// this has to hold for any number of resizes.
    /// </summary>
    [Fact]
    public void ResizeStamp_RepeatedResizesDoNotCompound()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.AddStamp(0, 60, 400, 200, 80, SolidBlack(100, 40), 100, 40);

        int index = 0;
        foreach (var (w, h) in new[] { (400.0, 160.0), (200.0, 80.0), (300.0, 120.0), (200.0, 80.0) })
        {
            var step = doc.ResizeStamp(0, index, 60, 400, w, h);
            Assert.NotNull(step);
            index = step!.Value.NewIndex;

            var annotation = doc.GetAnnotations(0)[index];
            Assert.Equal(w, annotation.Width, precision: 1);
            Assert.Equal(h, annotation.Height, precision: 1);

            // And the ink actually fills it, which the reported size alone
            // would not have caught.
            Assert.True(DarkInside(doc.RenderPage(0, 1.0f), 60, 400, (int)w, (int)h) > w * h * 0.75,
                $"after resizing to {w}x{h} the ink did not fill the box");
        }
    }

    /// <summary>
    /// The other thing re-creation buys: it works on a stamp Rune never placed,
    /// because the pixels come out of the file rather than a session cache.
    /// </summary>
    [Fact]
    public void ResizeStamp_WorksOnAStampAlreadyInTheFile()
    {
        string path = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf")))
            {
                doc.AddStamp(0, 60, 400, 200, 80, SolidBlack(100, 40), 100, 40);
                doc.SaveAs(path);
            }

            using var reopened = PdfDocument.Open(path);
            Assert.NotNull(reopened.ResizeStamp(0, 0, 60, 400, 360, 144));

            Assert.True(DarkInside(reopened.RenderPage(0, 1.0f), 60, 400, 360, 144) > 40000,
                "a stamp loaded from disk did not resize");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResizeStamp_TheReturnedSpecRestoresTheOriginal()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.AddStamp(0, 60, 400, 200, 80, SolidBlack(100, 40), 100, 40);

        // Exactly the pair an undo entry replays.
        var result = doc.ResizeStamp(0, 0, 60, 400, 400, 160);
        Assert.NotNull(result);
        Assert.True(doc.RemoveAnnotation(0, result!.Value.NewIndex));
        doc.AddAnnotationFromSpec(result.Value.Before);

        var restored = Assert.Single(doc.GetAnnotations(0));
        Assert.Equal(200, restored.Width, precision: 1);
        Assert.Equal(80, restored.Height, precision: 1);

        var pixels = doc.RenderPage(0, 1.0f);
        Assert.True(DarkInside(pixels, 60, 400, 200, 80) > 12000, "undo lost the stamp");
        // The band only the enlarged version covered must be clear again.
        Assert.True(DarkInside(pixels, 280, 490, 160, 60) < 500,
            "undo restored the box but left the image enlarged");
    }

    [Fact]
    public void ResizeStamp_SurvivesASaveAndReopen()
    {
        string path = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf")))
            {
                doc.AddStamp(0, 60, 400, 200, 80, SolidBlack(100, 40), 100, 40);
                doc.ResizeStamp(0, 0, 60, 400, 300, 120);
                doc.SaveAs(path);
            }

            using var reopened = PdfDocument.Open(path);
            var annotation = Assert.Single(reopened.GetAnnotations(0));
            Assert.Equal(300, annotation.Width, precision: 1);
            Assert.Equal(120, annotation.Height, precision: 1);
            Assert.True(DarkInside(reopened.RenderPage(0, 1.0f), 60, 400, 300, 120) > 25000,
                "the resized stamp did not survive the round trip");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Transparency has to survive a resize, or a signature turns into a block.</summary>
    [Fact]
    public void ResizeStamp_KeepsTransparency()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        int textBefore = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));

        doc.AddStamp(0, 40, 30, 300, 70, FullyTransparent(150, 35), 150, 35);
        doc.ResizeStamp(0, 0, 40, 30, 540, 120); // now covers all the page text

        int textAfter = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));
        Assert.True(textAfter >= textBefore * 0.95,
            $"the resized stamp painted over the text: {textBefore} -> {textAfter} dark pixels");
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-40, 100)]
    public void ResizeStamp_DegenerateSizeIsIgnored(double widthPt, double heightPt)
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.AddStamp(0, 60, 400, 200, 80, SolidBlack(100, 40), 100, 40);

        Assert.Null(doc.ResizeStamp(0, 0, 60, 400, widthPt, heightPt));

        // And the original is still there, not half-removed.
        var untouched = Assert.Single(doc.GetAnnotations(0));
        Assert.Equal(200, untouched.Width, precision: 1);
        Assert.Equal(80, untouched.Height, precision: 1);
    }

    [Fact]
    public void RestoreAnnotationRect_PutsAMovedStampBack()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.AddStamp(0, 60, 400, 200, 80, SolidBlack(100, 40), 100, 40);

        // This is exactly the pair the undo stack replays.
        var original = doc.MoveAnnotation(0, 0, 300, 650);
        Assert.NotNull(original);
        Assert.True(doc.RestoreAnnotationRect(0, 0, original!.Value));

        var restored = doc.RenderPage(0, 1.0f);
        Assert.True(DarkInside(restored, 60, 400, 200, 80) > 5000, "undo did not return the stamp");
        Assert.True(DarkInside(restored, 300, 650, 200, 80) < 500, "a copy was left at the moved position");
    }

    [Fact]
    public void MoveAnnotation_SurvivesASaveAndReopen()
    {
        string path = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf")))
            {
                doc.AddStamp(0, 60, 400, 200, 80, SolidBlack(100, 40), 100, 40);
                doc.MoveAnnotation(0, 0, 60, 620);
                doc.SaveAs(path);
            }

            // GenerateContent is what makes the move part of the file rather
            // than a live-object edit; without it this reopens at the old spot.
            using var reopened = PdfDocument.Open(path);
            var page = reopened.RenderPage(0, 1.0f);
            Assert.True(DarkInside(page, 60, 620, 200, 80) > 5000, "the move was not serialized");
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }
}
