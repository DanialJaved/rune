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
    private bool TryHandleFormPress(Point docPoint, bool touch)
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

        // Form fields are often a few points tall, which a mouse hits exactly
        // and a fingertip does not. The slop is zero for a mouse, so nothing
        // about the existing behaviour moves.
        double slop = TouchSlopPt(touch);
        var local = ToPageLocal(page, docPoint);
        var hit = fields.FirstOrDefault(f =>
            local.X >= f.X - slop && local.X <= f.X + f.Width + slop &&
            local.Y >= f.Y - slop && local.Y <= f.Y + f.Height + slop);

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

        // A PDF form field is drawn by PDFium onto the Win2D canvas, so as far
        // as Windows is concerned nothing that accepts text just took focus and
        // the touch keyboard never appears on its own. On a tablet that made
        // form filling impossible: the field takes the caret and there is no way
        // to put a character in it. Asking for the pane explicitly is the fix.
        if (touch)
        {
            ShowTouchKeyboard();
        }

        // Press only. The release comes from Canvas_PointerReleased and anything
        // in between is a drag, which is the only way PDFium's edit control will
        // select text: it starts the selection on the button-down and extends it
        // on each move. Sending a down and an up at one point, as this used to,
        // places the caret and can never select anything.
        _formPointerDown = true;
        _lastFormDragPoint = docPoint;
        DispatchFormPointer(document, page, local.X, local.Y, FormPointer.Down);
        return true;
    }

    private bool _formPointerDown;

    /// <summary>True while a press that landed on a field has not been released.</summary>
    private bool IsDraggingInFormField => _formPointerDown;

    private enum FormPointer { Down, Move, Up }

    /// <summary>
    /// Where the drag last was, so a gesture the system cancels can still send
    /// PDFium the button-up it is waiting for. A cancel carries no position.
    /// </summary>
    private Point _lastFormDragPoint;

    /// <summary>Extends the selection while the button is held inside a field.</summary>
    private void UpdateFormDrag(Point docPoint)
    {
        if (!_formPointerDown || _layout is null || _document is not { HasFillableForm: true } document)
        {
            return;
        }
        _lastFormDragPoint = docPoint;
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

    // ---- touch keyboard ----
    //
    // A PDF form field is not a XAML control. It is pixels PDFium drew onto the
    // Win2D canvas, and the caret inside it is PDFium's too, so Windows sees no
    // text input take focus and never raises the soft keyboard. With a hardware
    // keyboard that costs nothing. On a tablet it meant a field could be focused
    // and then never typed into, which is the whole feature gone.

    private Windows.UI.ViewManagement.InputPane? _inputPane;

    /// <summary>
    /// The soft keyboard for this window. A desktop app has no CoreWindow, so
    /// the WinRT type has to be asked for by HWND — the same reason printing
    /// goes through PrintManagerInterop and sharing through
    /// DataTransferManagerInterop.
    /// </summary>
    private Windows.UI.ViewManagement.InputPane? TouchKeyboard()
    {
        if (_inputPane is not null)
        {
            return _inputPane;
        }
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!);
            _inputPane = Windows.UI.ViewManagement.InputPaneInterop.GetForWindow(hwnd);
        }
        catch
        {
            _inputPane = null; // no pane here; a hardware keyboard still works
        }
        return _inputPane;
    }

    private void ShowTouchKeyboard()
    {
        if (TouchKeyboard() is not { } pane)
        {
            return;
        }
        // GetForWindow hands every tab's viewer the same instance, so the pair
        // keeps this one subscribed exactly once however often a field is tapped.
        pane.Showing -= InputPane_Showing;
        pane.Showing += InputPane_Showing;
        // Returns true when Windows accepts the request. Whether the pane is
        // then actually painted is the system's call, not ours: with a hardware
        // keyboard attached and the device not in tablet posture it accepts and
        // stays hidden, which is correct and is what happens on a desktop.
        pane.TryShow();
    }

    private void HideTouchKeyboard()
    {
        if (_inputPane is not { } pane)
        {
            return;
        }
        pane.Showing -= InputPane_Showing;
        pane.TryHide();
    }

    /// <summary>
    /// The keyboard covers the bottom of a tablet screen, and a field underneath
    /// it is a field being typed into blind. Scroll it clear.
    /// </summary>
    private void InputPane_Showing(
        Windows.UI.ViewManagement.InputPane sender,
        Windows.UI.ViewManagement.InputPaneVisibilityEventArgs args)
    {
        if (_formFocusField is not { } field || _layout is null || _pageSizes.Length == 0)
        {
            return;
        }

        // The field's own corners, through the view rotation, into the viewport.
        var a = ToDocumentPoint(_formFocusPage, field.X, field.Y);
        var b = ToDocumentPoint(_formFocusPage, field.X + field.Width, field.Y + field.Height);
        double bottom = Math.Max(a.Y, b.Y) - Scroller.VerticalOffset;

        double clear = args.OccludedRect.Y - 16;
        if (clear <= 0 || bottom <= clear)
        {
            return;
        }

        Scroller.ChangeView(null, Scroller.VerticalOffset + (bottom - clear), null, false);
        args.EnsuredFocusedElementInView = true;
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
    /// Restyles whatever field has focus right now. Returns false when there is
    /// none, when it has no <c>/DA</c> to rewrite, or when the change was
    /// rejected — the three cases the caller treats identically, because the
    /// user pressed a key and nothing happened.
    ///
    /// This is the keyboard's way in: Ctrl+B and Ctrl+Shift+&gt; act on the field
    /// under the caret, where the restyle dialog acts on one picked by name.
    /// </summary>
    public async Task<bool> RestyleFocusedFieldAsync(Func<FieldAppearance, FieldAppearance> change)
    {
        if (_formFocusField is not { } field || _document is not { } document)
        {
            return false;
        }

        int page = _formFocusPage;
        string name = field.Name;

        FieldAppearance? current;
        try
        {
            current = await _scheduler.RunAsync(
                PdfWorkPriority.Interactive, () => document.GetFieldAppearance(page, name));
        }
        catch
        {
            return false;
        }
        if (_document != document || current is not { } appearance)
        {
            return false;
        }

        bool applied = await ApplyFieldAppearanceAsync(page, name, change(appearance));

        // Rewriting a /DA rebuilds the page's form state, which means committing
        // and dropping the focused widget first — so a restyle from the keyboard
        // would otherwise throw the caret out of the field the user is still
        // filling in. Put it back, whether or not the change took: they pressed
        // a formatting key, not Tab.
        RefocusField(page, field);
        return applied;
    }

    /// <summary>
    /// Re-establishes the caret in a field by replaying a click at its centre,
    /// which is the only way PDFium's form layer takes focus. Silent if the
    /// field is no longer there.
    /// </summary>
    private void RefocusField(int page, FormFieldInfo field)
    {
        if (_document is not { HasFillableForm: true } document)
        {
            return;
        }

        double centreX = field.X + (field.Width / 2);
        double centreY = field.Y + (field.Height / 2);

        _formFocusPage = page;
        _formFocusField = field;
        Focus(FocusState.Programmatic);

        DispatchFormPointer(document, page, centreX, centreY, FormPointer.Down);
        DispatchFormPointer(document, page, centreX, centreY, FormPointer.Up);
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

        // Nothing is taking typed characters any more, so the soft keyboard has
        // no business covering a third of the page.
        HideTouchKeyboard();

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
