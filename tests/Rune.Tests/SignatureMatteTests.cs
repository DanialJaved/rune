using Rune.Engine;

namespace Rune.Tests;

/// <summary>
/// Keying the paper out of a photographed signature.
///
/// <see cref="KeyedPhoto_StampsWithoutCoveringThePage"/> is the gate: it is the
/// only test that states what the feature is for, and it does so by placing the
/// same fixture twice — once raw, once keyed — over the text of a real PDF.
/// Everything else pins one decision inside the matte.
///
/// The fixtures are synthesised rather than loaded, because the engine cannot
/// decode an image (that lives in WinUI) and because a hand-built buffer is the
/// only way to know the exact answer a pixel should produce.
/// </summary>
public class SignatureMatteTests
{
    // ---- fixtures ----

    /// <summary>Uniform opaque paper at the given luma.</summary>
    private static byte[] Paper(int width, int height, byte level)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = level;
            pixels[i + 1] = level;
            pixels[i + 2] = level;
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    /// <summary>Shades the paper from <paramref name="left"/> to <paramref name="right"/> across x — a hand shadow.</summary>
    private static void WithLumaRamp(byte[] bgra, int width, int height, int left, int right)
    {
        for (int x = 0; x < width; x++)
        {
            byte level = (byte)(left + (right - left) * x / Math.Max(1, width - 1));
            for (int y = 0; y < height; y++)
            {
                int i = (y * width + x) * 4;
                bgra[i] = level;
                bgra[i + 1] = level;
                bgra[i + 2] = level;
            }
        }
    }

