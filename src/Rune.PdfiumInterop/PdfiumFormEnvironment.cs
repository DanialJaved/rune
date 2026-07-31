using System.Runtime.InteropServices;

namespace Rune.PdfiumInterop;

/// <summary>
/// Owns a PDFium form-fill environment (FPDF_FORMHANDLE) and the native
/// callback table it is built from.
///
/// Lifetime rules (why this class exists, mirroring <see cref="FileAccessAdapter"/>):
///  - PDFium keeps the FPDF_FORMFILLINFO *pointer* and calls back through it
///    for the environment's whole life, so the struct lives in AllocHGlobal
///    memory rather than on the managed heap, where the GC could move it.
///  - Every delegate whose function pointer is stored in that struct is held
///    in a field. Letting one be collected turns the next callback into a jump
///    to freed memory.
///  - The environment must be created and destroyed on the same thread that
///    drives it — Rune's render thread — and destroyed *before* the document.
///
/// Deliberately unimplemented callbacks (left as null; PDFium null-checks each
/// one, so this is supported rather than a gap):
///  - FFI_SetTimer / FFI_KillTimer: only used for caret blink. Implementing
///    them would re-rasterize the focused field's tiles twice a second.
///  - FFI_GetLocalTime: returns a 16-byte struct by value, and is only used by
///    form JavaScript, which this non-V8 PDFium build cannot run anyway.
/// </summary>
public sealed class PdfiumFormEnvironment : IDisposable
{
    /// <summary>Signals that a page region needs re-rendering, in PDF page space.</summary>
    public delegate void InvalidateHandler(IntPtr page, double left, double top, double right, double bottom);

    /// <summary>Resolves a page index to a live FPDF_PAGE. Must come from the page cache.</summary>
    public delegate IntPtr ResolvePageHandler(int pageIndex);

    private readonly InvalidateHandler? _onInvalidate;
    private readonly ResolvePageHandler? _resolvePage;
    private readonly Action? _onChange;

    // Held so the GC cannot collect the delegates PDFium holds pointers to.
    private readonly NativeMethods.FfiInvalidateDelegate _invalidate;
    private readonly NativeMethods.FfiOnChangeDelegate _change;
    private readonly NativeMethods.FfiGetPageDelegate _getPage;
    private readonly NativeMethods.FfiGetCurrentPageDelegate _getCurrentPage;
    private readonly NativeMethods.FfiGetRotationDelegate _getRotation;

    private IntPtr _callbackStruct;
    private IntPtr _handle;

    /// <summary>The FPDF_FORMHANDLE, or Zero once disposed.</summary>
    public IntPtr Handle => _handle;

    /// <summary>The document's form type (FORMTYPE_*), captured at construction.</summary>
    public int FormType { get; }

    /// <summary>True when the document uses XFA, which PDFium cannot fill.</summary>
    public bool IsXfa => FormType is NativeMethods.FORMTYPE_XFA_FULL or NativeMethods.FORMTYPE_XFA_FOREGROUND;

    /// <summary>
    /// Creates the environment. Returns null when the document has no form at
    /// all, so callers can skip the extra FFLDraw pass entirely.
    /// </summary>
    public static PdfiumFormEnvironment? TryCreate(
        IntPtr document,
        ResolvePageHandler resolvePage,
        InvalidateHandler? onInvalidate = null,
        Action? onChange = null)
    {
        int formType = NativeMethods.FPDF_GetFormType(document);
        if (formType == NativeMethods.FORMTYPE_NONE)
        {
            return null;
        }

        var env = new PdfiumFormEnvironment(document, formType, resolvePage, onInvalidate, onChange);
        if (env.Handle == IntPtr.Zero)
        {
            env.Dispose();
            return null;
        }
        return env;
    }

    private PdfiumFormEnvironment(
        IntPtr document,
        int formType,
        ResolvePageHandler resolvePage,
        InvalidateHandler? onInvalidate,
        Action? onChange)
    {
        FormType = formType;
        _resolvePage = resolvePage;
        _onInvalidate = onInvalidate;
        _onChange = onChange;

        _invalidate = Invalidate;
        _change = Change;
        _getPage = GetPage;
        _getCurrentPage = GetCurrentPage;
        _getRotation = GetRotation;

        var info = new NativeMethods.FPDF_FORMFILLINFO
        {
            Version = 1, // must be 1: this build has no XFA, so there is no version 2 tail
            FFI_Invalidate = Marshal.GetFunctionPointerForDelegate(_invalidate),
            FFI_OnChange = Marshal.GetFunctionPointerForDelegate(_change),
            FFI_GetPage = Marshal.GetFunctionPointerForDelegate(_getPage),
            FFI_GetCurrentPage = Marshal.GetFunctionPointerForDelegate(_getCurrentPage),
            FFI_GetRotation = Marshal.GetFunctionPointerForDelegate(_getRotation),
        };

        _callbackStruct = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.FPDF_FORMFILLINFO>());
        Marshal.StructureToPtr(info, _callbackStruct, fDeleteOld: false);

        _handle = NativeMethods.FPDFDOC_InitFormFillEnvironment(document, _callbackStruct);
    }

    private void Invalidate(IntPtr pThis, IntPtr page, double left, double top, double right, double bottom)
    {
        try
        {
            _onInvalidate?.Invoke(page, left, top, right, bottom);
        }
        catch
        {
            // Never let an exception cross the native boundary.
        }
    }

    private void Change(IntPtr pThis)
    {
        try
        {
            _onChange?.Invoke();
        }
        catch
        {
        }
    }

    /// <summary>
    /// PDFium resolves page indices through here. It must NOT call
    /// FORM_OnAfterLoadPage — PDFium calls this from inside its own page setup,
    /// and re-entering would recurse.
    /// </summary>
    private IntPtr GetPage(IntPtr pThis, IntPtr document, int pageIndex)
    {
        try
        {
            return _resolvePage?.Invoke(pageIndex) ?? IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private IntPtr GetCurrentPage(IntPtr pThis, IntPtr document)
    {
        try
        {
            return _resolvePage?.Invoke(0) ?? IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Page rotation as PDFium's form layer sees it. Always 0: Rune's view
    /// rotation is applied when drawing, not to the page, so the form layer
    /// must keep working in unrotated page space.
    /// </summary>
    private int GetRotation(IntPtr pThis, IntPtr page) => 0;

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.FPDFDOC_ExitFormFillEnvironment(_handle);
            _handle = IntPtr.Zero;
        }
        // Only after the environment is gone can the table it points at be freed.
        if (_callbackStruct != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_callbackStruct);
            _callbackStruct = IntPtr.Zero;
        }
    }
}
