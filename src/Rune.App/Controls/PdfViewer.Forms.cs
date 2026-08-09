using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Rune.Engine;
using Rune.Styles;
using Windows.Foundation;
using Windows.System;

namespace Rune.Controls;

// Interactive form filling.
//
// Field geometry is prefetched onto the UI thread (the same trick PageText
// uses for selection) so pointer hit-testing is pure managed math: extracting
// it lazily on pointer contact would lose the race against the click itself,
// and a render-thread round-trip per mouse-move would be far too slow.
//
// Actual editing still goes through the render thread, because PDFium's form
// state is bound to the thread that created the environment.
public sealed partial class PdfViewer
{
    private readonly Dictionary<int, IReadOnlyList<FormFieldInfo>> _formFields = [];
    private readonly HashSet<int> _formFieldsRequested = [];

    /// <summary>The page whose field currently has keyboard focus, or -1.</summary>
    private int _formFocusPage = -1;

    /// <summary>
    /// The focused field itself, kept so a press can tell "same field, move the
    /// caret" from "different field, commit and move on". Only its identity and
    /// geometry are used, so a stale instance from a replaced cache is fine.
    /// </summary>
    private FormFieldInfo? _formFocusField;

    /// <summary>True when a form field is taking keystrokes, so navigation keys must not steal them.</summary>
    public bool IsFormFieldFocused => _formFocusPage >= 0;

    /// <summary>True when this document has fillable AcroForm fields.</summary>
    public bool HasFillableForm => _document?.HasFillableForm == true;

    /// <summary>True for XFA documents, which PDFium cannot fill.</summary>
    public bool IsXfaForm => _document?.FormKind == PdfFormKind.Xfa;

    // ---- Geometry prefetch ----

    private async void EnsureFormFields(int pageIndex)
    {
        if (_document is not { HasFillableForm: true } document || !_formFieldsRequested.Add(pageIndex))
        {
            return;
        }

        try
        {
            var fields = await _scheduler.RunAsync(
                PdfWorkPriority.Interactive, () => document.GetFormFields(pageIndex));
            if (_document == document)
            {
                _formFields[pageIndex] = fields;
            }
        }
        catch
        {
            _formFieldsRequested.Remove(pageIndex); // retry on the next prefetch
        }
    }

    private void ClearFormState()
    {
        _formFields.Clear();
        _formFieldsRequested.Clear();
        _formFocusPage = -1;
        _formFocusField = null;
    }

    /// <summary>
    /// Re-reads a page's field geometry after a value changed.
    ///
    /// The old rects are deliberately left in place until the new ones arrive.
    /// Dropping them first opened a window — one per keystroke, since every edit
    /// lands here — in which <see cref="TryHandleFormPress"/> found no geometry,
    /// returned false, and let the click fall through to text selection. The
    /// symptom was that clicking a second field did nothing until you stopped
    /// typing and pressed Escape.
    /// </summary>
    private void InvalidateFormFields(int pageIndex)
    {
        _formFieldsRequested.Remove(pageIndex);
        EnsureFormFields(pageIndex); // overwrites _formFields[pageIndex] on arrival
    }

    // ---- Pointer ----

    /// <summary>
    /// Handles a press that landed on a form field. Returns true when the
    /// click was consumed, so links and text selection stay out of the way.
    /// </summary>
    private bool TryHandleFormPress(Point docPoint)
    {
        if (_layout is null || _document is not { HasFillableForm: true } document)
        {
            return false;
        }

        int page = _layout.PageAt(docPoint.Y);
        if (!_formFields.TryGetValue(page, out var fields))
        {
            EnsureFormFields(page);
            return false; // geometry not in yet; this click falls through to selection
        }

        var local = ToPageLocal(page, docPoint);
        var hit = fields.FirstOrDefault(f =>
            local.X >= f.X && local.X <= f.X + f.Width &&
            local.Y >= f.Y && local.Y <= f.Y + f.Height);

        if (hit is null)
        {
            // A click on the page but off every field commits the current edit,
            // matching what every other PDF reader does.
            if (IsFormFieldFocused)
            {
                KillFormFocus();
            }
            return false;
        }

        // Moving to a *different* field has to commit the old one first. PDFium
        // keeps the in-progress value in the focused widget, so without this the
        // new click can be swallowed by the widget that still holds focus — and
        // whatever was typed goes with it. Clicking inside the field you are
        // already editing must NOT do this: there the click is just moving the
        // caret, and committing would throw the caret back to the start.
        if (IsFormFieldFocused && !IsFocusedField(hit))
        {
            KillFormFocus();
        }

        // Take keyboard focus before the async hop so the very next keystroke
        // is already routed to the field.
        _formFocusPage = page;
        _formFocusField = hit;
        Focus(FocusState.Pointer);
        DispatchFormClick(document, page, local.X, local.Y);
        return true;
    }

    /// <summary>Whether a field is the one that currently has focus.</summary>
    private bool IsFocusedField(FormFieldInfo field) =>
        _formFocusField is { } focused && focused.IsSamePlaceAs(field);

    private async void DispatchFormClick(PdfDocument document, int page, double localX, double localY)
    {
        try
        {
            await _scheduler.RunAsync(
                PdfWorkPriority.Interactive, () => document.FormClick(page, localX, localY));
        }
        catch
        {
            return; // document swapped or closed under us
        }

        if (_document == document)
        {
            // Checkboxes and radios change appearance on the click itself.
            InvalidateFormFields(page);
        }
    }

    // ---- Keyboard ----

