namespace Rune.Services;

/// <summary>
/// App-wide clipboard for PDF pages. Holds serialized bytes, never live
/// document handles — the copied pages must survive the source tab closing,
/// and cross-tab paste must not touch another viewer's render thread.
/// </summary>
public static class PageClipboard
{
    public static byte[]? Pdf { get; private set; }
    public static int PageCount { get; private set; }

    public static bool HasPages => Pdf is { Length: > 0 };

    public static void Set(byte[] pdf, int pageCount)
    {
        Pdf = pdf;
        PageCount = pageCount;
    }

    public static void Clear()
    {
        Pdf = null;
        PageCount = 0;
    }
}
