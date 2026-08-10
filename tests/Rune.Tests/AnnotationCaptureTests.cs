using Rune.Engine;

namespace Rune.Tests;

/// <summary>
/// <see cref="PdfDocument.CaptureAnnotation"/> over the two kinds of stamp.
///
/// Until v0.7.0 it returned null for subtype 13, so the eraser had nothing to put
/// on the undo stack and Ctrl+Z after erasing a signature did nothing at all.
/// These assert on rendered pixels rather than on the spec, because a spec that
/// reads back correctly and rebuilds to a blank page is exactly the failure that
/// was possible here.
/// </summary>
public class AnnotationCaptureTests
{
    [Fact]
    public void CaptureAnnotation_RebuildsATextBoxItErased()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        int blank = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));

        doc.AddTextBox(0, 60, 200, new TextBoxContent("Erase me", PdfStandardFont.Courier, 22, 0, 0, 0));
        int written = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));

        var captured = doc.CaptureAnnotation(0, 0);
        Assert.NotNull(captured);
        Assert.NotNull(captured!.Text);
        Assert.Equal("Courier", captured.Text!.PostScriptName);

        Assert.True(doc.RemoveAnnotation(0, 0));
        Assert.Equal(blank, PixelAssert.CountDark(doc.RenderPage(0, 1.0f)));

        doc.AddAnnotationFromSpec(captured);

        // Rebuilt from the words rather than from a raster, so it comes back
        // pixel-identical rather than merely close.
        Assert.Equal(written, PixelAssert.CountDark(doc.RenderPage(0, 1.0f)));
    }

    [Fact]
    public void CaptureAnnotation_RebuildsAPictureItErased()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        int blank = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));

        var black = new byte[30 * 30 * 4];
        for (int i = 3; i < black.Length; i += 4) { black[i] = 255; }
        doc.AddStamp(0, 60, 100, 90, 90, black, 30, 30);
        int placed = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));
        Assert.True(placed > blank);

        var captured = doc.CaptureAnnotation(0, 0);
        Assert.NotNull(captured);
        Assert.NotNull(captured!.Stamp);

        Assert.True(doc.RemoveAnnotation(0, 0));
        Assert.Equal(blank, PixelAssert.CountDark(doc.RenderPage(0, 1.0f)));

        doc.AddAnnotationFromSpec(captured);

        // Re-created from pixels through a fresh appearance, so it is allowed to
        // differ by an edge pixel or two where the scale lands.
        Assert.InRange(PixelAssert.CountDark(doc.RenderPage(0, 1.0f)), placed - 60, placed + 60);
    }
}
