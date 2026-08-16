using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Rune.Engine;
using Rune.Styles;
using Windows.Foundation;
using Windows.System;

namespace Rune.Controls;

/// <summary>A request to restyle one form field's typed text, with what it declares today.</summary>
public sealed record FormAppearanceRequest(int PageIndex, string FieldName, FieldAppearance Current);

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

        // Press only. The release comes from Canvas_PointerReleased and anything
        // in between is a drag, which is the only way PDFium's edit control will
        // select text: it starts the selection on the button-down and extends it
        // on each move. Sending a down and an up at one point, as this used to,
        // places the caret and can never select anything.
        _formPointerDown = true;
        DispatchFormPointer(document, page, local.X, local.Y, FormPointer.Down);
        return true;
    }

    private bool _formPointerDown;

    /// <summary>True while a press that landed on a field has not been released.</summary>
    private bool IsDraggingInFormField => _formPointerDown;

    private enum FormPointer { Down, Move, Up }

    /// <summary>Extends the selection while the button is held inside a field.</summary>
    private void UpdateFormDrag(Point docPoint)
    {
        if (!_formPointerDown || _layout is null || _document is not { HasFillableForm: true } document)
        {
            return;
        }
        var local = ToPageLocal(_formFocusPage, docPoint);
        DispatchFormPointer(document, _formFocusPage, local.X, local.Y, FormPointer.Move);
    }

    /// <summary>Ends the drag. The selection PDFium built survives the release.</summary>
    private void CommitFormDrag(Point docPoint)
    {
        if (!_formPointerDown)
        {
            return;
        }
        _formPointerDown = false;

        if (_layout is null || _document is not { HasFillableForm: true } document)
        {
            return;
        }
        var local = ToPageLocal(_formFocusPage, docPoint);
        DispatchFormPointer(document, _formFocusPage, local.X, local.Y, FormPointer.Up);
    }

    /// <summary>Whether a field is the one that currently has focus.</summary>
    private bool IsFocusedField(FormFieldInfo field) =>
        _formFocusField is { } focused && focused.IsSamePlaceAs(field);

    // ---- text appearance ----

    /// <summary>Raised when the user asks to restyle a field's typed text.</summary>
    public event EventHandler<FormAppearanceRequest>? FormAppearanceRequested;

    /// <summary>
    /// The text-accepting field at a page-local point, from the geometry already
    /// cached for drawing borders. Pure lookup, no PDFium call: this runs while
    /// a context menu is being built.
    /// </summary>
    private FormFieldInfo? TextFieldAt(int page, double localX, double localY)
    {
        if (_document is not { HasFillableForm: true } || !_formFields.TryGetValue(page, out var fields))
        {
            return null;
        }
        return fields.FirstOrDefault(f =>
            f.AcceptsText &&
            localX >= f.X && localX <= f.X + f.Width &&
            localY >= f.Y && localY <= f.Y + f.Height);
    }

    /// <summary>
    /// Asks the shell to restyle a field, handing it whatever the field declares
    /// today so the dialog opens on the current values rather than on a guess.
    /// </summary>
    private async void RequestFieldAppearance(int page, string name)
    {
        if (_document is not { } document)
        {
            return;
        }

        FieldAppearance? current;
        try
        {
            current = await _scheduler.RunAsync(
                PdfWorkPriority.Interactive, () => document.GetFieldAppearance(page, name));
        }
        catch
        {
            return;
        }
        if (_document != document)
        {
            return;
        }

        FormAppearanceRequested?.Invoke(this,
            new FormAppearanceRequest(page, name, current ?? FieldAppearance.Default));
    }

    /// <summary>
    /// Applies a new text appearance and makes it undoable. Returns false when
    /// the field has no <c>/DA</c> of its own to rewrite, which the shell reports
    /// rather than leaving the user wondering why nothing changed.
    /// </summary>
    public async Task<bool> ApplyFieldAppearanceAsync(int page, string name, FieldAppearance wanted)
    {
        if (_document is not { } document)
        {
            return false;
        }

        (FieldAppearance? Previous, bool Applied) result;
        try
        {
            result = await _scheduler.RunAsync(PdfWorkPriority.Interactive, () =>
            {
                // Read before writing: undo restores the string that was there,
                // not a reconstruction of it.
                var before = document.GetFieldAppearance(page, name);
                return (before, document.SetFieldAppearance(page, name, wanted));
            });
        }
        catch
        {
            return false;
        }
        if (!result.Applied || _document != document)
        {
            return false;
        }

        // The widget's appearance was rebuilt, so every cached tile of this page
        // is stale.
        InvalidatePage(page);
        DocumentEdited?.Invoke(this, EventArgs.Empty);

        if (result.Previous is { } previous)
        {
            AnnotationEdited?.Invoke(this, new AnnotationEditEventArgs
            {
                Label = "text appearance",
                PageIndex = page,
                UndoAction = d => d.SetFieldAppearance(page, name, previous),
                RedoAction = d => d.SetFieldAppearance(page, name, wanted),
            });
        }
        return true;
    }

    /// <summary>
    /// Sends one pointer event to the focused field's page.
    ///
    /// Order matters and is guaranteed: the scheduler runs same-priority ops
    /// first-in-first-out on its single thread, so a down, its moves and its up
    /// reach PDFium in the order the user made them even though each is queued
    /// from a separate async call.
    /// </summary>
    private async void DispatchFormPointer(
        PdfDocument document, int page, double localX, double localY, FormPointer which)
    {
        int modifier = FormModifiersNow();
        try
        {
            await _scheduler.RunAsync(PdfWorkPriority.Interactive, () => which switch
            {
                FormPointer.Down => document.FormPointerDown(page, localX, localY, modifier),
                FormPointer.Move => document.FormPointerMove(page, localX, localY, modifier),
                _ => document.FormPointerUp(page, localX, localY, modifier),
            });
        }
        catch
        {
            return; // document swapped or closed under us
        }

        // Checkboxes and radios change appearance on the release, so the cached
        // geometry is only stale once the gesture is over. Refreshing on every
        // move of a drag would be a round-trip per pointer event.
        if (which == FormPointer.Up && _document == document)
        {
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

        int modifier = FormModifiersNow();
        bool control = (modifier & PdfDocument.FormModifiers.Control) != 0;

        switch (key)
        {
            case VirtualKey.Escape:
            case VirtualKey.Enter:
            case VirtualKey.Tab:
                // Commit and hand focus back to the document.
                KillFormFocus();
                return key != VirtualKey.Tab; // let Tab move on normally

            // Backspace is NOT a key as far as PDFium's edit control is
            // concerned. It handles Delete and the arrows in its key handler and
            // backspace in its CHARACTER handler, so FORM_OnKeyDown(8) returns
            // false and changes nothing, which is exactly how it behaved here:
            // Delete worked, backspace did nothing at all.
            //
            // It is routed from KeyDown rather than from CharacterReceived on
            // purpose. KeyDown certainly fires for it; whether a WM_CHAR follows
            // is not ours to rely on, and TryHandleFormCharacter drops control
            // characters, so this cannot delete twice.
            case VirtualKey.Back:
                DispatchFormEdit(document, _formFocusPage,
                    page => document.FormChar(page, Backspace, modifier));
                return true;

            // Ctrl+A likewise arrives as a control character, not as a letter
            // with a flag: FORM_OnKeyDown(A, ctrl) is refused.
            case (VirtualKey)0x41 when control: // A
                DispatchFormEdit(document, _formFocusPage,
                    page => document.FormChar(page, SelectAll, modifier));
                return true;

            case VirtualKey.Delete:
            case VirtualKey.Left:
            case VirtualKey.Right:
            case VirtualKey.Home:
            case VirtualKey.End:
            case VirtualKey.Up:
            case VirtualKey.Down:
                // The modifier is what makes shift-arrow SELECT rather than just
                // move the caret. Passing 0 here, as this did, leaves a field
                // that cannot be selected from the keyboard at all.
                DispatchFormEdit(document, _formFocusPage,
                    page => document.FormKeyDown(page, (int)key, modifier));
                return true;

            default:
                return false;
        }
    }

    /// <summary>ASCII backspace, which is how PDFium's edit control wants it.</summary>
    private const int Backspace = 8;

    /// <summary>Ctrl+A as its control character.</summary>
    private const int SelectAll = 1;

    /// <summary>
    /// The modifier keys held right now, in PDFium's flags. Read from the live
    /// keyboard state because <c>KeyRoutedEventArgs</c> carries no modifier set.
    /// </summary>
    private static int FormModifiersNow()
    {
        int flags = 0;
        if (IsHeld(VirtualKey.Shift)) { flags |= PdfDocument.FormModifiers.Shift; }
        if (IsHeld(VirtualKey.Control)) { flags |= PdfDocument.FormModifiers.Control; }
        if (IsHeld(VirtualKey.Menu)) { flags |= PdfDocument.FormModifiers.Alt; }
        return flags;

        static bool IsHeld(VirtualKey key) => Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
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
