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

/// <summary>What to write, and how it should look.</summary>
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
// PROBE CODE. Two routes are implemented here so a test can measure both,
// because which of them is viable decides the shape of the whole feature:
//
//   AddTextAnnotation   a text object hung on a stamp annotation, the way
//                       AddStamp hangs an image. Discrete and erasable, and it
//                       inherits the existing move/undo/flatten machinery.
//   AddTextToPageContent a text object inserted straight into the page's own
//                       content. Not an object the user can later grab, but it
//                       is indistinguishable from the document's own text.
//
// The open questions are whether each renders at all, and whether the text is
// reachable by search afterwards. The loser gets deleted.
public sealed partial class PdfDocument
{
    /// <summary>
    /// Writes text into a stamp annotation at a page-local top-left point.
    ///
    /// Stamp rather than FreeText on purpose: PDFium gates
    /// <c>FPDFAnnot_AppendObject</c> on its "object supported subtype" check,
    /// which admits only ink and stamp. A FreeText annotation would be refused
    /// the object, and PDFium generates no appearance for FreeText itself, so it
    /// would render as nothing at all.
    /// </summary>
    public AnnotationSpec? AddTextAnnotation(int pageIndex, double x, double y, TextBoxContent content)
        => AddTextLocked(pageIndex, x, y, content, asAnnotation: true);

    /// <summary>Writes the same text straight into the page's content stream.</summary>
    public AnnotationSpec? AddTextToPageContent(int pageIndex, double x, double y, TextBoxContent content)
        => AddTextLocked(pageIndex, x, y, content, asAnnotation: false);

    private AnnotationSpec? AddTextLocked(
        int pageIndex, double x, double y, TextBoxContent content, bool asAnnotation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

        if (string.IsNullOrEmpty(content.Text) || content.FontSize <= 0)
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

            IntPtr font = PdfiumNative.LoadStandardFont(_handle, content.PostScriptName);
            if (font == IntPtr.Zero)
            {
                ReleasePageLocked(pageIndex);
                throw new PdfiumException($"PDFium does not know the font '{content.PostScriptName}'.", 1);
            }

            try
            {
                // A single line for now: enough to answer the probe's questions.
                // The box is the text's own height; width is guessed generously
                // because measuring belongs to the caller, which has DirectWrite.
                double widthPt = content.FontSize * content.Text.Length;
                var (l, b, r, t) = ToPageRectLocked(page, pageIndex, x, y, widthPt, content.FontSize * 1.4);

                IntPtr text = PdfiumNative.NewTextObject(_handle, font, (float)content.FontSize);
                if (text == IntPtr.Zero)
                {
                    throw new PdfiumException("Could not create the text object.", 1);
                }

                if (!PdfiumNative.SetTextObjectText(text, content.Text))
                {
                    throw new PdfiumException("PDFium rejected the text.", 1);
                }
                PdfiumNative.SetObjectFillColor(text, content.R, content.G, content.B, 255);

                // The text origin is its baseline, which sits above the box's
                // bottom by the descender. A quarter of the size is close enough
                // for a probe; real metrics come later.
                PdfiumNative.TransformPageObject(text, 1, 0, 0, 1, l, b + content.FontSize * 0.25);

                if (asAnnotation)
                {
                    IntPtr annot = PdfiumNative.CreateAnnot(page, PdfiumNative.AnnotStamp);
                    if (annot == IntPtr.Zero)
                    {
                        throw new PdfiumException("Could not create the text annotation.", 1);
                    }
                    try
                    {
                        PdfiumNative.SetAnnotRect(annot, l, b, r, t);
                        if (!PdfiumNative.AppendAnnotObject(annot, text))
                        {
                            throw new PdfiumException("PDFium refused the text object on the annotation.", 1);
                        }
                        PdfiumNative.SetAnnotPrintFlag(annot);
                        // The written text also goes in /Contents: it is where a
                        // reader looks for an annotation's text, and it is how
                        // Rune will later tell a text stamp from a picture one.
                        PdfiumNative.SetAnnotString(annot, "Contents", content.Text);
                    }
                    finally
                    {
                        PdfiumNative.CloseAnnot(annot);
                    }
                }
                else
                {
                    PdfiumNative.InsertPageObject(page, text);
                    if (!PdfiumNative.GenerateContent(page))
                    {
                        throw new PdfiumException("Could not regenerate the page content.", 1);
                    }
                }

                spec = new AnnotationSpec(
                    pageIndex,
                    PdfiumNative.AnnotStamp,
                    Quads: [],
                    InkStrokes: [],
                    Rect: (l, b, r, t),
                    Color: (content.R, content.G, content.B, 255),
                    BorderWidth: 0,
                    Contents: content.Text);
            }
            finally
            {
                PdfiumNative.CloseFont(font);
                ReleasePageLocked(pageIndex);
            }
        }

        IsDirty = true;
        return spec;
    }
}
