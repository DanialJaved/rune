using Folio.Engine;
using Folio.PdfiumInterop;

namespace Folio.Tests;

public class PdfDocumentTests
{
    private static string CorpusPath(string name)
    {
        // tests/corpus is copied next to the test assembly via the csproj.
        return Path.Combine(AppContext.BaseDirectory, "corpus", name);
    }

    [Fact]
    public void Open_ValidPdf_ReportsPageCountAndSizes()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        Assert.Equal(2, doc.PageCount);

        var (width, height) = doc.GetPageSize(0);
        Assert.Equal(612f, width, precision: 1);   // US Letter in PDF points
        Assert.Equal(792f, height, precision: 1);
    }

    [Fact]
    public void RenderPage_ProducesInkOnWhiteBackground()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        var bmp = doc.RenderPage(0, scale: 1.0f);

        Assert.Equal(612, bmp.Width);
        Assert.Equal(792, bmp.Height);
        Assert.True(bmp.Pixels.Length >= bmp.Stride * bmp.Height); // pooled buffers may be larger

        // Count non-white pixels: the "Hello from Folio!" text must have
        // produced some ink, but most of the page must remain white.
        int nonWhite = 0;
        for (int i = 0; i < bmp.Stride * bmp.Height; i += 4)
        {
            if (bmp.Pixels[i] != 0xFF || bmp.Pixels[i + 1] != 0xFF || bmp.Pixels[i + 2] != 0xFF)
            {
                nonWhite++;
            }
        }

        int totalPixels = bmp.Width * bmp.Height;
        Assert.InRange(nonWhite, 100, totalPixels / 2);
    }

    [Fact]
    public void RenderPage_AtDoubleScale_DoublesPixelDimensions()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        var bmp = doc.RenderPage(0, scale: 2.0f);

        Assert.Equal(1224, bmp.Width);
        Assert.Equal(1584, bmp.Height);
    }

    [Fact]
    public void Open_CorruptFile_ThrowsPdfiumException()
    {
        var ex = Assert.Throws<PdfiumException>(() => PdfDocument.Open(CorpusPath("corrupt.pdf")));
        Assert.NotEqual(0u, ex.ErrorCode);
    }

    [Fact]
    public void Open_MissingFile_ThrowsFileNotFound()
    {
        Assert.ThrowsAny<IOException>(() => PdfDocument.Open(CorpusPath("does-not-exist.pdf")));
    }

    [Fact]
    public void RenderPage_ConcurrentCalls_DoNotCrash()
    {
        // PDFium itself is single-threaded; our lock must make concurrent
        // engine calls safe. Hammer it from several threads.
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        Parallel.For(0, 16, i =>
        {
            var bmp = doc.RenderPage(i % doc.PageCount, scale: 0.5f);
            Assert.True(bmp.Width > 0);
        });
    }
}
