namespace Rune.Engine;

/// <summary>
/// A page's full text plus one bounding box per character, extracted once on
/// the render thread. Selection hit-testing and range-rect building then run
/// entirely in managed memory — no PDFium call (and no render-thread wait) on
/// the pointer path, which is what keeps drag-selection smooth.
///
/// Boxes are in page points with a top-left origin (same convention as
/// <see cref="TextRect"/> everywhere else). Only valid for the unrotated view,
/// like all selection geometry in the app.
/// </summary>
public sealed class PageText
{
    private readonly TextRect[] _charBoxes;

    public int PageIndex { get; }
    public string Text { get; }
    public int Count => _charBoxes.Length;
    public IReadOnlyList<TextRect> CharBoxes => _charBoxes;

    public PageText(int pageIndex, string text, TextRect[] charBoxes)
    {
        PageIndex = pageIndex;
        // PDFium's per-char APIs index code units; keep text padded/truncated
        // to match so Substring over char indices can never go out of range.
        Text = text.Length == charBoxes.Length
            ? text
            : text.Length > charBoxes.Length
                ? text[..charBoxes.Length]
                : text.PadRight(charBoxes.Length);
        _charBoxes = charBoxes;
    }

    public static PageText Empty(int pageIndex) => new(pageIndex, string.Empty, []);

    /// <summary>
    /// Char index at a page-local point, or -1. Mirrors
    /// FPDFText_GetCharIndexAtPos: containment wins, else the nearest box
    /// within <paramref name="tolerance"/> points.
    /// </summary>
    public int CharIndexAt(double x, double y, double tolerance = 6)
    {
        int nearest = -1;
        double nearestDistance = double.MaxValue;

        for (int i = 0; i < _charBoxes.Length; i++)
        {
            var box = _charBoxes[i];
            if (box.Width <= 0 || box.Height <= 0)
            {
                continue; // control chars (newlines) carry no geometry
            }
            if (x >= box.X && x <= box.X + box.Width && y >= box.Y && y <= box.Y + box.Height)
            {
                return i;
            }

            double dx = x < box.X ? box.X - x : x > box.X + box.Width ? x - (box.X + box.Width) : 0;
            double dy = y < box.Y ? box.Y - y : y > box.Y + box.Height ? y - (box.Y + box.Height) : 0;
            double distance = Math.Max(dx, dy);
            if (distance <= tolerance && distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = i;
            }
        }
        return nearest;
    }

    /// <summary>Builds a selection between two char indices (inclusive, either order).</summary>
    public TextSelection GetSelection(int anchorChar, int focusChar)
    {
        if (Count == 0)
        {
            return new TextSelection(PageIndex, 0, 0, string.Empty, []);
        }
        int start = Math.Clamp(Math.Min(anchorChar, focusChar), 0, Count - 1);
        int end = Math.Clamp(Math.Max(anchorChar, focusChar), 0, Count - 1);
        int count = end - start + 1;
        return new TextSelection(PageIndex, start, count, Text.Substring(start, count), RangeRects(start, count));
    }

    /// <summary>
    /// Merges the char boxes of a range into per-line rectangles (chars join a
    /// line while they vertically overlap it), approximating
    /// FPDFText_CountRects/GetRect closely enough for highlights.
    /// </summary>
    public List<TextRect> RangeRects(int start, int count)
    {
        var rects = new List<TextRect>();
        double lineX1 = 0, lineY1 = 0, lineX2 = 0, lineY2 = 0;
        bool lineOpen = false;

        for (int i = start; i < start + count && i < _charBoxes.Length; i++)
        {
            var box = _charBoxes[i];
            if (box.Width <= 0 || box.Height <= 0)
            {
                continue;
            }

            if (lineOpen && VerticalOverlap(lineY1, lineY2, box.Y, box.Y + box.Height))
            {
                lineX1 = Math.Min(lineX1, box.X);
                lineY1 = Math.Min(lineY1, box.Y);
                lineX2 = Math.Max(lineX2, box.X + box.Width);
                lineY2 = Math.Max(lineY2, box.Y + box.Height);
            }
            else
            {
                if (lineOpen)
                {
                    rects.Add(new TextRect(lineX1, lineY1, lineX2 - lineX1, lineY2 - lineY1));
                }
                (lineX1, lineY1, lineX2, lineY2) = (box.X, box.Y, box.X + box.Width, box.Y + box.Height);
                lineOpen = true;
            }
        }
        if (lineOpen)
        {
            rects.Add(new TextRect(lineX1, lineY1, lineX2 - lineX1, lineY2 - lineY1));
        }
        return rects;
    }

    /// <summary>True when the ranges overlap by at least half the smaller height (same text line).</summary>
    private static bool VerticalOverlap(double topA, double bottomA, double topB, double bottomB)
    {
        double overlap = Math.Min(bottomA, bottomB) - Math.Max(topA, topB);
        double minHeight = Math.Min(bottomA - topA, bottomB - topB);
        return minHeight > 0 && overlap >= minHeight * 0.5;
    }
}
