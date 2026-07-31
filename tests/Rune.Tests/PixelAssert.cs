using Rune.Engine;

namespace Rune.Tests;

/// <summary>
/// Shared helpers for tests that assert on rendered output. Rendering is the
/// only way to prove an annotation, form field or stamp actually reaches the
/// page — the object model can look right while the pixels stay blank.
/// </summary>
internal static class PixelAssert
{
    /// <summary>tests/corpus is copied next to the test assembly via the csproj.</summary>
    public static string CorpusPath(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    /// <summary>Pixels that differ from the white page background — a proxy for "something was drawn".</summary>
    public static int CountNonWhite(PageBitmap bmp)
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

    /// <summary>
    /// Near-black pixels — glyphs and ink strokes. Use this instead of
    /// <see cref="CountNonWhite"/> when the thing being drawn sits inside an
    /// area that is already tinted (a form field's highlight wash, a
    /// highlighted text run): there, "non-white" is saturated before the test
    /// even starts and cannot detect the mark at all.
    /// </summary>
    public static int CountDark(PageBitmap bmp)
    {
        int dark = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                int i = y * bmp.Stride + x * 4;
                if (bmp.Pixels[i] < 120 && bmp.Pixels[i + 1] < 120 && bmp.Pixels[i + 2] < 120)
                {
                    dark++;
                }
            }
        }
        return dark;
    }

    /// <summary>One pixel as (B, G, R). For assertions about exact compositing.</summary>
    public static (byte B, byte G, byte R) Pixel(PageBitmap bmp, int x, int y)
    {
        int i = y * bmp.Stride + x * 4;
        return (bmp.Pixels[i], bmp.Pixels[i + 1], bmp.Pixels[i + 2]);
    }

    /// <summary>Warm-tinted pixels (yellow-ish), used to detect highlight fills.</summary>
    public static int CountTinted(PageBitmap bmp)
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
