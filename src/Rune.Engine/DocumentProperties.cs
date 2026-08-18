using System.Globalization;

namespace Rune.Engine;

/// <summary>One headed group of name/value rows in the properties dialog.</summary>
public sealed record PropertySection(string Title, IReadOnlyList<(string Name, string Value)> Rows);

/// <summary>A font the document draws with.</summary>
/// <param name="Embedded">
/// Whether the font program travels with the file. A document that is not
/// embedded renders in whatever the reading machine substitutes, which is the
/// single most common reason a PDF looks different somewhere else.
/// </param>
public sealed record FontUsage(string Name, bool Embedded, string Kind);

/// <summary>
/// PDF's own date syntax: <c>D:YYYYMMDDHHmmSSOHH'mm'</c>, where everything
/// after the year is optional and <c>O</c> is <c>+</c>, <c>-</c> or <c>Z</c>.
///
/// Worth parsing rather than showing raw, which is what the properties dialog
/// did until now: <c>D:20260812093000+01'00'</c> is a timestamp only to someone
/// who already knows the format.
/// </summary>
public static class PdfDate
{
    public static DateTimeOffset? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // The D: prefix is required by the spec and omitted by plenty of writers.
        string s = value.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        if (s.Length < 4 || !TryPart(s, 0, 4, out int year))
        {
            return null;
        }

        // Every field below the year defaults to the lowest legal value, which
        // is what the spec says an absent one means.
        int month = TryPart(s, 4, 2, out int m) ? m : 1;
        int day = TryPart(s, 6, 2, out int d) ? d : 1;
        int hour = TryPart(s, 8, 2, out int h) ? h : 0;
        int minute = TryPart(s, 10, 2, out int mi) ? mi : 0;
        int second = TryPart(s, 12, 2, out int sec) ? sec : 0;

        var offset = TimeSpan.Zero;
        if (s.Length > 14)
        {
            char sign = s[14];
            if (sign is '+' or '-')
            {
                int offsetHours = TryPart(s, 15, 2, out int oh) ? oh : 0;
                // The minutes are quoted — +01'00' — so they start one past the
                // apostrophe rather than straight after the hours.
                int offsetMinutes = TryPart(s, 18, 2, out int om) ? om : 0;
                offset = new TimeSpan(offsetHours, offsetMinutes, 0);
                if (sign == '-')
                {
                    offset = -offset;
                }
            }
        }

        try
        {
            return new DateTimeOffset(year, month, day, hour, minute, second, offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // month 13, day 32, an offset past 14 hours
        }
    }

    private static bool TryPart(string s, int start, int length, out int value)
    {
        value = 0;
        return start + length <= s.Length
            && int.TryParse(s.AsSpan(start, length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>
/// Naming a page size, because "595 × 842 points" answers a question nobody
/// asked. The measurements are still shown alongside — the name is the shortcut,
/// not the answer.
/// </summary>
public static class PaperSize
{
    /// <summary>Points per millimetre.</summary>
    private const double PtPerMm = 72.0 / 25.4;

    /// <summary>
    /// Tolerance in points. Generous enough to absorb the rounding every writer
    /// does differently (A4 is 595.276pt and gets written as 595 and 596 alike)
    /// and far too tight to confuse two named sizes with each other.
    /// </summary>
    private const double Tolerance = 3;

    private static readonly (string Name, double Width, double Height)[] Known =
    [
        ("A3", 841.89, 1190.55),
        ("A4", 595.28, 841.89),
        ("A5", 419.53, 595.28),
        ("A6", 297.64, 419.53),
        ("Letter", 612, 792),
        ("Legal", 612, 1008),
        ("Tabloid", 792, 1224),
        ("Executive", 522, 756),
    ];

    /// <summary>The paper's name, in either orientation, or null for an odd size.</summary>
    public static string? Name(double widthPt, double heightPt)
    {
        foreach (var (name, w, h) in Known)
        {
            if ((Close(widthPt, w) && Close(heightPt, h)) || (Close(widthPt, h) && Close(heightPt, w)))
            {
                return name;
            }
        }
        return null;
    }

    /// <summary>
    /// A page size as everything a reader might want: the name where there is
    /// one, the millimetres people measure paper in, and the points the file
    /// actually stores.
    /// </summary>
    public static string Describe(double widthPt, double heightPt)
    {
        string mm = $"{Mm(widthPt)} × {Mm(heightPt)} mm";
        string pt = $"{widthPt:0.#} × {heightPt:0.#} pt";
        string orientation = widthPt > heightPt ? ", landscape" : string.Empty;

        return Name(widthPt, heightPt) is { } name
            ? $"{name}{orientation} — {mm} ({pt})"
            : $"{mm} ({pt})";
    }

    private static bool Close(double a, double b) => Math.Abs(a - b) <= Tolerance;

    private static string Mm(double pt) => (pt / PtPerMm).ToString("0", CultureInfo.CurrentCulture);
}
