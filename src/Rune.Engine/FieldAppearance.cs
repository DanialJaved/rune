using System.Globalization;

namespace Rune.Engine;

/// <summary>
/// What Rune lets you change about a form field's typed text: how big it is,
/// what colour it is, and — where the file makes it possible — whether it is
/// bold or italic.
/// </summary>
/// <param name="FontSize">In points. Zero is PDF's own "fit the text to the box".</param>
/// <param name="Bold">
/// Nullable, and null means "leave it alone". A <c>/DA</c> names a font resource
/// rather than a weight, so whether a field is currently bold is only knowable
/// for the handful of resource names whose spelling says so — see
/// <see cref="DefaultAppearance"/>. Null keeps a read honest about not knowing,
/// and keeps a write from asserting something it was not asked to.
/// </param>
public readonly record struct FieldAppearance(
    double FontSize, byte R, byte G, byte B, bool? Bold = null, bool? Italic = null)
{
    /// <summary>PDF's meaning for a size of zero: size the text to fill its box.</summary>
    public bool IsAutoSize => FontSize <= 0;

    public static FieldAppearance Default => new(12, 0, 0, 0);
}

/// <summary>
/// Reads and rewrites a widget's <c>/DA</c> (default appearance) string.
///
/// <c>/DA</c> is a scrap of content stream, not a structured value: something
/// like <c>/Helv 12 Tf 0 g</c>, meaning "12pt in the font resource named Helv,
/// painted black". PDFium exposes it only through the generic annotation string
/// accessors, so the grammar has to be handled here.
///
/// The rule throughout is **conservative**: keep the font resource name the file
/// already uses and carry any token that is not understood through untouched.
/// The resource name is a key into the AcroForm's <c>/DR</c> dictionary, so
/// inventing one produces a field that renders in no font at all — which is why
/// a <c>/DA</c> with no <c>Tf</c> is reported as un-rewritable rather than
/// guessed at.
/// </summary>
public static class DefaultAppearance
{
    /// <summary>
    /// The Acrobat font resource names, in <c>{regular, bold, italic,
    /// bold-italic}</c> order, for the three families a <c>/DR</c> conventionally
    /// carries.
    ///
    /// These four-letter names are a convention rather than a rule, and the
    /// entries a given file actually has are whatever its author's tool wrote —
    /// so naming one of them is a GUESS, and a resource that is not in the
    /// <c>/DR</c> gives a field that renders in no font at all. PDFium exposes no
    /// way to read the <c>/DR</c> and check, which is why
    /// <see cref="PdfDocument.SetFieldAppearance"/> proves the guess by looking
    /// at the pixels afterwards instead of trusting this table.
    /// </summary>
    private static readonly string[][] Families =
    [
        ["Helv", "HeBo", "HeOb", "HeBO"],
        ["TiRo", "TiBo", "TiIt", "TiBI"],
        ["Cour", "CoBo", "CoOb", "CoBO"],
    ];

    /// <summary>
    /// The sibling of <paramref name="resource"/> at a given weight and slope,
    /// or null when the name is not one this knows — a file naming its font
    /// <c>/F1</c> says nothing about what <c>/F2</c> would be.
    /// </summary>
    internal static string? Sibling(string resource, bool bold, bool italic)
    {
        int wanted = (bold ? 1 : 0) | (italic ? 2 : 0);
        foreach (string[] family in Families)
        {
            if (Array.IndexOf(family, resource) >= 0)
            {
                return family[wanted];
            }
        }
        return null;
    }

    /// <summary>
    /// What a resource name says about its own weight and slope, or null when it
    /// is not a name this knows.
    /// </summary>
    internal static (bool Bold, bool Italic)? ReadFace(string resource)
    {
        foreach (string[] family in Families)
        {
            int i = Array.IndexOf(family, resource);
            if (i >= 0)
            {
                return ((i & 1) != 0, (i & 2) != 0);
            }
        }
        return null;
    }

    /// <summary>How many operands each colour operator takes.</summary>
    private static int ColourOperands(string op) => op switch
    {
        "g" => 1,
        "rg" => 3,
        "k" => 4,
        _ => 0,
    };

