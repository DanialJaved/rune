using Rune.Engine;

namespace Rune.Tests;

/// <summary>
/// Editing a text box that is already in the file: reading its style back out,
/// resizing it by re-rendering, and capturing it so an erase can be undone.
///
/// The read-back is deliberately not a session cache. Everything here therefore
/// has to survive a save and a reopen, which is what proves a text box placed
/// last week is as editable as one placed a second ago.
/// </summary>
public class TextBoxEditTests
{
    private static string TempPdf() => Path.Combine(Path.GetTempPath(), $"rune-tbe-{Guid.NewGuid():N}.pdf");

    private static TextBoxContent Text(string text, double size = 24) =>
        new(text, PdfStandardFont.Helvetica, size, 0, 0, 0);

    // ---- the name round-trip ----

    [Fact]
    public void PostScriptName_RoundTripsForEveryFaceRuneCanWrite()
    {
        foreach (var font in Enum.GetValues<PdfStandardFont>())
        {
            foreach (bool bold in new[] { false, true })
            {
                foreach (bool italic in new[] { false, true })
                {
                    string name = TextBoxContent.PostScriptNameFor(font, bold, italic);
                    var parsed = TextBoxContent.TryParsePostScriptName(name);

                    Assert.NotNull(parsed);
                    Assert.Equal((font, bold, italic), parsed!.Value);
                }
            }
        }
    }

    [Fact]
    public void TryParsePostScriptName_HandlesASubsetTagAndRefusesAStranger()
    {
        // Six capitals and a plus is an embedded subset. The standard 14 are
        // never subset, but a file that has been through another tool is not
        // Rune's to assume about.
        Assert.Equal((PdfStandardFont.Helvetica, true, false),
            TextBoxContent.TryParsePostScriptName("ABCDEF+Helvetica-Bold"));

        Assert.Null(TextBoxContent.TryParsePostScriptName("ArialMT"));
        Assert.Null(TextBoxContent.TryParsePostScriptName("Helvetica-Italic")); // Oblique, in this family
        Assert.Null(TextBoxContent.TryParsePostScriptName(""));
        Assert.Null(TextBoxContent.TryParsePostScriptName(null));
    }

    // ---- telling the two kinds of stamp apart ----

    [Fact]
    public void GetStampKind_SeparatesTextFromPixelsAndFromEverythingElse()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        doc.AddTextBox(0, 60, 100, Text("Words"));
        Assert.Equal(StampKind.Text, doc.GetStampKind(0, 0));

        var opaque = new byte[40 * 20 * 4];
        for (int i = 3; i < opaque.Length; i += 4) { opaque[i] = 255; }
        doc.AddStamp(0, 60, 300, 80, 40, opaque, 40, 20);
        Assert.Equal(StampKind.Image, doc.GetStampKind(0, 1));

        doc.AddNote(0, 200, 200, "not a stamp");
        Assert.Equal(StampKind.None, doc.GetStampKind(0, 2));
    }

    // ---- reading the style back ----

    [Fact]
    public void TryReadTextBox_RecoversEveryPartOfTheStyleAfterAReopen()
    {
        string saved = TempPdf();
        try
        {
            var written = new TextBoxContent(
                "Read me back\nboth lines", PdfStandardFont.Times, 19, 200, 40, 60,
                Bold: true, Italic: true);

            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf")))
            {
                doc.AddTextBox(0, 60, 200, written);
                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);
            var read = reopened.TryReadTextBox(0, 0);

            Assert.NotNull(read);
            Assert.Equal(written.Text, read!.Text);
            Assert.Equal(written.PostScriptName, read.PostScriptName);
            Assert.Equal(written.FontSize, read.FontSize, 2);
            Assert.Equal((written.R, written.G, written.B), (read.R, read.G, read.B));
        }
        finally
        {
            if (File.Exists(saved)) { File.Delete(saved); }
        }
    }

    [Fact]
    public void TryReadTextBox_ReturnsNullForAPictureAndForANote()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        var pixels = new byte[16 * 16 * 4];
        for (int i = 3; i < pixels.Length; i += 4) { pixels[i] = 255; }
        doc.AddStamp(0, 60, 100, 40, 40, pixels, 16, 16);
        doc.AddNote(0, 200, 200, "a note");

        Assert.Null(doc.TryReadTextBox(0, 0));
        Assert.Null(doc.TryReadTextBox(0, 1));
    }

    // ---- resizing ----

    [Fact]
    public void ResizeTextBox_ScalesTheFontRatherThanStretchingTheGlyphs()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.AddTextBox(0, 60, 200, Text("Grow me", size: 20));

        var before = doc.GetAnnotations(0)[0];
        var resized = doc.ResizeTextBox(0, 0, 60, 200, before.Width * 2);

        Assert.NotNull(resized);
        var after = doc.GetAnnotations(0)[resized!.Value.NewIndex];

        // The size doubled, so the box did, in both axes: a stretch would have
        // taken the width alone.
        Assert.Equal(40, doc.TryReadTextBox(0, resized.Value.NewIndex)!.FontSize, 1);
        Assert.InRange(after.Width, before.Width * 2 - 2, before.Width * 2 + 2);
        Assert.InRange(after.Height, before.Height * 2 - 2, before.Height * 2 + 2);
    }

    [Fact]
    public void ResizeTextBox_UndoesBackToThePixelsItStartedWith()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.AddTextBox(0, 60, 200, Text("Undo me", size: 18));
        int original = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));

        var before = doc.GetAnnotations(0)[0];
        var resized = doc.ResizeTextBox(0, 0, 60, 200, before.Width * 1.5);
        Assert.NotNull(resized);
        Assert.NotEqual(original, PixelAssert.CountDark(doc.RenderPage(0, 1.0f)));

        // The undo pair, exactly as the viewer plays it back.
        Assert.True(doc.RemoveAnnotation(0, resized!.Value.NewIndex));
        doc.AddAnnotationFromSpec(resized.Value.Before);

        // Rebuilt from the words rather than from a raster, so it comes back
        // pixel-identical rather than merely close.
        Assert.Equal(original, PixelAssert.CountDark(doc.RenderPage(0, 1.0f)));
    }

    [Fact]
    public void ResizeTextBox_ClampsRatherThanProducingAnUnreadableSize()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.AddTextBox(0, 60, 200, Text("Tiny", size: 24));

        var before = doc.GetAnnotations(0)[0];
        var resized = doc.ResizeTextBox(0, 0, 60, 200, before.Width / 1000);

        Assert.NotNull(resized);
        Assert.Equal(4, doc.TryReadTextBox(0, resized!.Value.NewIndex)!.FontSize, 1);
    }

    [Fact]
    public void ResizeTextBox_RefusesAPictureAndLeavesItAlone()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        var pixels = new byte[16 * 16 * 4];
        for (int i = 3; i < pixels.Length; i += 4) { pixels[i] = 255; }
        doc.AddStamp(0, 60, 100, 40, 40, pixels, 16, 16);

        Assert.Null(doc.ResizeTextBox(0, 0, 60, 100, 200));
        Assert.Single(doc.GetAnnotations(0));
    }

}
