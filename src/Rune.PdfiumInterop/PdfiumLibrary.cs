namespace Rune.PdfiumInterop;

/// <summary>
/// Global PDFium state. PDFium keeps internal global state and is not
/// thread-safe, so every call into it must hold <see cref="Lock"/>.
/// (M2 will funnel all calls through a single render thread instead;
/// the lock stays as a correctness backstop.)
/// </summary>
public static class PdfiumLibrary
{
    public static readonly object Lock = new();

    private static bool _initialized;

    /// <summary>Idempotent. Safe to call from anywhere before using PDFium.</summary>
    public static void EnsureInitialized()
    {
        lock (Lock)
        {
            if (_initialized)
            {
                return;
            }

            NativeMethods.FPDF_InitLibrary();
            _initialized = true;
        }
    }
}