    /// <summary>
    /// Reads the size and colour out of a <c>/DA</c>, or null when it carries no
    /// <c>Tf</c> and therefore says nothing about either.
    ///
    /// A string with a <c>Tf</c> but no colour operator is not a failure: PDF's
    /// default is black, so that is what comes back.
    /// </summary>
    public static FieldAppearance? TryRead(string? da)
    {
        if (string.IsNullOrWhiteSpace(da))
        {
            return null;
        }

        string[] tokens = Tokenize(da);
        int tf = LastIndexOf(tokens, "Tf");
        if (tf < 1 || !TryNumber(tokens[tf - 1], out double size))
        {
            return null;
        }

        var (r, g, b) = ReadColour(tokens);

        // The resource name sits two tokens before Tf and starts with a slash.
        // Its spelling is the only evidence there is about weight and slope, and
        // for a name outside the known families there is none — hence null
        // rather than false, which would claim the field is not bold.
        var face = tf >= 2 && tokens[tf - 2].StartsWith('/')
            ? ReadFace(tokens[tf - 2][1..])
            : null;

        return new FieldAppearance(size, r, g, b, face?.Bold, face?.Italic);
    }

    /// <summary>
    /// Returns <paramref name="da"/> with the text size, colour and — when asked
    /// for and possible — face replaced, or null when it has no <c>Tf</c> to
    /// anchor the rewrite to.
    ///
    /// Any existing colour operator is dropped along with its operands and a
    /// single <c>rg</c> written at the end, which is where a <c>/DA</c>'s colour
    /// conventionally sits and is legal wherever it appears.
    ///
    /// The font resource name changes only when <see cref="FieldAppearance.Bold"/>
    /// or <see cref="FieldAppearance.Italic"/> says to AND the current name is
    /// one of the conventional ones. Anything else keeps the name the file
    /// already uses, because inventing one produces a field that renders in no
    /// font at all.
    /// </summary>
    public static string? TryWrite(string? da, FieldAppearance wanted)
    {
        if (string.IsNullOrWhiteSpace(da))
        {
            return null;
        }

        string[] tokens = Tokenize(da);
        if (LastIndexOf(tokens, "Tf") < 1)
        {
            return null;
        }

        // Drop every colour operator and the operands it consumed, keeping the
        // rest in order. Walking forwards and retracting from the output is what
        // makes "0 g" and "1 1 0 rg" both disappear cleanly.
        var kept = new List<string>(tokens.Length);
        foreach (string token in tokens)
        {
            int operands = ColourOperands(token);
            if (operands > 0)
            {
                for (int i = 0; i < operands && kept.Count > 0; i++)
                {
                    kept.RemoveAt(kept.Count - 1);
                }
                continue;
            }
            kept.Add(token);
        }

        // The Tf index has moved now that colour tokens are gone, so find it again.
        int tf = LastIndexOf(kept, "Tf");
        if (tf < 1)
        {
            return null;
        }
        kept[tf - 1] = Number(wanted.FontSize);

        // Face, when it was asked for and the name is one that can carry it.
        if ((wanted.Bold is not null || wanted.Italic is not null)
            && tf >= 2 && kept[tf - 2].StartsWith('/')
            && ReadFace(kept[tf - 2][1..]) is { } current
            && Sibling(kept[tf - 2][1..], wanted.Bold ?? current.Bold, wanted.Italic ?? current.Italic)
               is { } sibling)
        {
            kept[tf - 2] = $"/{sibling}";
        }

        kept.Add(Number(wanted.R / 255.0));
        kept.Add(Number(wanted.G / 255.0));
        kept.Add(Number(wanted.B / 255.0));
        kept.Add("rg");
        return string.Join(' ', kept);
    }

    /// <summary>
    /// The colour the string ends up painting with. Black when it names none,
    /// which is what PDF itself defaults to.
    /// </summary>
    private static (byte R, byte G, byte B) ReadColour(string[] tokens)
    {
        for (int i = tokens.Length - 1; i >= 0; i--)
        {
            int operands = ColourOperands(tokens[i]);
            if (operands == 0 || i < operands)
            {
                continue;
            }

            var values = new double[operands];
            bool parsed = true;
            for (int j = 0; j < operands; j++)
            {
                parsed &= TryNumber(tokens[i - operands + j], out values[j]);
            }
            if (!parsed)
            {
                continue;
            }

            return tokens[i] switch
            {
                "g" => (Channel(values[0]), Channel(values[0]), Channel(values[0])),
                "rg" => (Channel(values[0]), Channel(values[1]), Channel(values[2])),
                // CMYK the same way PDF composites it onto white.
                _ => (Channel((1 - values[0]) * (1 - values[3])),
                      Channel((1 - values[1]) * (1 - values[3])),
                      Channel((1 - values[2]) * (1 - values[3]))),
            };
        }
        return (0, 0, 0);
    }

    private static byte Channel(double unit) => (byte)Math.Clamp(Math.Round(unit * 255), 0, 255);

    private static string[] Tokenize(string da) =>
        da.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static int LastIndexOf(IReadOnlyList<string> tokens, string op)
    {
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            if (tokens[i] == op)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Invariant culture throughout: a decimal comma is not a PDF number.</summary>
    private static bool TryNumber(string token, out double value) =>
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Number(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);
}
