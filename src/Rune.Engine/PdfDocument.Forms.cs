using Rune.PdfiumInterop;

namespace Rune.Engine;

/// <summary>What kind of interactive form a document carries.</summary>
public enum PdfFormKind
{
    /// <summary>No form fields at all.</summary>
    None,
    /// <summary>A standard AcroForm — fillable.</summary>
    AcroForm,
    /// <summary>An XFA form. PDFium cannot fill these; the UI must say so rather than fail silently.</summary>
    Xfa,
}

/// <summary>A form field under the cursor. Geometry is in page points, top-left origin.</summary>
public sealed record FormFieldInfo(
    int PageIndex,
    FormFieldKind Kind,
    string Name,
    string Value,
    bool IsReadOnly,
    double X,
    double Y,
    double Width,
    double Height)
{
    /// <summary>Pushbuttons and read-only fields accept clicks but never text.</summary>
    public bool AcceptsText => !IsReadOnly && Kind is FormFieldKind.Text or FormFieldKind.ComboBox;

    /// <summary>
    /// Whether this and <paramref name="other"/> are the same widget on the page.
    ///
    /// Compared by page, name and position rather than by record equality:
    /// <see cref="Value"/> changes on every keystroke, so the generated equality
    /// would report "different field" mid-edit — and the viewer uses this to
    /// decide whether a click is moving the caret inside the field being typed
    /// in or leaving it for another, which must survive typing to be any use.
    /// Position is part of it because every radio button in a group shares one
    /// name.
    /// </summary>
    public bool IsSamePlaceAs(FormFieldInfo other) =>
        PageIndex == other.PageIndex
        && Name == other.Name
        && Math.Abs(X - other.X) < 0.5
        && Math.Abs(Y - other.Y) < 0.5;
}

public enum FormFieldKind
{
    Unknown = 0,
    PushButton = 1,
    CheckBox = 2,
    RadioButton = 3,
    ComboBox = 4,
    ListBox = 5,
    Text = 6,
    Signature = 7,
}

// The form-fill half of PdfDocument.
//
// PDFium drives form widgets through an environment handle that is bound to
// both the document and the thread that created it, and it edits state living
// in the FPDF_PAGE object — which is why this sits on top of the page cache
// (see PdfDocument.PageCache.cs) rather than loading pages per call.
//
// Coordinates: every method here takes page-local points with a TOP-LEFT
// origin, the same space TextRect and AnnotationInfo use, and converts to
// PDFium's bottom-left page space internally. Callers never see the flip.
public sealed partial class PdfDocument
{
    private PdfiumFormEnvironment? _formEnv;

    /// <summary>Raised when PDFium reports a form field's appearance changed and needs re-rendering.</summary>
    public event Action<int>? FormPageInvalidated;

    public PdfFormKind FormKind { get; private set; } = PdfFormKind.None;

    /// <summary>True when this document has fillable AcroForm fields.</summary>
    public bool HasFillableForm => FormKind == PdfFormKind.AcroForm && _formEnv is not null;

    /// <summary>Caller must hold the lock. Called once from Open.</summary>
    private void InitializeFormsLocked()
    {
        _formEnv = PdfiumFormEnvironment.TryCreate(
            _handle,
            resolvePage: ResolveFormPageLocked,
            onInvalidate: OnFormInvalidate,
            onChange: () => IsDirty = true);

        FormKind = _formEnv is null ? PdfFormKind.None
                 : _formEnv.IsXfa ? PdfFormKind.Xfa
                 : PdfFormKind.AcroForm;

        if (_formEnv is { IsXfa: false })
        {
            // A light blue wash over fillable fields, as Acrobat and Preview do.
            //
            // The channel order is BGR, not the RGB the PDFium header implies:
            // passing 0x3399FF renders peach (verified on screen), so the value
            // reaches the BGRA render buffer without being swizzled.
            //
            // Kept deliberately faint: the viewer strokes each field's edge
            // itself (PdfViewer.DrawFormFieldBorders), so the wash only has to
            // say "there is a field here" — at the old alpha of 40 it competed
            // with the document's own text.
            const uint FieldHighlightBgr = 0xFF9933; // -> rgb(51, 153, 255)
            PdfiumNative.SetFormFieldHighlight(_formEnv.Handle, FieldHighlightBgr, 28);
            PdfiumNative.FormDoDocumentOpenAction(_formEnv.Handle);
        }
    }

