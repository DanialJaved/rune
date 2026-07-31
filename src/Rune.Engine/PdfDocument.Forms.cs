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
            const uint FieldHighlightBgr = 0xFF9933; // -> rgb(51, 153, 255)
            PdfiumNative.SetFormFieldHighlight(_formEnv.Handle, FieldHighlightBgr, 40);
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
