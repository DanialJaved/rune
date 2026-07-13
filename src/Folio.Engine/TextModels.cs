namespace Folio.Engine;

/// <summary>
/// A rectangle over page text, in page points with a top-left origin — the
/// same convention as <see cref="PdfLink"/>, so multiplying by zoom yields a
/// layout-space rectangle for drawing selection/search highlights.
/// </summary>
public readonly record struct TextRect(double X, double Y, double Width, double Height);

/// <summary>A single occurrence of a search term on a page.</summary>
public sealed record SearchHit(int PageIndex, int CharIndex, int Length, IReadOnlyList<TextRect> Rects);

/// <summary>The result of a text selection: the covered char range, its text, and its rectangles.</summary>
public sealed record TextSelection(int PageIndex, int Start, int Count, string Text, IReadOnlyList<TextRect> Rects);