    /// <summary>
    /// PDFium's FFI_GetPage. Deliberately returns only pages the cache already
    /// holds: loading here would re-enter the cache while PDFium is mid-call,
    /// and firing FORM_OnAfterLoadPage from inside PDFium's own page setup
    /// recurses. Every path that drives form events acquires the page first,
    /// so by the time PDFium asks, it is resident.
    /// </summary>
    private IntPtr ResolveFormPageLocked(int pageIndex)
        => _pageCache.TryGetValue(pageIndex, out var entry) ? entry.Page : IntPtr.Zero;

    private void OnFormInvalidate(IntPtr page, double left, double top, double right, double bottom)
    {
        // Map the handle back to an index; the cache is small so this is cheap.
        foreach (var (index, entry) in _pageCache)
        {
            if (entry.Page == page)
            {
                FormPageInvalidated?.Invoke(index);
                return;
            }
        }
    }

    // ---- Page cache hooks (declared in PdfDocument.PageCache.cs) ----

    partial void OnPageEnteredCacheLocked(int pageIndex, IntPtr page)
    {
        if (_formEnv is { Handle: not 0 } env && !env.IsXfa)
        {
            PdfiumNative.FormOnAfterLoadPage(page, env.Handle);
        }
    }

    partial void OnPageLeavingCacheLocked(int pageIndex, IntPtr page)
    {
        if (_formEnv is { Handle: not 0 } env && !env.IsXfa)
        {
            PdfiumNative.FormOnBeforeClosePage(page, env.Handle);
        }
    }

    // ---- Input ----

