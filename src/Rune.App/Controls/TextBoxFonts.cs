using Rune.Engine;

namespace Rune.Controls;

/// <summary>
/// Which Windows font stands in for each PDF standard font while you are typing.
///
/// The text on screen is drawn by Windows and the text in the file is drawn by
/// PDFium from one of the 14 standard fonts, so a pairing has to be chosen or
/// the preview lies about the result. These three pairs are metrically
/// compatible by design rather than by luck: Arial was drawn to Helvetica's
/// widths, and Times New Roman and Courier New are the Monotype cuts of the
/// faces the standard 14 name. Same widths means a line that fits on screen fits
/// in the file.
///
/// Every one of them ships with Windows, so unlike <see cref="SignatureFonts"/>
/// there is nothing to detect and no case where the preview has to fall back.
/// </summary>
internal static class TextBoxFonts
{
    /// <summary>The families offered, in the order the picker shows them.</summary>
    internal static readonly PdfStandardFont[] Families =
    [
        PdfStandardFont.Helvetica,
        PdfStandardFont.Times,
        PdfStandardFont.Courier,
    ];

    /// <summary>What the picker calls each one. The PDF names would mean nothing to a reader.</summary>
    internal static string DisplayName(PdfStandardFont font) => font switch
    {
        PdfStandardFont.Times => "Serif",
        PdfStandardFont.Courier => "Monospace",
        _ => "Sans",
    };

    /// <summary>The Windows family that measures the same as the PDF one.</summary>
    internal static string WindowsFamily(PdfStandardFont font) => font switch
    {
        PdfStandardFont.Times => "Times New Roman",
        PdfStandardFont.Courier => "Courier New",
        _ => "Arial",
    };

    /// <summary>Sizes offered, in points. Matches the set the form-field dialog uses.</summary>
    internal static readonly double[] Sizes = [8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 36, 48];
}
