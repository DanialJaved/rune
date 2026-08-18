using System.Globalization;
using Rune.PdfiumInterop;

namespace Rune.Engine;

/// <summary>
/// One of the 14 standard PDF fonts, which every reader has built in and none
/// has to embed. Bold and italic are separate fonts here rather than flags,
/// because that is how PostScript names them.
/// </summary>
public enum PdfStandardFont
{
    Helvetica,
    Times,
    Courier,
}

/// <summary>How the lines sit inside the box's width.</summary>
public enum TextAlign
{
    Left,
    Center,
    Right,
    Justify,
}

/// <summary>What to write, and how it should look. Line breaks are honoured.</summary>
/// <param name="WidthPt">
/// How wide the box is, in points. Zero means "no box": the text is as wide as
/// its longest line and nothing wraps, which is what a freshly typed box is
/// until someone drags a corner.
/// </param>
public sealed record TextBoxContent(
    string Text,
    PdfStandardFont Font,
    double FontSize,
    byte R,
    byte G,
    byte B,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    TextAlign Align = TextAlign.Left,
    double WidthPt = 0)
{
    /// <summary>
    /// Baseline-to-baseline distance as a multiple of the size. 1.2 is the
    /// typographic default and what every word processor starts from, so text
    /// pasted out of one lands looking the same.
    /// </summary>
    public const double LineHeight = 1.2;

    /// <summary>
    /// The lines to draw. PDF has no concept of wrapping inside a text object,
    /// so whatever breaks the caller wants must already be in the string.
    /// </summary>
    public string[] Lines =>
        Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>The PostScript name PDFium wants for this face.</summary>
    public string PostScriptName => PostScriptNameFor(Font, Bold, Italic);

    /// <summary>
    /// Times is the awkward one: its regular weight is "Times-Roman", not
    /// "Times", and its slanted face is "Italic" where the other two families
    /// call it "Oblique".
    /// </summary>
    public static string PostScriptNameFor(PdfStandardFont font, bool bold, bool italic) => font switch
    {
        PdfStandardFont.Times => (bold, italic) switch
        {
            (true, true) => "Times-BoldItalic",
            (true, false) => "Times-Bold",
            (false, true) => "Times-Italic",
            _ => "Times-Roman",
        },
        PdfStandardFont.Courier => Suffix("Courier", bold, italic),
        _ => Suffix("Helvetica", bold, italic),
    };

    private static string Suffix(string family, bool bold, bool italic) => (bold, italic) switch
    {
        (true, true) => $"{family}-BoldOblique",
        (true, false) => $"{family}-Bold",
        (false, true) => $"{family}-Oblique",
        _ => family,
    };

    /// <summary>
    /// Reads a /BaseFont name back into the three things that produced it, or
    /// null for a name Rune did not write.
    ///
    /// Deliberately implemented by generating every name and comparing, rather
    /// than by a second switch that picks the string apart: the two directions
    /// then cannot drift, and there are only twelve of them.
    /// </summary>
    public static (PdfStandardFont Font, bool Bold, bool Italic)? TryParsePostScriptName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string wanted = StripSubsetPrefix(name.Trim());
        foreach (var font in Enum.GetValues<PdfStandardFont>())
        {
            foreach (bool bold in new[] { false, true })
            {
                foreach (bool italic in new[] { false, true })
                {
                    if (PostScriptNameFor(font, bold, italic) == wanted)
                    {
                        return (font, bold, italic);
                    }
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Drops an embedded-subset tag: six capitals and a plus, as in
    /// "ABCDEF+Helvetica". The standard 14 are never subset, since they are
    /// never embedded, but a file that has been through another tool on the way
    /// back is not Rune's to assume about.
    /// </summary>
    private static string StripSubsetPrefix(string name) =>
        name.Length > 7 && name[6] == '+' && name.Take(6).All(c => c is >= 'A' and <= 'Z')
            ? name[7..]
            : name;
}

// Real text on a page, as opposed to a picture of one.
//
// Stored as a text object inside a STAMP annotation, which the probe in
// TextObjectProbeTests settled and pinned:
//
//   * PDFium generates no appearance for a FreeText annotation, and
//     FPDFAnnot_AppendObject is gated on a subtype check admitting only ink and
//     stamp — so FreeText is refused the object and renders as nothing.
//   * As a stamp annotation the text is a discrete object: movable, resizable,
//     erasable, undoable, all through machinery that already exists.
//   * It is vector text, so it stays crisp at any zoom, and Flatten turns it
//     into ordinary searchable page text because the glyphs were always real.
//
// Nothing here carries font metrics. Positioning measures the objects PDFium
// actually built (FPDFPageObj_GetBounds) and moves them into place afterwards,
// so the result cannot drift from what the renderer does — the same reason
// SignatureFonts resolves a face once and feeds it to both preview and output.
public sealed partial class PdfDocument
{
    /// <summary>
    /// Writes text onto a page with its top-left corner at
    /// (<paramref name="x"/>, <paramref name="y"/>) in page-local points.
    ///
    /// Returns the spec needed to re-create it, so the caller can make it
    /// undoable, or null when there was nothing to write.
    /// </summary>
    public AnnotationSpec? AddTextBox(int pageIndex, double x, double y, TextBoxContent content)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

        if (content.FontSize <= 0 || content.Lines.All(string.IsNullOrEmpty))
        {
            return null;
        }

        // The size can arrive from a file rather than from the size picker —
        // TryReadTextBox reads whatever the object carries — so it is clamped
        // here rather than trusted.
        content = content with { FontSize = Math.Clamp(content.FontSize, MinFontPt, MaxFontPt) };

        AnnotationSpec spec;
        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = AcquirePageLocked(pageIndex);
            if (page == IntPtr.Zero)
            {
                throw PdfiumNative.LastError();
            }
            try
            {
                // Where the top-left the caller asked for lands in page space.
                // Height is unknown until the text is measured, so the rect is
                // built from the measurement and only its top-left is used here.
                var (targetL, _, _, targetT) = ToPageRectLocked(page, pageIndex, x, y, 1, 1);

                IntPtr annot = PdfiumNative.CreateAnnot(page, PdfiumNative.AnnotStamp);
                if (annot == IntPtr.Zero)
                {
                    throw new PdfiumException("Could not create the text annotation.", 1);
                }

                (float L, float B, float R, float T) rect;
                try
                {
                    rect = AttachTextLocked(annot, content, targetL, targetT);
                    PdfiumNative.SetAnnotPrintFlag(annot);
                    // /Contents is where a reader looks for an annotation's text,
                    // and it is how Rune tells a text stamp from a picture one.
                    PdfiumNative.SetAnnotString(annot, "Contents", content.Text);
                    WriteTextStyleLocked(annot, content);
                }
                finally
                {
                    PdfiumNative.CloseAnnot(annot);
                }

                spec = new AnnotationSpec(
                    pageIndex,
                    PdfiumNative.AnnotStamp,
                    Quads: [],
                    InkStrokes: [],
                    Rect: rect,
                    Color: (content.R, content.G, content.B, 255),
                    BorderWidth: 0,
                    Contents: content.Text,
                    Text: content);
            }
            finally
            {
                ReleasePageLocked(pageIndex);
            }
        }

        IsDirty = true;
        return spec;
    }

    /// <summary>
    /// Lays the words out, moves the block so its top-left sits at
    /// (<paramref name="targetL"/>, <paramref name="targetT"/>), hangs the whole
    /// lot on <paramref name="annot"/>, and returns the rect it occupies.
    ///
    /// Separate from annotation creation so undo can re-attach text to an
    /// annotation <see cref="AddAnnotationFromSpec"/> has already made, the way
    /// <c>AttachStampImageLocked</c> re-attaches pixels.
    ///
    /// The layout happens in a local frame with the box's left edge at x=0 and
    /// line <c>i</c>'s BASELINE at <c>y = -i × leading</c>. A text object's own
    /// origin is its baseline before any transform, so working in that frame is
    /// what lets an underline be placed by arithmetic rather than by guessing
    /// where the glyphs sit. The whole block is measured and moved into place
    /// afterwards, exactly as before.
    ///
    /// Caller must hold <see cref="PdfiumLibrary.Lock"/> and have the page.
    /// </summary>
    private (float L, float B, float R, float T) AttachTextLocked(
        IntPtr annot, TextBoxContent content, float targetL, float targetT)
    {
        IntPtr font = PdfiumNative.LoadStandardFont(_handle, content.PostScriptName);
        if (font == IntPtr.Zero)
        {
            throw new PdfiumException($"PDFium does not know the font '{content.PostScriptName}'.", 1);
        }

        var objects = new List<IntPtr>();
        try
        {
            double size = content.FontSize;
            double leading = size * TextBoxContent.LineHeight;
            var lines = WrapLocked(font, content);

            // Without a box the lines align against the widest of them, since
            // that is the only edge there is to align to.
            double boxWidth = content.WidthPt > 0
                ? content.WidthPt
                : lines.Max(line => MeasureWidthLocked(font, size, line.Text));

            for (int i = 0; i < lines.Count; i++)
            {
                string text = lines[i].Text;
                if (text.Length == 0)
                {
                    continue; // a blank line still advances the leading, draws nothing
                }

                double baseline = -i * leading;
                var placedLine = LayOutLineLocked(font, content, text, lines[i].EndsParagraph, boxWidth, baseline);
                objects.AddRange(placedLine.Objects);

                if (content.Underline)
                {
                    // Below the descenders rather than through them, and thin
                    // enough to read as a rule rather than as a bar.
                    float descent = PdfiumNative.GetFontDescent(font, (float)size);
                    IntPtr rule = PdfiumNative.NewFilledRect(
                        (float)placedLine.Left,
                        (float)(baseline - descent * 0.6),
                        (float)placedLine.Width,
                        (float)Math.Max(0.4, size / 14),
                        content.R, content.G, content.B);
                    if (rule != IntPtr.Zero)
                    {
                        objects.Add(rule);
                    }
                }
            }

            if (objects.Count == 0)
            {
                throw new PdfiumException("There was no text to write.", 1);
            }

            var measured = MeasureLocked(objects)
                ?? throw new PdfiumException("PDFium could not measure the text.", 1);

            // Move the whole block into place. Transforms accumulate, so this
            // composes with the per-line offsets already applied.
            //
            // Horizontally that is the BOX's left edge, which the layout frame
            // puts at x=0 — not the ink's. Re-anchoring on the ink would drag a
            // centred line back to the left margin and cancel the alignment
            // exactly, which is what it did until this comment existed.
            // Vertically the ink is all there is: the top depends on the
            // ascent, which is the thing this whole approach refuses to assume.
            double dx = targetL;
            double dy = targetT - measured.T;
            foreach (IntPtr obj in objects)
            {
                PdfiumNative.TransformPageObject(obj, 1, 0, 0, 1, dx, dy);
            }

            // Horizontally the rect is the BOX, not the ink: the frame you
            // dragged has to be the frame you get back, or a centred line would
            // shrink its own box the moment it was re-read. Vertically the ink
            // is all there is to go on, so that is what it uses.
            double left = content.WidthPt > 0 ? targetL : measured.L + dx;
            double right = content.WidthPt > 0 ? targetL + content.WidthPt : measured.R + dx;
            var placed = ((float)left, (float)(measured.B + dy), (float)right, (float)(measured.T + dy));

            // The rect goes on BEFORE the objects. PDFium sizes the appearance
            // form's bounding box from the annotation's rect at the moment an
            // object is appended, so appending into a rect that is still empty
            // produces a zero-sized box and the text renders as nothing at all,
            // with every read-back still reporting exactly what was asked for.
            // Setting the rect afterwards does not rebuild it. The stamp path
            // has always done it in this order; this cost a regression to learn.
            PdfiumNative.SetAnnotRect(annot, placed.Item1, placed.Item2, placed.Item3, placed.Item4);

            foreach (IntPtr obj in objects)
            {
                if (!PdfiumNative.AppendAnnotObject(annot, obj))
                {
                    throw new PdfiumException("PDFium refused the text object on the annotation.", 1);
                }
            }
            objects.Clear(); // the annotation owns them now

            return placed;
        }
        finally
        {
            PdfiumNative.CloseFont(font);
        }
    }

    /// <summary>One laid-out line: its objects, and where its ink starts and ends.</summary>
    private readonly record struct PlacedLine(List<IntPtr> Objects, double Left, double Width);

    /// <summary>
    /// Places one line at <paramref name="baseline"/>, aligned within
    /// <paramref name="boxWidth"/>.
    ///
    /// Justified lines are the odd ones: the gap between words has to be
    /// stretched, and PDF has no way to say that inside a single run, so each
    /// word becomes its own object at a computed x. A paragraph's last line is
    /// never justified (that is what justification means), and neither is a line
    /// already wider than its box — stretching a negative slack would pull the
    /// words on top of each other.
    /// </summary>
    private PlacedLine LayOutLineLocked(
        IntPtr font, TextBoxContent content, string text, bool endsParagraph,
        double boxWidth, double baseline)
    {
        double size = content.FontSize;
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (content.Align == TextAlign.Justify && !endsParagraph && words.Length > 1)
        {
            var widths = words.Select(w => MeasureWidthLocked(font, size, w)).ToArray();
            double slack = boxWidth - widths.Sum();
            if (slack > 0)
            {
                double gap = slack / (words.Length - 1);
                var placed = new List<IntPtr>(words.Length);
                double x = 0;
                for (int i = 0; i < words.Length; i++)
                {
                    placed.Add(NewLineObjectLocked(font, content, words[i], x, baseline));
                    x += widths[i] + gap;
                }
                return new PlacedLine(placed, 0, boxWidth);
            }
        }

        double width = MeasureWidthLocked(font, size, text);
        double left = content.Align switch
        {
            TextAlign.Center => Math.Max(0, (boxWidth - width) / 2),
            TextAlign.Right => Math.Max(0, boxWidth - width),
            _ => 0,
        };
        return new PlacedLine([NewLineObjectLocked(font, content, text, left, baseline)], left, width);
    }

    /// <summary>
    /// A text object carrying <paramref name="text"/>, with its ink starting at
    /// <paramref name="x"/> and its baseline at <paramref name="baseline"/>.
    ///
    /// The left side bearing is measured out rather than assumed to be zero:
    /// asking for x is asking where the ink starts, and for a leading "T" or a
    /// space that is not where the origin is.
    /// </summary>
    private IntPtr NewLineObjectLocked(
        IntPtr font, TextBoxContent content, string text, double x, double baseline)
    {
        IntPtr obj = PdfiumNative.NewTextObject(_handle, font, (float)content.FontSize);
        if (obj == IntPtr.Zero)
        {
            throw new PdfiumException("Could not create the text object.", 1);
        }
        if (!PdfiumNative.SetTextObjectText(obj, text))
        {
            PdfiumNative.DestroyPageObject(obj);
            throw new PdfiumException("PDFium rejected the text.", 1);
        }
        PdfiumNative.SetObjectFillColor(obj, content.R, content.G, content.B, 255);

        double bearing = PdfiumNative.GetObjectBounds(obj)?.L ?? 0;
        PdfiumNative.TransformPageObject(obj, 1, 0, 0, 1, x - bearing, baseline);
        return obj;
    }

    /// <summary>A line of laid-out text, and whether it is the last of its paragraph.</summary>
    private readonly record struct WrappedLine(string Text, bool EndsParagraph);

    /// <summary>
    /// Breaks the content's hard lines to fit <see cref="TextBoxContent.WidthPt"/>.
    ///
    /// Greedy, which is what every word processor does and what the reader
    /// expects to see. A word too long for the box on its own is left to
    /// overhang rather than being broken mid-word: hyphenation is a language
    /// question, and a wrong hyphen is worse than a wide line.
    ///
    /// With no box width nothing wraps and the hard lines come back untouched,
    /// which is the whole of the behaviour a freshly typed box has.
    /// </summary>
    private List<WrappedLine> WrapLocked(IntPtr font, TextBoxContent content)
    {
        string[] hard = content.Lines;
        if (content.WidthPt <= 0)
        {
            return [.. hard.Select(line => new WrappedLine(line, true))];
        }

        var wrapped = new List<WrappedLine>();
        foreach (string paragraph in hard)
        {
            string[] words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                wrapped.Add(new WrappedLine(string.Empty, true));
                continue;
            }

            string current = words[0];
            for (int i = 1; i < words.Length; i++)
            {
                string candidate = $"{current} {words[i]}";
                if (MeasureWidthLocked(font, content.FontSize, candidate) <= content.WidthPt)
                {
                    current = candidate;
                    continue;
                }
                wrapped.Add(new WrappedLine(current, false));
                current = words[i];
            }
            // Whatever is left is the paragraph's last line, justified or not.
            wrapped.Add(new WrappedLine(current, true));
        }
        return wrapped;
    }

    /// <summary>
    /// How wide <paramref name="text"/> draws, in points.
    ///
    /// Measured by building the object PDFium would draw and asking it, rather
    /// than from the standard-14 width tables. The tables can disagree with the
    /// renderer; this cannot, and it is the same reason
    /// <see cref="MeasureLocked"/> exists. Zero for text with no ink, which is
    /// what a blank line measures.
    /// </summary>
    private double MeasureWidthLocked(IntPtr font, double fontSize, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        IntPtr obj = PdfiumNative.NewTextObject(_handle, font, (float)fontSize);
        if (obj == IntPtr.Zero)
        {
            return 0;
        }
        try
        {
            return PdfiumNative.SetTextObjectText(obj, text)
                   && PdfiumNative.GetObjectBounds(obj) is { } bounds
                ? bounds.R - bounds.L
                : 0;
        }
        finally
        {
            // Never appended to anything, so this one is ours to free.
            PdfiumNative.DestroyPageObject(obj);
        }
    }

    /// <summary>
    /// A private key holding the two things about a text box that cannot be read
    /// back off the objects: whether it is underlined, and how it is aligned.
    ///
    /// PDF has nowhere standard to put either on a stamp annotation. Underline
    /// could in principle be inferred from the presence of a path object and
    /// alignment from where the ink sits, but both inferences are guesses that
    /// go wrong on a one-line box, and a resize that guesses wrong silently
    /// rewrites the user's formatting. Other readers ignore the key, as they
    /// should; the words in /Contents remain the interoperable part.
    /// </summary>
    private const string RuneStyleKey = "RuneStyle";

    /// <summary>
    /// Compact enough to read in a raw PDF, e.g. <c>U1 A2 W180.5</c>. The width
    /// is written rather than taken from the annotation rect on the way back in:
    /// an auto-width box's rect is its own ink, and re-reading that as a box
    /// width would set the wrap point to the exact width of the longest line,
    /// where a hair of rounding turns into a spurious extra line.
    /// </summary>
    private static void WriteTextStyleLocked(IntPtr annot, TextBoxContent content)
        => PdfiumNative.SetAnnotString(annot, RuneStyleKey, EncodeStyle(content));

    private static string EncodeStyle(TextBoxContent content)
    {
        string style = $"U{(content.Underline ? 1 : 0)} A{(int)content.Align}";
        return content.WidthPt > 0
            ? $"{style} W{content.WidthPt.ToString("0.###", CultureInfo.InvariantCulture)}"
            : style;
    }

    /// <summary>
    /// Reads <see cref="RuneStyleKey"/> back. Anything unparseable reads as the
    /// defaults — a box written by an older build has no key at all, and it was
    /// left-aligned, not underlined and auto-width, which is exactly what that
    /// yields.
    /// </summary>
    private static (bool Underline, TextAlign Align, double WidthPt) DecodeStyle(string? encoded)
    {
        bool underline = false;
        var align = TextAlign.Left;
        double width = 0;

        foreach (string token in (encoded ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 2
                || !double.TryParse(token[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                continue;
            }
            switch (token[0])
            {
                case 'U':
                    underline = value != 0;
                    break;
                case 'A' when Enum.IsDefined((TextAlign)(int)value):
                    align = (TextAlign)(int)value;
                    break;
                case 'W' when value > 0:
                    width = value;
                    break;
            }
        }
        return (underline, align, width);
    }

    /// <summary>
    /// Reads a text box back out of the file: its words, its size, its face and
    /// its colour. Null when that annotation carries no text, which the caller
    /// should treat as "not a text box" rather than as a failure.
    ///
    /// The counterpart to <see cref="TryReadStampImage"/>, and written for the
    /// same reason. A style cached in the session would refuse to resize a text
    /// box that was already in the document when it opened, and annotation
    /// indexes shift underneath such a cache anyway.
    /// </summary>
    public TextBoxContent? TryReadTextBox(int pageIndex, int annotIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

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
                IntPtr annot = PdfiumNative.GetAnnot(page, annotIndex);
                if (annot == IntPtr.Zero)
                {
                    return null;
                }
                try
                {
                    // The words come from /Contents, which AddTextBox writes.
                    // Reassembling them from the objects would lose the line
                    // breaks, since each line is a separate object.
                    string text = PdfiumNative.GetAnnotString(annot, "Contents");
                    if (string.IsNullOrEmpty(text))
                    {
                        return null;
                    }

                    // Underline, alignment and box width have nowhere to live on
                    // the objects, so they come from Rune's own key.
                    var (underline, align, width) =
                        DecodeStyle(PdfiumNative.GetAnnotString(annot, RuneStyleKey));

                    // The first text object is the first non-empty line, and
                    // every line of a box shares its style.
                    int count = PdfiumNative.GetAnnotObjectCount(annot);
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr obj = PdfiumNative.GetAnnotObject(annot, i);
                        if (obj == IntPtr.Zero || !PdfiumNative.IsTextObject(obj))
                        {
                            continue;
                        }
                        if (PdfiumNative.GetTextObjectFontSize(obj) is not { } size || size <= 0)
                        {
                            continue;
                        }
                        if (TextBoxContent.TryParsePostScriptName(
                                PdfiumNative.GetTextObjectFontName(obj)) is not { } face)
                        {
                            continue;
                        }

                        // Black when the object names no fill, which is what PDF
                        // itself defaults to.
                        var (r, g, b, _) = PdfiumNative.GetObjectFillColor(obj)
                            ?? ((byte)0, (byte)0, (byte)0, (byte)255);
                        return new TextBoxContent(
                            text, face.Font, size, r, g, b, face.Bold, face.Italic,
                            underline, align, width);
                    }
                    return null;
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

    /// <summary>
    /// Resizes a text box by re-flowing its words into a new width, with its
    /// top-left at (<paramref name="x"/>, <paramref name="y"/>).
    ///
    /// **The point size does not change.** Dragging a corner makes the box
    /// wider or narrower and the text wraps to suit, which is what a text box
    /// does everywhere else and what makes the size control the only thing that
    /// changes the size. Height is not a parameter at all: it is whatever the
    /// wrap comes out at.
    ///
    /// Remove-and-re-create for the same reason <see cref="ResizeStamp"/> is:
    /// editing an appearance in place compounds. Returns the index the
    /// re-created box now sits at plus a spec that rebuilds the original, which
    /// is the pair an undo entry needs, or null when there is no text there.
    /// </summary>
    public (int NewIndex, AnnotationSpec Before)? ResizeTextBox(
        int pageIndex, int annotIndex, double x, double y, double widthPt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);
        ArgumentOutOfRangeException.ThrowIfNegative(annotIndex);

        if (widthPt <= 0 || TryReadTextBox(pageIndex, annotIndex) is not { } content)
        {
            return null;
        }
        if (GetAnnotations(pageIndex).ElementAtOrDefault(annotIndex) is not { } original
            || original.Subtype != PdfiumNative.AnnotStamp || original.Width <= 0)
        {
            return null;
        }

        // Everything needed to put the original back, captured before anything
        // is destroyed. The words ride along rather than a raster, so undo
        // re-renders rather than replaying pixels.
        var before = new AnnotationSpec(
            pageIndex,
            PdfiumNative.AnnotStamp,
            Quads: [],
            InkStrokes: [],
            Rect: ToPageRect(pageIndex, original.X, original.Y, original.Width, original.Height),
            Color: (content.R, content.G, content.B, 255),
            BorderWidth: 0,
            Contents: content.Text,
            Text: content);

        // Never narrower than the size itself: a box a couple of points wide
        // wraps to one character a line and cannot be grabbed back out of it.
        double width = Math.Max(content.FontSize, widthPt);
        if (!RemoveAnnotation(pageIndex, annotIndex))
        {
            return null;
        }

        if (AddTextBox(pageIndex, x, y, content with { WidthPt = width }) is null)
        {
            // Put the original back rather than leaving the page a box short.
            AddAnnotationFromSpec(before);
            return null;
        }

        // Re-creating appends, so the box is now last.
        return (GetAnnotations(pageIndex).Count - 1, before);
    }

    /// <summary>Small enough to be a footnote, and still readable at 100%.</summary>
    private const double MinFontPt = 4;

    /// <summary>A line of this at 288pt is already taller than a third of a page.</summary>
    private const double MaxFontPt = 288;

    /// <summary>
    /// Writes the same text straight into the page's own content stream.
    ///
    /// Not used by the app, and internal for that reason. It is the control that
    /// keeps <see cref="AddTextBox"/> honest: it is searchable the moment it is
    /// saved, which is what proves that the annotation route's searchability
    /// after flattening is a property of the route rather than of PDFium.
    /// </summary>
    internal AnnotationSpec? AddTextToPageContent(int pageIndex, double x, double y, TextBoxContent content)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

        if (content.FontSize <= 0 || content.Lines.All(string.IsNullOrEmpty))
        {
            return null;
        }

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = AcquirePageLocked(pageIndex);
            if (page == IntPtr.Zero)
            {
                throw PdfiumNative.LastError();
            }

            IntPtr font = PdfiumNative.LoadStandardFont(_handle, content.PostScriptName);
            if (font == IntPtr.Zero)
            {
                ReleasePageLocked(pageIndex);
                throw new PdfiumException($"PDFium does not know the font '{content.PostScriptName}'.", 1);
            }

            try
            {
                var (targetL, _, _, targetT) = ToPageRectLocked(page, pageIndex, x, y, 1, 1);

                IntPtr obj = PdfiumNative.NewTextObject(_handle, font, (float)content.FontSize);
                if (obj == IntPtr.Zero || !PdfiumNative.SetTextObjectText(obj, content.Lines[0]))
                {
                    throw new PdfiumException("Could not create the text object.", 1);
                }
                PdfiumNative.SetObjectFillColor(obj, content.R, content.G, content.B, 255);

                var measured = MeasureLocked([obj])
                    ?? throw new PdfiumException("PDFium could not measure the text.", 1);
                PdfiumNative.TransformPageObject(obj, 1, 0, 0, 1, targetL - measured.L, targetT - measured.T);

                PdfiumNative.InsertPageObject(page, obj);
                if (!PdfiumNative.GenerateContent(page))
                {
                    throw new PdfiumException("Could not regenerate the page content.", 1);
                }

                IsDirty = true;
                return new AnnotationSpec(
                    pageIndex, PdfiumNative.AnnotStamp, Quads: [], InkStrokes: [],
                    Rect: ((float)measured.L, (float)measured.B, (float)measured.R, (float)measured.T),
                    Color: (content.R, content.G, content.B, 255),
                    BorderWidth: 0, Contents: content.Text, Text: content);
            }
            finally
            {
                PdfiumNative.CloseFont(font);
                ReleasePageLocked(pageIndex);
            }
        }
    }

    /// <summary>The union of several objects' bounds, or null if none could be measured.</summary>
    private static (double L, double B, double R, double T)? MeasureLocked(IReadOnlyList<IntPtr> objects)
    {
        double l = double.MaxValue, b = double.MaxValue, r = double.MinValue, t = double.MinValue;
        bool any = false;

        foreach (IntPtr obj in objects)
        {
            if (PdfiumNative.GetObjectBounds(obj) is not { } bounds)
            {
                continue;
            }
            any = true;
            l = Math.Min(l, bounds.L);
            b = Math.Min(b, bounds.B);
            r = Math.Max(r, bounds.R);
            t = Math.Max(t, bounds.T);
        }

        return any ? (l, b, r, t) : null;
    }
}
