using Rune.Engine;

namespace Rune.Tests;

/// <summary>
/// Exercises the form-fill environment end to end. The round-trip test below is
/// the single highest-value assertion in the feature: passing it proves the
/// FPDF_FORMFILLINFO struct layout, the delegate lifetimes, the top-left↔
/// bottom-left coordinate flip, the page cache keeping one stable handle, and
/// the kill-focus-before-save rule — all at once.
///
/// Field centres are derived from form.pdf's /Rect values (bottom-left origin,
/// 612x792 page) converted to the top-left page-local points this API takes:
///   name     [200 700 450 724] -> (325,  80)
///   country  [200 650 450 674] -> (325, 130)
///   agree    [200 602 220 622] -> (210, 180)
///   submit   [200 540 320 570] -> (260, 237)
/// </summary>
public class FormFillTests
{
    private static string TempPdf() => Path.Combine(Path.GetTempPath(), $"rune-form-{Guid.NewGuid():N}.pdf");

    private const double NameX = 325, NameY = 80;
    private const double CountryX = 325, CountryY = 130;
    private const double AgreeX = 210, AgreeY = 180;
    private const double SubmitX = 260, SubmitY = 237;

    [Fact]
    public void DocumentWithoutForm_ReportsNone_AndCreatesNoEnvironment()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        Assert.Equal(PdfFormKind.None, doc.FormKind);
        Assert.False(doc.HasFillableForm);
    }

    [Fact]
    public void AcroForm_IsDetected()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));

        Assert.Equal(PdfFormKind.AcroForm, doc.FormKind);
        Assert.True(doc.HasFillableForm);
    }

    [Fact]
    public void GetFormFields_ReturnsAllFourWidgets_WithNamesAndKinds()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));
        var fields = doc.GetFormFields(0);

        Assert.Equal(4, fields.Count);
        Assert.Equal(FormFieldKind.Text, fields.Single(f => f.Name == "name").Kind);
        Assert.Equal(FormFieldKind.ComboBox, fields.Single(f => f.Name == "country").Kind);
        Assert.Equal(FormFieldKind.CheckBox, fields.Single(f => f.Name == "agree").Kind);
        Assert.Equal(FormFieldKind.PushButton, fields.Single(f => f.Name == "submit").Kind);
    }

    [Fact]
    public void GetFormFieldAt_HitsTheFieldUnderThePoint()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));

        Assert.Equal("name", doc.GetFormFieldAt(0, NameX, NameY)?.Name);
        Assert.Equal("country", doc.GetFormFieldAt(0, CountryX, CountryY)?.Name);

        // Empty area of the page: the coordinate flip being wrong would still
        // hit *something*, so a miss here is a meaningful assertion.
        Assert.Null(doc.GetFormFieldAt(0, 500, 500));
    }

    [Fact]
    public void TypeIntoTextField_SaveAs_Reopen_ValuePersists()
    {
        string saved = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf")))
            {
                Assert.True(doc.FormClick(0, NameX, NameY), "click did not land on the text field");

                foreach (char c in "Abc")
                {
                    Assert.True(doc.FormChar(0, c), $"PDFium rejected the character '{c}'");
                }

                doc.FormKillFocus();
                Assert.Equal("Abc", doc.GetFormFieldValue(0, "name"));

                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);
            Assert.Equal("Abc", reopened.GetFormFieldValue(0, "name"));
        }
        finally
        {
            if (File.Exists(saved)) { File.Delete(saved); }
        }
    }

    [Fact]
    public void SaveWithFocusStillAlive_DoesNotLoseTheTypedValue()
    {
        // The realistic failure: the user types and hits Ctrl+S without
        // clicking away. SaveAs must kill focus itself, or the edit is lost.
        string saved = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf")))
            {
                doc.FormClick(0, NameX, NameY);
                foreach (char c in "Zed")
                {
                    doc.FormChar(0, c);
                }
                doc.SaveAs(saved); // deliberately no FormKillFocus() first
            }

            using var reopened = PdfDocument.Open(saved);
            Assert.Equal("Zed", reopened.GetFormFieldValue(0, "name"));
        }
        finally
        {
            if (File.Exists(saved)) { File.Delete(saved); }
        }
    }

    [Fact]
    public void Checkbox_Toggles_AndRoundTrips()
    {
        string saved = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf")))
            {
                Assert.Equal("Off", doc.GetFormFieldValue(0, "agree"));

                doc.FormClick(0, AgreeX, AgreeY);
                doc.FormKillFocus();

                Assert.Equal("On", doc.GetFormFieldValue(0, "agree"));
                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);
            Assert.Equal("On", reopened.GetFormFieldValue(0, "agree"));
        }
        finally
        {
            if (File.Exists(saved)) { File.Delete(saved); }
        }
    }

    [Fact]
    public void ComboBox_ReportsItsValue()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));

        // The fixture ships /V (UK).
        Assert.Equal("UK", doc.GetFormFieldValue(0, "country"));
    }

    [Fact]
    public void PushButton_HasNoTextEntry()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));
        var submit = doc.GetFormFieldAt(0, SubmitX, SubmitY);

        Assert.NotNull(submit);
        Assert.Equal(FormFieldKind.PushButton, submit.Kind);
        Assert.False(submit.AcceptsText);
    }

    [Fact]
    public void FieldHighlight_IsDrawnByTheFormPass()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));
        var bmp = doc.RenderPage(0, 1.0f);

        // FPDF_FFLDraw washes every field with the highlight colour. The four
        // widget rects total ~16k px, so a page that only rendered its three
        // text labels would come in an order of magnitude lower.
        Assert.True(PixelAssert.CountNonWhite(bmp) > 10000,
            $"form pass did not draw field highlights: {PixelAssert.CountNonWhite(bmp)} non-white pixels");
    }

    [Fact]
    public void TypedValue_IsActuallyRendered()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));
        int before = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));

        doc.FormClick(0, NameX, NameY);
        foreach (char c in "WWWWWWWWWWWWWW")
        {
            doc.FormChar(0, c);
        }
        doc.FormKillFocus();

        int after = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));

        // Counts dark glyph pixels, not non-white: the field is already washed
        // with the highlight colour, so non-white is saturated before typing
        // and would report no change even when the text renders correctly.
        //
        // Proves the FPDF_FFLDraw pass composes over the page render. Without
        // it the value is stored but invisible — the widget has no appearance
        // stream of its own, so FPDF_ANNOT alone draws nothing.
        Assert.True(after > before + 200, $"typed text did not reach the raster: {before} -> {after}");
    }

    [Fact]
    public void FormEdits_MarkTheDocumentDirty()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));
        Assert.False(doc.IsDirty);

        doc.FormClick(0, NameX, NameY);
        doc.FormChar(0, 'x');
        doc.FormKillFocus();

        // Comes through FFI_OnChange; without it Ctrl+S would look like a no-op
        // and the close prompt would never appear.
        Assert.True(doc.IsDirty);
    }

    [Fact]
    public void FormPageInvalidated_FiresWithThePageIndex()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));
        var invalidated = new List<int>();
        doc.FormPageInvalidated += invalidated.Add;

        doc.FormClick(0, NameX, NameY);
        doc.FormChar(0, 'q');

        // This is the signal the viewer uses to refresh stale tiles; if PDFium
        // never calls it, typing looks like nothing happened.
        Assert.Contains(0, invalidated);
    }

    // IsSamePlaceAs is what lets the viewer tell "click inside the field I am
    // already typing in" (move the caret) from "click on a different field"
    // (commit the old one first). Getting it wrong either strands focus on the
    // old widget — which is what made clicking a second field do nothing until
    // you pressed Escape — or resets the caret every time you click mid-text.

    [Fact]
    public void IsSamePlaceAs_IgnoresTheValue()
    {
        var before = Field(name: "email", value: "");
        var after = before with { Value = "typed while editing" };

        // Record equality would say false here, once per keystroke.
        Assert.True(before.IsSamePlaceAs(after));
    }

    [Fact]
    public void IsSamePlaceAs_SeparatesRadiosSharingOneName()
    {
        var first = Field(name: "choice", y: 100);
        var second = Field(name: "choice", y: 140);

        // Every radio in a group carries the group's name, so position is the
        // only thing telling two of them apart.
        Assert.False(first.IsSamePlaceAs(second));
    }

    [Fact]
    public void IsSamePlaceAs_SeparatesSameNameOnDifferentPages()
    {
        var onPage0 = Field(name: "signature", page: 0);
        var onPage1 = Field(name: "signature", page: 1);

        Assert.False(onPage0.IsSamePlaceAs(onPage1));
    }

    private static FormFieldInfo Field(string name, string value = "", int page = 0, double y = 100) =>
        new(page, FormFieldKind.Text, name, value, IsReadOnly: false, X: 50, Y: y, Width: 200, Height: 20);
}
