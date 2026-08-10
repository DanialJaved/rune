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

/// <summary>What to write, and how it should look. Line breaks are honoured.</summary>
public sealed record TextBoxContent(
    string Text,
    PdfStandardFont Font,
    double FontSize,
    byte R,
    byte G,
    byte B,
    bool Bold = false,
    bool Italic = false)
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

    /// <summary>
    /// The PostScript name PDFium wants. Times is the awkward one: its regular
    /// weight is "Times-Roman", not "Times", and its slanted face is "Italic"
    /// where the other two families call it "Oblique".
    /// </summary>
    public string PostScriptName => Font switch
    {
        PdfStandardFont.Times => (Bold, Italic) switch
        {
            (true, true) => "Times-BoldItalic",
            (true, false) => "Times-Bold",
            (false, true) => "Times-Italic",
            _ => "Times-Roman",
        },
        PdfStandardFont.Courier => Suffix("Courier"),
        _ => Suffix("Helvetica"),
    };

    private string Suffix(string family) => (Bold, Italic) switch
    {
        (true, true) => $"{family}-BoldOblique",
        (true, false) => $"{family}-Bold",
        (false, true) => $"{family}-Oblique",
        _ => family,
    };
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
    /// Builds one text object per line, moves the block so its top-left sits at
    /// (<paramref name="targetL"/>, <paramref name="targetT"/>), hangs the whole
    /// lot on <paramref name="annot"/>, and returns the rect it occupies.
    ///
    /// Separate from annotation creation so undo can re-attach text to an
    /// annotation <see cref="AddAnnotationFromSpec"/> has already made, the way
    /// <c>AttachStampImageLocked</c> re-attaches pixels.
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
            // Lay the lines out around an arbitrary origin first. Their absolute
            // position does not matter yet: the block is measured and moved into
            // place as a whole, which is what makes the result independent of any
            // assumption about ascent.
            double leading = content.FontSize * TextBoxContent.LineHeight;
            string[] lines = content.Lines;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0)
                {
                    continue; // a blank line still advances the leading, draws nothing
                }

                IntPtr obj = PdfiumNative.NewTextObject(_handle, font, (float)content.FontSize);
                if (obj == IntPtr.Zero)
                {
                    throw new PdfiumException("Could not create the text object.", 1);
                }
                objects.Add(obj);

                if (!PdfiumNative.SetTextObjectText(obj, lines[i]))
                {
                    throw new PdfiumException("PDFium rejected the text.", 1);
                }
                PdfiumNative.SetObjectFillColor(obj, content.R, content.G, content.B, 255);
                PdfiumNative.TransformPageObject(obj, 1, 0, 0, 1, 0, -i * leading);
            }

            if (objects.Count == 0)
            {
                throw new PdfiumException("There was no text to write.", 1);
            }

            var measured = MeasureLocked(objects)
                ?? throw new PdfiumException("PDFium could not measure the text.", 1);

            // Move the whole block so its measured top-left is where it was
            // asked for. Transforms accumulate, so this composes with the
            // per-line offsets already applied.
            double dx = targetL - measured.L;
            double dy = targetT - measured.T;
            foreach (IntPtr obj in objects)
            {
                PdfiumNative.TransformPageObject(obj, 1, 0, 0, 1, dx, dy);
            }

            var placed = ((float)(measured.L + dx), (float)(measured.B + dy),
                          (float)(measured.R + dx), (float)(measured.T + dy));

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