    /// <summary>
    /// Presses and releases the left button at a page-local point. Returns true
    /// when PDFium consumed the click, i.e. it landed on a widget.
    /// </summary>
    public bool FormClick(int pageIndex, double localX, double localY)
    {
        if (!HasFillableForm)
        {
            return false;
        }

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = AcquirePageLocked(pageIndex);
            if (page == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                var (px, py) = ToPageSpaceLocked(page, pageIndex, localX, localY);
                IntPtr form = _formEnv!.Handle;
                bool down = PdfiumNative.FormOnLButtonDown(form, page, px, py);
                bool up = PdfiumNative.FormOnLButtonUp(form, page, px, py);
                return down || up;
            }
            finally
            {
                ReleasePageLocked(pageIndex);
            }
        }
    }

    /// <summary>Sends one character to the focused field. Returns true when consumed.</summary>
    public bool FormChar(int pageIndex, int charCode)
        => WithFormPage(pageIndex, (form, page) => PdfiumNative.FormOnChar(form, page, charCode));

    /// <summary>Sends a virtual key (backspace, arrows, delete) to the focused field.</summary>
    public bool FormKeyDown(int pageIndex, int keyCode)
        => WithFormPage(pageIndex, (form, page) => PdfiumNative.FormOnKeyDown(form, page, keyCode));

    /// <summary>Selects an option by index in the focused combo/list box.</summary>
    public bool FormSetIndexSelected(int pageIndex, int index, bool selected)
        => WithFormPage(pageIndex, (form, page) => PdfiumNative.FormSetIndexSelected(form, page, index, selected));

    /// <summary>
    /// Commits the focused field's pending edit and drops focus.
    ///
    /// This MUST run before any save: PDFium holds the in-progress value in the
    /// focused widget, so saving with focus alive writes the field's *previous*
    /// value and silently loses whatever was just typed.
    /// </summary>
    public void FormKillFocus()
    {
        if (!HasFillableForm)
        {
            return;
        }
        lock (PdfiumLibrary.Lock)
        {
            if (_handle != IntPtr.Zero && _formEnv is { Handle: not 0 } env)
            {
                PdfiumNative.FormKillFocus(env.Handle);
            }
        }
    }

    private bool WithFormPage(int pageIndex, Func<IntPtr, IntPtr, bool> action)
    {
        if (!HasFillableForm)
        {
            return false;
        }

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = AcquirePageLocked(pageIndex);
            if (page == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return action(_formEnv!.Handle, page);
            }
            finally
            {
                ReleasePageLocked(pageIndex);
            }
        }
    }

    // ---- Queries ----

    /// <summary>The form field at a page-local point, or null.</summary>
    public FormFieldInfo? GetFormFieldAt(int pageIndex, double localX, double localY)
    {
        if (!HasFillableForm)
        {
            return null;
        }

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = AcquirePageLocked(pageIndex);
            if (page == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                var (px, py) = ToPageSpaceLocked(page, pageIndex, localX, localY);
                IntPtr form = _formEnv!.Handle;
                IntPtr annot = PdfiumNative.GetFormFieldAtPoint(form, page, (float)px, (float)py);
                if (annot == IntPtr.Zero)
                {
                    return null;
                }
                try
                {
                    return DescribeFieldLocked(form, page, pageIndex, annot);
                }
                finally
                {
                    PdfiumNative.CloseAnnot(annot);
                }
            }
            finally
            {
                ReleasePageLocked(pageIndex);
            }
        }
    }

    /// <summary>Every fillable field on a page, in document order.</summary>
    public IReadOnlyList<FormFieldInfo> GetFormFields(int pageIndex)
    {
        var fields = new List<FormFieldInfo>();
        if (!HasFillableForm)
        {
            return fields;
        }

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = AcquirePageLocked(pageIndex);
            if (page == IntPtr.Zero)
            {
                return fields;
            }
            try
            {
                IntPtr form = _formEnv!.Handle;
                int count = PdfiumNative.GetAnnotCount(page);
                for (int i = 0; i < count; i++)
                {
                    IntPtr annot = PdfiumNative.GetAnnot(page, i);
                    if (annot == IntPtr.Zero)
                    {
                        continue;
                    }
                    try
                    {
                        if (PdfiumNative.GetAnnotSubtype(annot) != PdfiumNative.AnnotWidget)
                        {
                            continue;
                        }
                        var info = DescribeFieldLocked(form, page, pageIndex, annot);
                        if (info is not null)
                        {
                            fields.Add(info);
                        }
                    }
                    finally
                    {
                        PdfiumNative.CloseAnnot(annot);
                    }
                }
            }
            finally
            {
                ReleasePageLocked(pageIndex);
            }
        }
        return fields;
    }

    /// <summary>Current value of a named field, or null if there is no such field on the page.</summary>
    public string? GetFormFieldValue(int pageIndex, string name)
        => GetFormFields(pageIndex).FirstOrDefault(f => f.Name == name)?.Value;

    // ---- Text appearance (/DA) ----

    /// <summary>
    /// The size and colour a named field types in, or null when it has no
    /// <c>/DA</c> of its own to read.
    /// </summary>
    public FieldAppearance? GetFieldAppearance(int pageIndex, string name)
        => WithFieldAnnot(pageIndex, name, annot =>
            DefaultAppearance.TryRead(PdfiumNative.GetAnnotString(annot, DefaultAppearanceKey)));

    /// <summary>
    /// Rewrites a field's <c>/DA</c> so its typed text takes a new size and
    /// colour, and makes PDFium repaint the widget with it.
    ///
    /// Writing the string is the easy half. PDFium builds a widget's appearance
    /// stream when the page is set up for forms and caches it, so an edit made
    /// straight into the dictionary is invisible until that happens again — the
    /// value reads back perfectly while the page keeps drawing the old one. The
    /// second half of this method is therefore the point of it: drop the page
    /// handle so the next acquire re-runs <c>FORM_OnAfterLoadPage</c> and the
    /// appearance is rebuilt from the <c>/DA</c> just written.
    ///
    /// Returns false when the field has no <c>/DA</c> carrying a <c>Tf</c>. That
    /// is not a failure to report loudly: it means the field inherits its
    /// appearance from the AcroForm default, and the font resource name needed to
    /// write a valid replacement is not available here. Guessing one produces a
    /// field that renders in nothing at all.
    /// </summary>
    public bool SetFieldAppearance(int pageIndex, string name, FieldAppearance appearance)
    {
        // The in-progress edit lives in the focused widget, and rebuilding the
        // page underneath it would strand that. Commit first, as saving does.
        FormKillFocus();

        bool written = WithFieldAnnot(pageIndex, name, annot =>
        {
            string? updated = DefaultAppearance.TryWrite(
                PdfiumNative.GetAnnotString(annot, DefaultAppearanceKey), appearance);
            return updated is not null
                && PdfiumNative.SetAnnotString(annot, DefaultAppearanceKey, updated);
        });

        if (!written)
        {
            return false;
        }

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            EvictPageLocked(pageIndex);
        }
        IsDirty = true;
        return true;
    }

    /// <summary>PDF's key for the default appearance string.</summary>
    private const string DefaultAppearanceKey = "DA";

    /// <summary>
    /// Runs <paramref name="read"/> against the widget annotation of a named
    /// field. Returns <c>default</c> when the page or the field is not there.
    /// </summary>
    private T? WithFieldAnnot<T>(int pageIndex, string name, Func<IntPtr, T?> read)
    {
        if (!HasFillableForm)
        {
            return default;
        }

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = AcquirePageLocked(pageIndex);
            if (page == IntPtr.Zero)
            {
                return default;
            }
            try
            {
                IntPtr form = _formEnv!.Handle;
                int count = PdfiumNative.GetAnnotCount(page);
                for (int i = 0; i < count; i++)
                {
                    IntPtr annot = PdfiumNative.GetAnnot(page, i);
                    if (annot == IntPtr.Zero)
                    {
                        continue;
                    }
                    try
                    {
                        if (PdfiumNative.GetAnnotSubtype(annot) == PdfiumNative.AnnotWidget
                            && PdfiumNative.GetFormFieldName(form, annot) == name)
                        {
                            return read(annot);
                        }
                    }
                    finally
                    {
                        PdfiumNative.CloseAnnot(annot);
                    }
                }
                return default;
            }
            finally
            {
                ReleasePageLocked(pageIndex);
            }
        }
    }

    private FormFieldInfo? DescribeFieldLocked(IntPtr form, IntPtr page, int pageIndex, IntPtr annot)
    {
        if (!PdfiumNative.GetAnnotRect(annot, out float l, out float t, out float r, out float b))
        {
            return null;
        }

        var (ptW, ptH) = _pageSizes[pageIndex];
        int sizeX = Math.Max(1, (int)MathF.Round(ptW));
        int sizeY = Math.Max(1, (int)MathF.Round(ptH));
        var (x1, y1) = PdfiumNative.PageToDevice(page, sizeX, sizeY, 0, l, t);
        var (x2, y2) = PdfiumNative.PageToDevice(page, sizeX, sizeY, 0, r, b);

        int flags = PdfiumNative.GetFormFieldFlags(form, annot);
        var kind = (FormFieldKind)PdfiumNative.GetFormFieldType(form, annot);

        // Checkboxes and radios report state, not text, through IsChecked.
        string value = kind is FormFieldKind.CheckBox or FormFieldKind.RadioButton
            ? (PdfiumNative.IsFormFieldChecked(form, annot) ? "On" : "Off")
            : PdfiumNative.GetFormFieldValue(form, annot);

        return new FormFieldInfo(
            pageIndex,
            kind,
            PdfiumNative.GetFormFieldName(form, annot),
            value,
            (flags & PdfiumNative.FormFlagReadOnly) != 0,
            Math.Min(x1, x2),
            Math.Min(y1, y2),
            Math.Abs(x2 - x1),
            Math.Abs(y2 - y1));
    }

    /// <summary>Page-local top-left points → PDFium page space (bottom-left origin).</summary>
    private (double X, double Y) ToPageSpaceLocked(IntPtr page, int pageIndex, double localX, double localY)
    {
        var (ptW, ptH) = _pageSizes[pageIndex];
        return PdfiumNative.DeviceToPage(
            page,
            Math.Max(1, (int)MathF.Round(ptW)),
            Math.Max(1, (int)MathF.Round(ptH)),
            (int)Math.Round(localX),
            (int)Math.Round(localY));
    }

    /// <summary>Caller must hold the lock. Tears the environment down before the document.</summary>
    private void DisposeFormsLocked()
    {
        _formEnv?.Dispose();
        _formEnv = null;
        FormKind = PdfFormKind.None;
    }
}
