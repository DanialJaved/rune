using Rune.Engine;
namespace Rune.Tests;
public class RotScaleRepro
{
    [Xunit.Fact]
    public void RenderPage_Rotated_AtAppScale_HasInk()
    {
        using var doc = PdfDocument.Open(System.IO.Path.Combine(System.AppContext.BaseDirectory, "corpus", "linked.pdf"));
        var bmp = doc.RenderPage(0, scale: 2.225f, rotation: 1);
        int nonWhite = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                int i = y * bmp.Stride + x * 4;
                if (bmp.Pixels[i] != 0xFF || bmp.Pixels[i+1] != 0xFF || bmp.Pixels[i+2] != 0xFF) nonWhite++;
            }
        Xunit.Assert.True(nonWhite > 500, $"expected ink, got {nonWhite} non-white of {bmp.Width}x{bmp.Height}");
    }
}