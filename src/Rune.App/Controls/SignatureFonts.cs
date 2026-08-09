using Microsoft.Graphics.Canvas.Text;

namespace Rune.Controls;

/// <summary>
/// The handwriting faces offered for a typed signature, and which of them this
/// machine actually has.
///
/// Rune ships no font. Windows has included Segoe Script and Segoe Print since
/// Vista and Ink Free since Windows 10 1803, so all three are present on any
/// mainstream install, and bundling one would put weight back into a package
/// that v0.6.0 spent real effort trimming.
///
/// The catch is that a stripped image — LTSC, an N edition, some server SKUs —
/// may have none of them, and DirectWrite would silently fall back to the UI
/// sans, which reads as typed text rather than as a signature. So the family is
/// <b>resolved once against the installed set</b> and the same answer feeds both
/// the on-screen preview and the offscreen render. Preview and output can then
/// never disagree, and <see cref="AnyAvailable"/> lets the pad say so plainly
/// instead of quietly producing something that does not look signed.
/// </summary>
internal static class SignatureFonts
{
    /// <summary>A named style, and the faces that can serve it, best first.</summary>
    internal sealed record Style(string Name, string[] Candidates);

    /// <summary>
    /// Offered in the order most people would want them: a flowing hand first,
    /// then a printed hand, then a formal script.
    /// </summary>
    internal static readonly Style[] Styles =
    [
        new("Handwritten", ["Segoe Script", "Ink Free", "Gabriola"]),
        new("Printed", ["Segoe Print", "Ink Free", "Segoe Script"]),
        new("Formal", ["Gabriola", "Segoe Script", "Ink Free"]),
    ];

    private static HashSet<string>? _installed;

    /// <summary>
    /// Family names present on this machine, matched case-insensitively.
    /// Built once: enumerating the system font set is not free, and the answer
    /// cannot change while the dialog is open.
    /// </summary>
    private static HashSet<string> Installed
    {
        get
        {
            if (_installed is not null)
            {
                return _installed;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var face in CanvasFontSet.GetSystemFontSet().Fonts)
                {
                    foreach (var family in face.FamilyNames.Values)
                    {
                        names.Add(family);
                    }
                }
            }
            catch (Exception ex)
            {
                // Never worth failing the dialog over. An empty set means every
                // Resolve falls back, which is the same as the font being absent.
                Rune.Services.ErrorLog.Default.Write(nameof(SignatureFonts), ex);
            }
            return _installed = names;
        }
    }

    /// <summary>
    /// The first candidate this machine has, or null when it has none of them.
    /// Null is the caller's cue to tell the user typing will not look like a
    /// signature here.
    /// </summary>
    internal static string? Resolve(Style style)
    {
        foreach (var candidate in style.Candidates)
        {
            if (Installed.Contains(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>True when at least one style can be rendered in a real script face.</summary>
    internal static bool AnyAvailable => Styles.Any(s => Resolve(s) is not null);
}
