using Rune.Engine;

namespace Rune.Tests;

/// <summary>
/// Editing inside a focused text field: deleting, and selecting.
///
/// Every one of these was broken in the app until v0.7.0, and each for a
/// different reason, so they are pinned separately. There is no API for reading
/// a field's selection back, so a selection is detected the way a user would
/// notice one: type a character and see whether it REPLACED anything.
///
/// Field centres come from form.pdf's /Rect values, as in <see cref="FormFillTests"/>.
/// </summary>
public class FormEditingTests
{
    private const double NameX = 325, NameY = 80;

    /// <summary>Virtual key codes, which PDFium's FWL_VKEY_* values happen to match.</summary>
    private const int VkHome = 0x24, VkEnd = 0x23, VkLeft = 0x25, VkRight = 0x27, VkDelete = 0x2E;

    private static PdfDocument OpenWithTypedName(string text)
    {
        var doc = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));
        Assert.True(doc.FormClick(0, NameX, NameY), "click did not land on the text field");
        foreach (char c in text)
        {
            doc.FormChar(0, c);
        }
        return doc;
    }

    private static string ValueAfterCommit(PdfDocument doc)
    {
        doc.FormKillFocus();
        return doc.GetFormFieldValue(0, "name") ?? "";
    }

    // ---- deleting ----

    [Fact]
    public void Backspace_GoesThroughTheCharacterRoute_NotTheKeyRoute()
    {
        // The whole bug in one assertion. PDFium's edit control handles Delete
        // and the arrows in its key handler, but backspace in its CHARACTER
        // handler, so sending it as a virtual key is refused and silently does
        // nothing. Rune sent it as a key, which is why Delete worked and
        // backspace did not.
        using var doc = OpenWithTypedName("Abcd");

        Assert.False(doc.FormKeyDown(0, 8), "FORM_OnKeyDown accepted backspace; the routing below may be unnecessary now");
        Assert.Equal("Abcd", ValueAfterCommit(doc));
    }

    [Fact]
    public void Backspace_AsACharacter_DeletesBackwards()
    {
        using var doc = OpenWithTypedName("Abcd");

        Assert.True(doc.FormChar(0, 8));
        Assert.Equal("Abc", ValueAfterCommit(doc));
    }

    [Fact]
    public void Delete_StillWorksThroughTheKeyRoute()
    {
        using var doc = OpenWithTypedName("Abcd");

        doc.FormKeyDown(0, VkHome);
        Assert.True(doc.FormKeyDown(0, VkDelete));
        Assert.Equal("bcd", ValueAfterCommit(doc));
    }

    // ---- selecting ----

    [Fact]
    public void ShiftArrow_Selects_AndTypingReplacesTheSelection()
    {
        using var doc = OpenWithTypedName("Abcd");

        doc.FormKeyDown(0, VkHome);
        doc.FormKeyDown(0, VkRight, PdfDocument.FormModifiers.Shift);
        doc.FormKeyDown(0, VkRight, PdfDocument.FormModifiers.Shift);
        doc.FormChar(0, 'X');

        // Without the modifier the arrows only move the caret, and this reads
        // "AbXcd" — which is exactly what the app did before the fix.
        Assert.Equal("Xcd", ValueAfterCommit(doc));
    }

    [Fact]
    public void ShiftEnd_SelectsToTheEnd()
    {
        using var doc = OpenWithTypedName("Abcd");

        doc.FormKeyDown(0, VkHome);
        doc.FormKeyDown(0, VkRight);
        doc.FormKeyDown(0, VkEnd, PdfDocument.FormModifiers.Shift);
        doc.FormChar(0, 'Z');

        Assert.Equal("AZ", ValueAfterCommit(doc));
    }

    [Fact]
    public void ArrowWithoutShift_MovesTheCaretAndSelectsNothing()
    {
        using var doc = OpenWithTypedName("Abcd");

        doc.FormKeyDown(0, VkHome);
        doc.FormKeyDown(0, VkRight);
        doc.FormKeyDown(0, VkRight);
        doc.FormChar(0, 'X');

        Assert.Equal("AbXcd", ValueAfterCommit(doc));
    }

    [Fact]
    public void PressMoveRelease_SelectsTheTextItDraggedOver()
    {
        using var doc = OpenWithTypedName("Abcd");

        // The field spans x 200..450 and the text starts at its left edge, so a
        // drag from just inside it rightwards covers the value.
        doc.FormPointerDown(0, 205, NameY);
        doc.FormPointerMove(0, 220, NameY);
        doc.FormPointerMove(0, 245, NameY);
        doc.FormPointerUp(0, 245, NameY);
        doc.FormChar(0, 'X');

        Assert.Equal("X", ValueAfterCommit(doc));
    }

    [Fact]
    public void PressAndReleaseWithNoMove_PlacesTheCaretWithoutSelecting()
    {
        // The old FormClick path, which is why the mouse could never select:
        // a down and an up at one point is a caret move, not a drag.
        using var doc = OpenWithTypedName("Abcd");

        doc.FormPointerDown(0, 205, NameY);
        doc.FormPointerUp(0, 205, NameY);
        doc.FormChar(0, 'X');

        Assert.Equal("XAbcd", ValueAfterCommit(doc));
    }

    [Fact]
    public void ControlA_SelectsEverything()
    {
        using var doc = OpenWithTypedName("Abcd");

        // As a control character. FORM_OnKeyDown(A, ctrl) is refused, which is
        // the trap: the obvious routing looks right and does nothing.
        Assert.True(doc.FormChar(0, 1, PdfDocument.FormModifiers.Control));
        doc.FormChar(0, 'X');

        Assert.Equal("X", ValueAfterCommit(doc));
    }

    [Fact]
    public void ControlA_AsAKeyCode_IsRefused()
    {
        using var doc = OpenWithTypedName("Abcd");

        Assert.False(doc.FormKeyDown(0, 0x41, PdfDocument.FormModifiers.Control));
        doc.FormChar(0, 'X');

        Assert.Equal("AbcdX", ValueAfterCommit(doc));
    }

    // ---- the edit still has to survive ----

    [Fact]
    public void EditedValue_SurvivesSaveAndReopen()
    {
        string saved = Path.Combine(Path.GetTempPath(), $"rune-formedit-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = OpenWithTypedName("Abcd"))
            {
                doc.FormChar(0, 8);                                     // backspace
                doc.FormKeyDown(0, VkHome);
                doc.FormKeyDown(0, VkRight, PdfDocument.FormModifiers.Shift);
                doc.FormChar(0, 'Z');                                   // replaces "A"
                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);
            Assert.Equal("Zbc", reopened.GetFormFieldValue(0, "name"));
        }
        finally
        {
            if (File.Exists(saved)) { File.Delete(saved); }
        }
    }
}