    /// <summary>Paints a hard-edged rectangle — a pen stroke, without antialiasing to muddy the assertions.</summary>
    private static void DrawBar(byte[] bgra, int width, int x0, int y0, int w, int h, byte b, byte g, byte r)
    {
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                int i = (y * width + x) * 4;
                bgra[i] = b;
                bgra[i + 1] = g;
                bgra[i + 2] = r;
                bgra[i + 3] = 255;
            }
        }
    }

    /// <summary>Deterministic +/- noise. Its own LCG, so the fixture never depends on Random's seeding.</summary>
    private static void Noise(byte[] bgra, int amplitude, uint seed)
    {
        uint state = seed;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            state = (state * 1664525) + 1013904223;
            int delta = (int)(state >> 16) % (amplitude * 2 + 1) - amplitude;
            for (int c = 0; c < 3; c++)
            {
                bgra[i + c] = (byte)Math.Clamp(bgra[i + c] + delta, 0, 255);
            }
        }
    }

    private static int AlphaAt(byte[] bgra, int width, int x, int y) => bgra[(y * width + x) * 4 + 3];

    private static (byte B, byte G, byte R) BgrAt(byte[] bgra, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        return (bgra[i], bgra[i + 1], bgra[i + 2]);
    }

    /// <summary>Paper 250 with a near-black 60x20 bar at (70,50). The workhorse fixture.</summary>
    private static byte[] PaperWithBar(byte ink = 30)
    {
        var bgra = Paper(200, 120, 250);
        DrawBar(bgra, 200, 70, 50, 60, 20, ink, ink, ink);
        return bgra;
    }

    private static readonly MatteOptions Uncropped = new() { Crop = false };

    // ---- the basics ----

    [Fact]
    public void Corners_GoFullyTransparent()
    {
        var result = SignatureMatte.RemoveBackground(PaperWithBar(), 200, 120, Uncropped);

        Assert.Equal(MatteOutcome.Keyed, result.Outcome);
        foreach (var (x, y) in new[] { (0, 0), (199, 0), (0, 119), (199, 119) })
        {
            Assert.Equal(0, AlphaAt(result.Bgra, 200, x, y));
        }
    }

    [Fact]
    public void StrokeCore_StaysOpaque()
    {
        var result = SignatureMatte.RemoveBackground(PaperWithBar(), 200, 120, Uncropped);

        Assert.Equal(255, AlphaAt(result.Bgra, 200, 100, 60));
    }

    /// <summary>A blue pen has to stay blue: the unmix must not desaturate the ink it recovers.</summary>
    [Fact]
    public void StrokeColour_Survives()
    {
        var bgra = Paper(200, 120, 250);
        DrawBar(bgra, 200, 70, 50, 60, 20, 180, 60, 30); // BGR: a blue ballpoint

        var result = SignatureMatte.RemoveBackground(bgra, 200, 120, Uncropped);

        var (b, _, r) = BgrAt(result.Bgra, 200, 100, 60);
        Assert.True(b > r + 60, $"blue ink came back as ({b},_,{r}) — the hue did not survive keying");
    }

    /// <summary>
    /// The test a single global threshold cannot pass, and therefore the whole
    /// justification for estimating paper per tile: under a 100-luma gradient the
    /// dark end of the paper is further from the bright end than ink is from
    /// paper, so one constant either keeps the shadow or loses the ink.
    /// </summary>
    [Fact]
    public void UnevenLighting_DoesNotBlotch()
    {
        var bgra = Paper(200, 120, 250);
        WithLumaRamp(bgra, 200, 120, 250, 150);
        DrawBar(bgra, 200, 70, 50, 60, 20, 30, 30, 30);

        var result = SignatureMatte.RemoveBackground(bgra, 200, 120, Uncropped);

        for (int y = 0; y < 120; y++)
        {
            for (int x = 0; x < 200; x++)
            {
                bool insideBar = x >= 70 && x < 130 && y >= 50 && y < 70;
                if (!insideBar)
                {
                    Assert.Equal(0, AlphaAt(result.Bgra, 200, x, y));
                }
            }
        }
    }

    /// <summary>
    /// A stroke thicker than the tile grid. Every tile in its middle reads as
    /// ink, so a 3x3 dilation would still find only ink around the centre and
    /// the stroke would develop a hole — the fill has to reach real paper.
    /// </summary>
    [Fact]
    public void FullyInkedTile_DoesNotPunchAHole()
    {
        var bgra = Paper(200, 120, 250);
        DrawBar(bgra, 200, 50, 35, 100, 50, 30, 30, 30);

        var result = SignatureMatte.RemoveBackground(bgra, 200, 120, Uncropped);

        for (int y = 37; y < 83; y++)
        {
            for (int x = 52; x < 148; x++)
            {
                Assert.Equal(255, AlphaAt(result.Bgra, 200, x, y));
            }
        }
    }

    /// <summary>
    /// Photographed "black" ink bottoms out around luma 90, never near 0. A fixed
    /// upper threshold leaves the whole signature translucent; deriving it from
    /// the darkest ink actually present is what removes the need for a slider.
    /// </summary>
    [Fact]
    public void WashedOutPhoto_StillReachesFullOpacity()
    {
        var result = SignatureMatte.RemoveBackground(PaperWithBar(ink: 90), 200, 120, Uncropped);

        Assert.Equal(255, AlphaAt(result.Bgra, 200, 100, 60));
        Assert.Equal(0, AlphaAt(result.Bgra, 200, 0, 0));
    }

    /// <summary>
    /// The unmix gate, and the direct analogue of
    /// StampTests.HalfAlphaGrey_CompositesAsStraightAlpha. A pixel photographed
    /// at exactly half ink coverage must composite back to what the camera saw;
    /// without the unmix it returns visibly lighter, which is what makes a soft
    /// ramp look faded rather than smooth.
    /// </summary>
    [Fact]
    public void SoftEdge_CompositesBackToTheOriginal()
    {
        var bgra = Paper(200, 120, 255);
        DrawBar(bgra, 200, 70, 50, 60, 20, 0, 0, 0);      // solid ink, sets the ink level
        DrawBar(bgra, 200, 150, 0, 1, 120, 127, 127, 127); // one column at half coverage

        var result = SignatureMatte.RemoveBackground(bgra, 200, 120, Uncropped);

        int alpha = AlphaAt(result.Bgra, 200, 150, 60);
        var (b, _, _) = BgrAt(result.Bgra, 200, 150, 60);
        int overWhite = ((alpha * b) + ((255 - alpha) * 255)) / 255;

        Assert.True(Math.Abs(overWhite - 127) <= 12,
            $"a half-coverage pixel keyed to alpha {alpha}, colour {b} composites back to {overWhite}, "
            + "not the 127 the camera saw. ~191 means the unmix did not run.");
    }

    // ---- cropping ----

    [Fact]
    public void CropsToTheInk()
    {
        var bgra = Paper(400, 300, 250);
        DrawBar(bgra, 400, 180, 144, 40, 12, 30, 30, 30);

        var result = SignatureMatte.RemoveBackground(bgra, 400, 300, new MatteOptions { Padding = 4 });

        Assert.Equal(MatteOutcome.Keyed, result.Outcome);
        Assert.InRange(result.Width, 46, 50);   // 40 + 2*4
        Assert.InRange(result.Height, 18, 22);  // 12 + 2*4
    }

    /// <summary>The preview runs uncropped so its dimensions never change under the user.</summary>
    [Fact]
    public void CropIsSkipped_WhenCropIsFalse()
    {
        var bgra = Paper(400, 300, 250);
        DrawBar(bgra, 400, 180, 144, 40, 12, 30, 30, 30);

        var result = SignatureMatte.RemoveBackground(bgra, 400, 300, Uncropped);

        Assert.Equal(400, result.Width);
        Assert.Equal(300, result.Height);
    }

    /// <summary>One speck of dust in a corner would defeat a naive bounding box entirely.</summary>
    [Fact]
    public void SpeckDoesNotDefeatTheCrop()
    {
        var bgra = Paper(200, 150, 250);
        DrawBar(bgra, 200, 80, 70, 40, 12, 30, 30, 30);
        DrawBar(bgra, 200, 5, 5, 1, 1, 0, 0, 0); // the speck

        var result = SignatureMatte.RemoveBackground(bgra, 200, 150, new MatteOptions { Padding = 4 });

        Assert.InRange(result.Width, 46, 50);
        Assert.InRange(result.Height, 18, 22);
    }

    // ---- the paths that decline to key ----

    [Fact]
    public void AlreadyTransparent_IsLeftAlone()
    {
        var bgra = new byte[100 * 100 * 4]; // all transparent
        DrawBar(bgra, 100, 35, 35, 30, 30, 0, 0, 0);

        var result = SignatureMatte.RemoveBackground(bgra, 100, 100);

        Assert.Equal(MatteOutcome.SkippedHasAlpha, result.Outcome);
        Assert.Same(bgra, result.Bgra);
    }

    /// <summary>A photo of a blank sheet must not come back as a grey wash of keyed sensor noise.</summary>
    [Fact]
    public void BlankPaper_ReportsNoInk()
    {
        var bgra = Paper(200, 120, 250);
        Noise(bgra, 4, seed: 7);

        var result = SignatureMatte.RemoveBackground(bgra, 200, 120, Uncropped);

        Assert.Equal(MatteOutcome.NoInkFound, result.Outcome);
        Assert.Same(bgra, result.Bgra);
    }

    // ---- the gate ----

    /// <summary>Dark pixels inside a page-local rect, at 1 px per point.</summary>
    private static int DarkInside(PageBitmap bmp, int x, int y, int w, int h)
    {
        int dark = 0;
        for (int py = Math.Max(0, y); py < Math.Min(bmp.Height, y + h); py++)
        {
            for (int px = Math.Max(0, x); px < Math.Min(bmp.Width, x + w); px++)
            {
                var (b, g, r) = PixelAssert.Pixel(bmp, px, py);
                if (r < 120 && g < 120 && b < 120)
                {
                    dark++;
                }
            }
        }
        return dark;
    }

    /// <summary>
    /// THE GATE. The same photographed signature placed over hello.pdf's text
    /// twice: raw, it covers the text with an opaque sheet of paper — which is
    /// exactly what import did before this feature. Keyed, the text reads
    /// through. The raw half of the assertion is load-bearing: without it a
    /// fixture that never covered anything would make the keyed half vacuous.
    /// </summary>
    [Fact]
    public void KeyedPhoto_StampsWithoutCoveringThePage()
    {
        // Ink in the bottom third, so the stamp's transparent upper region is
        // what lands on the text band.
        var photo = Paper(200, 120, 250);
        DrawBar(photo, 200, 40, 90, 120, 20, 30, 30, 30);

        var keyed = SignatureMatte.RemoveBackground(photo, 200, 120, Uncropped);
        Assert.Equal(MatteOutcome.Keyed, keyed.Outcome);

        // hello.pdf's text sits around x 72-280, y 60-90 in top-left points.
        const int TextX = 40, TextY = 40, TextW = 300, TextH = 60;

        int before, raw, after;
        using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf")))
        {
            before = DarkInside(doc.RenderPage(0, 1.0f), TextX, TextY, TextW, TextH);
            doc.AddStamp(0, 40, 30, 540, 320, photo, 200, 120);
            raw = DarkInside(doc.RenderPage(0, 1.0f), TextX, TextY, TextW, TextH);
        }
        using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf")))
        {
            doc.AddStamp(0, 40, 30, 540, 320, keyed.Bgra, keyed.Width, keyed.Height);
            after = DarkInside(doc.RenderPage(0, 1.0f), TextX, TextY, TextW, TextH);
        }

        Assert.True(before > 100, "fixture should have visible text to cover");
        Assert.True(raw < before * 0.5,
            $"the unkeyed photo did not cover the text ({before} -> {raw} dark pixels), "
            + "so this test cannot prove that keying is what saved it");
        Assert.True(after >= before * 0.95,
            $"the keyed photo still covered the text: {before} -> {after} dark pixels");
    }

    // ---- edges ----

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(1, 500)]
    [InlineData(500, 1)]
    public void TinyImages_AreHandled(int width, int height)
    {
        var bgra = Paper(width, height, 250);

        var result = SignatureMatte.RemoveBackground(bgra, width, height);

        Assert.True(result.Width > 0 && result.Height > 0);
        Assert.True(result.Bgra.Length >= result.Width * result.Height * 4);
    }

    [Fact]
    public void UndersizedBuffer_Throws()
    {
        Assert.Throws<ArgumentException>(() => SignatureMatte.RemoveBackground(new byte[16], 100, 100));
    }

    /// <summary>What justifies the all-integer arithmetic: the tests above can assert exact values.</summary>
    [Fact]
    public void Deterministic_SameInputSameOutput()
    {
        var bgra = PaperWithBar();
        Noise(bgra, 6, seed: 42);

        var first = SignatureMatte.RemoveBackground(bgra, 200, 120, Uncropped);
        var second = SignatureMatte.RemoveBackground(bgra, 200, 120, Uncropped);

        Assert.Equal(first.Bgra, second.Bgra);
    }

    // ---- helpers used by the import path ----

    [Fact]
    public void HasMeaningfulAlpha_IgnoresAnOpaqueJpegButSpotsATransparentPng()
    {
        Assert.False(SignatureMatte.HasMeaningfulAlpha(Paper(100, 100, 250), 100, 100));

        var transparent = new byte[100 * 100 * 4];
        DrawBar(transparent, 100, 40, 40, 20, 20, 0, 0, 0);
        Assert.True(SignatureMatte.HasMeaningfulAlpha(transparent, 100, 100));
    }

    [Fact]
    public void AlphaBounds_TightensAroundOpaqueContent()
    {
        var bgra = new byte[200 * 150 * 4];
        DrawBar(bgra, 200, 80, 70, 40, 12, 0, 0, 0);

        var box = SignatureMatte.AlphaBounds(bgra, 200, 150, padding: 4);

        Assert.NotNull(box);
        Assert.Equal(76, box!.Value.X);
        Assert.Equal(66, box.Value.Y);
        Assert.Equal(48, box.Value.Width);
        Assert.Equal(20, box.Value.Height);
    }

    /// <summary>
    /// Direct2D rejects a straight-alpha bitmap outright, so the on-page hover
    /// preview has to premultiply first. Half-alpha is what separates the two
    /// conventions — the same pixel that pins the straight-alpha direction in
    /// StampTests.HalfAlphaGrey_CompositesAsStraightAlpha.
    /// </summary>
    [Fact]
    public void ToPremultiplied_ScalesColourByAlphaAndLeavesTheEdgesAlone()
    {
        var straight = new byte[]
        {
            200, 100, 50, 128,  // half alpha -> colour halves
            200, 100, 50, 255,  // opaque -> unchanged
            200, 100, 50, 0,    // transparent -> zeroed
        };

        var premultiplied = SignatureMatte.ToPremultiplied(straight);

        Assert.Equal((byte)(200 * 128 / 255), premultiplied[0]);
        Assert.Equal((byte)(100 * 128 / 255), premultiplied[1]);
        Assert.Equal((byte)128, premultiplied[3]);

        Assert.Equal((byte)200, premultiplied[4]);
        Assert.Equal((byte)255, premultiplied[7]);

        Assert.Equal((byte)0, premultiplied[8]);
        Assert.Equal((byte)0, premultiplied[11]);

        Assert.NotSame(straight, premultiplied); // the caller's buffer still gets stamped
        Assert.Equal((byte)200, straight[0]);
    }

    [Fact]
    public void CropTo_CopiesTheRequestedRectangle()
    {
        var bgra = Paper(20, 10, 200);
        DrawBar(bgra, 20, 5, 3, 4, 2, 10, 20, 30);

        var (cropped, w, h) = SignatureMatte.CropTo(bgra, 20, 10, 5, 3, 4, 2);

        Assert.Equal(4, w);
        Assert.Equal(2, h);
        Assert.Equal((10, 20, 30), BgrAt(cropped, 4, 0, 0));
        Assert.Equal((10, 20, 30), BgrAt(cropped, 4, 3, 1));
    }
}