    /// <summary>
    /// Sends a typed character to the focused field. Returns true when consumed.
    /// </summary>
    private bool TryHandleFormCharacter(char character)
    {
        if (!IsFormFieldFocused || _document is not { HasFillableForm: true } document)
        {
            return false;
        }
        // Control characters arrive through KeyDown instead.
        if (char.IsControl(character))
        {
            return false;
        }

        DispatchFormEdit(document, _formFocusPage, page => document.FormChar(page, character));
        return true;
    }

    /// <summary>
    /// Sends editing and navigation keys to the focused field. Returns true when
    /// consumed, which is what stops arrows from scrolling the document instead.
    /// </summary>
    private bool TryHandleFormKey(VirtualKey key)
    {
        if (!IsFormFieldFocused || _document is not { HasFillableForm: true } document)
        {
            return false;
        }

        switch (key)
        {
            case VirtualKey.Escape:
            case VirtualKey.Enter:
            case VirtualKey.Tab:
                // Commit and hand focus back to the document.
                KillFormFocus();
                return key != VirtualKey.Tab; // let Tab move on normally

            case VirtualKey.Back:
            case VirtualKey.Delete:
            case VirtualKey.Left:
            case VirtualKey.Right:
            case VirtualKey.Home:
            case VirtualKey.End:
            case VirtualKey.Up:
            case VirtualKey.Down:
                DispatchFormEdit(document, _formFocusPage, page => document.FormKeyDown(page, (int)key));
                return true;

            default:
                return false;
        }
    }

    private async void DispatchFormEdit(PdfDocument document, int page, Func<int, bool> edit)
    {
        try
        {
            await _scheduler.RunAsync(PdfWorkPriority.Interactive, () => edit(page));
        }
        catch
        {
            return;
        }

        if (_document != document)
        {
            return;
        }
        InvalidateFormFields(page);
        // Same signal annotation edits raise, so the tab's dirty marker, the
        // close prompt and Ctrl+S all light up without a second concept.
        DocumentEdited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Commits the focused field's edit and drops focus.
    ///
    /// Must happen before saving and before the document closes: PDFium holds
    /// the in-progress value in the focused widget, so committing late means
    /// the user's last keystrokes are silently discarded.
    /// </summary>
    public void KillFormFocus()
    {
        int page = _formFocusPage;
        _formFocusPage = -1;
        _formFocusField = null;

        if (page < 0 || _document is not { HasFillableForm: true } document)
        {
            return;
        }

        _ = DispatchKillFocus(document, page);
    }

    private async Task DispatchKillFocus(PdfDocument document, int page)
    {
        try
        {
            await _scheduler.RunAsync(PdfWorkPriority.Interactive, () =>
            {
                document.FormKillFocus();
                return true;
            });
        }
        catch
        {
            return;
        }

        if (_document == document)
        {
            InvalidateFormFields(page);
        }
    }

    // ---- Painting ----

    /// <summary>
    /// Outlines every field on a page.
    ///
    /// PDFium fills widgets during <c>FPDF_FFLDraw</c> and draws no edge, so
    /// fields that touch — a name box sitting directly on an email box — render
    /// as one undivided wash. The border is drawn here rather than asked of
    /// PDFium because the geometry is already cached on the UI thread for
    /// hit-testing, which makes this pure managed drawing with no render-thread
    /// work and nothing to invalidate.
    ///
    /// Field rects describe the file, so they go through the view rotation to
    /// reach the drawn box — the same path selection and search highlights take.
    /// PDFium's own widget fill already receives the rotation, so the two agree.
    /// </summary>
    private void DrawFormFieldBorders(CanvasDrawingSession session, int pageIndex, DipRect pageRect)
    {
        if (!_formFields.TryGetValue(pageIndex, out var fields))
        {
            return;
        }

        var rotation = RotationFor(pageIndex);

        var border = RuneColors.FormFieldBorder(_nightMode);
        var focusBorder = RuneColors.FormFieldFocusBorder(_nightMode);
        var readOnlyBorder = RuneColors.FormFieldReadOnlyBorder(_nightMode);

        foreach (var field in fields)
        {
            // Pushbuttons already draw their own bevel; outlining them adds a
            // second, competing edge.
            if (field.Kind == FormFieldKind.PushButton)
            {
                continue;
            }

            var drawn = rotation.ToDrawn(field.X, field.Y, field.Width, field.Height);
            var rect = new Rect(
                pageRect.X + drawn.X * _zoom,
                pageRect.Y + drawn.Y * _zoom,
                Math.Max(1, drawn.Width * _zoom),
                Math.Max(1, drawn.Height * _zoom));

            bool focused = IsFocusedField(field);
            var color = field.IsReadOnly ? readOnlyBorder : focused ? focusBorder : border;
            session.DrawRectangle(rect, color, focused ? 2f : 1f);
        }
    }

    // ---- Repaint ----

    /// <summary>
    /// PDFium reported a widget's appearance changed. Refresh in place rather
    /// than calling InvalidatePage: that drops the tiles AND the preview, so
    /// every keystroke would flash the page white and then blur-to-crisp. Here
    /// the existing tiles stay on screen until their replacements arrive.
    /// </summary>
    private void OnFormPageInvalidated(int pageIndex)
    {
        if (!_dispatcher.TryEnqueue(() =>
        {
            if (_document is not null)
            {
                RefreshPageTiles(pageIndex);
            }
        }))
        {
            // Dispatcher is shutting down; nothing to repaint.
        }
    }
}
