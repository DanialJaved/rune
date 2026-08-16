using Rune.Engine;

namespace Rune.Tests;

/// <summary>
/// What Ctrl+D reports, and the two pure helpers behind the parts of it that
/// are easy to get subtly wrong: PDF's own date syntax, and naming a page size.
/// </summary>
public class DocumentPropertiesTests
{
    // ---- PDF dates ----

    [Fact]
    public void PdfDate_ReadsAFullTimestampWithItsOffset()
    {
        var when = PdfDate.TryParse("D:20260812093015+01'00'");

        Assert.NotNull(when);
        Assert.Equal(new DateTimeOffset(2026, 8, 12, 9, 30, 15, TimeSpan.FromHours(1)), when);
    }

    [Fact]
    public void PdfDate_ReadsANegativeOffsetAndItsMinutes()
    {
        var when = PdfDate.TryParse("D:20260812093015-03'30'");

        Assert.NotNull(when);
        Assert.Equal(TimeSpan.FromHours(-3.5), when!.Value.Offset);
    }

    [Fact]
    public void PdfDate_FillsInWhateverTheWriterLeftOut()
    {
        // Everything below the year is optional, and an absent field means its
        // lowest legal value. Plenty of writers stop at the day.
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), PdfDate.TryParse("D:2026"));
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), PdfDate.TryParse("D:202608"));
        Assert.Equal(new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero), PdfDate.TryParse("D:20260812"));
    }

    [Fact]
    public void PdfDate_TakesAStringWithNoPrefix()
    {
        // The D: is required by the spec and omitted by plenty of files.
        Assert.Equal(new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero), PdfDate.TryParse("20260812"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("D:")]
    [InlineData("yesterday")]
    [InlineData("D:20261312")] // month 13
    [InlineData("D:20260232")] // 32 February
    public void PdfDate_RefusesWhatItCannotRead(string? value)
    {
        Assert.Null(PdfDate.TryParse(value));
    }

    // ---- paper sizes ----

    [Fact]
    public void PaperSize_NamesTheCommonOnesInEitherOrientation()
    {
        Assert.Equal("A4", PaperSize.Name(595.28, 841.89));
        Assert.Equal("A4", PaperSize.Name(841.89, 595.28));
        Assert.Equal("Letter", PaperSize.Name(612, 792));
        Assert.Equal("Legal", PaperSize.Name(612, 1008));
        Assert.Equal("A3", PaperSize.Name(841.89, 1190.55));
    }

    [Fact]
    public void PaperSize_AbsorbsTheRoundingEveryWriterDoesDifferently()
    {
        // A4 is 595.276pt and gets written as 595, 595.3 and 596 alike.
        Assert.Equal("A4", PaperSize.Name(595, 842));
        Assert.Equal("A4", PaperSize.Name(596, 841));
    }

    [Fact]
    public void PaperSize_HasNoNameForAnOddSize()
    {
        Assert.Null(PaperSize.Name(500, 500));
        Assert.Null(PaperSize.Name(612, 900));
    }

    [Fact]
    public void PaperSize_DescribesTheSizeWhetherOrNotItHasAName()
    {
        string a4 = PaperSize.Describe(595.28, 841.89);
        Assert.Contains("A4", a4);
        Assert.Contains("210 × 297 mm", a4);
        Assert.Contains("595.3 × 841.9 pt", a4);

        string odd = PaperSize.Describe(500, 400);
        Assert.DoesNotContain("(A", odd);
        Assert.Contains("500 × 400 pt", odd);
    }

    [Fact]
    public void PaperSize_SaysSoWhenThePageIsOnItsSide()
    {
        Assert.Contains("landscape", PaperSize.Describe(841.89, 595.28));
        Assert.DoesNotContain("landscape", PaperSize.Describe(595.28, 841.89));
    }

    // ---- what the dialog actually gets ----

    [Fact]
    public void Properties_AnswerEveryHeadingEvenWhenTheFileIsSilent()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        var sections = doc.GetDocumentProperties();
        var titles = sections.Select(s => s.Title).ToArray();

        Assert.Equal(
            ["Description", "Origin", "Pages", "Security", "Features", "File"],
            titles);

        // Blanks are shown as blanks rather than dropped. A missing Author row
        // cannot be told apart from an Author that was never looked for, which
        // is exactly what the old flat list did.
        var description = sections[0].Rows;
        Assert.Equal(["Title", "Author", "Subject", "Keywords"], description.Select(r => r.Name).ToArray());
        Assert.All(description, row => Assert.False(string.IsNullOrWhiteSpace(row.Value)));
    }

    [Fact]
    public void Properties_ReportThePageSizeAndTheCount()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        var pages = doc.GetDocumentProperties().Single(s => s.Title == "Pages").Rows;

        Assert.Equal(doc.PageCount.ToString(), pages.Single(r => r.Name == "Count").Value);
        Assert.Contains("pt", pages.Single(r => r.Name == "Page 1").Value);
    }

    [Fact]
    public void Properties_FollowTheCurrentPage()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("book-1000.pdf"));

        var pages = doc.GetDocumentProperties(currentPage: 41).Rows("Pages");

        // Asked about page 42, answered about page 42 — the dialog opens on
        // whatever you were reading, not always on page one.
        Assert.Contains(pages, r => r.Name == "Page 42");
    }

    [Fact]
    public void Properties_SayAnUnencryptedFileAllowsEverything()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        var security = doc.GetDocumentProperties().Rows("Security");

        Assert.Equal("No", security.Single(r => r.Name == "Encrypted").Value);
        Assert.Equal("Everything allowed", security.Single(r => r.Name == "Permissions").Value);
    }

    [Fact]
    public void Properties_NameTheFileAndTheFolderItIsIn()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        var file = doc.GetDocumentProperties().Rows("File");

        Assert.Equal("hello.pdf", file.Single(r => r.Name == "Name").Value);
        Assert.EndsWith("corpus", file.Single(r => r.Name == "Folder").Value);
        Assert.Contains(file, r => r.Name == "Size");
    }

    [Fact]
    public void Properties_TellAFillableFormFromNone()
    {
        using var plain = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        using var form = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));

        Assert.Equal("None", plain.GetDocumentProperties().Rows("Features").Single(r => r.Name == "Form").Value);
        Assert.StartsWith("AcroForm", form.GetDocumentProperties().Rows("Features").Single(r => r.Name == "Form").Value);
    }

    // ---- fonts ----

    [Fact]
    public void Fonts_ReportsWhatThePageDrawsWith()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        var fonts = doc.GetFontsUsed(maxPages: 10);

        Assert.NotEmpty(fonts);
        Assert.All(fonts, f => Assert.False(string.IsNullOrWhiteSpace(f.Name)));
    }

    [Fact]
    public void Fonts_FindTheOneRuneItselfWrote()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        // A text box lives in an annotation's appearance stream, not the page
        // content, so it is deliberately NOT in this list until it is flattened
        // — the same boundary that keeps it out of search.
        doc.AddTextBox(0, 60, 300, new TextBoxContent("Zarquon", PdfStandardFont.Courier, 18, 0, 0, 0));
        Assert.DoesNotContain(doc.GetFontsUsed(1), f => f.Name.Contains("Courier"));

        Assert.Equal(FlattenResult.Flattened, doc.FlattenPage(0));
        Assert.Contains(doc.GetFontsUsed(1), f => f.Name.Contains("Courier"));
    }

    [Fact]
    public void Fonts_StopAtThePageCapAndAreDeduplicated()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("book-1000.pdf"));

        var capped = doc.GetFontsUsed(maxPages: 3);
        var none = doc.GetFontsUsed(maxPages: 0);

        Assert.Empty(none);
        // One entry per name however many pages use it, which is the whole point
        // of a font list rather than a font census.
        Assert.Equal(capped.Select(f => f.Name).Distinct().Count(), capped.Count);
    }

    [Fact]
    public void Fonts_CanBeCancelledMidScan()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("book-1000.pdf"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => doc.GetFontsUsed(maxPages: 1000, cancelled.Token));
    }
}

internal static class PropertySectionExtensions
{
    /// <summary>The rows of one named section, so a test can name what it means.</summary>
    public static IReadOnlyList<(string Name, string Value)> Rows(
        this IReadOnlyList<PropertySection> sections, string title)
        => sections.Single(s => s.Title == title).Rows;
}
