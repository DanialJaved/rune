namespace Folio.Engine;

/// <summary>
/// Immutable vertical-stack layout of all pages at a given zoom and rotation:
/// each page's rectangle in document space (DIPs), centered horizontally.
/// Rebuilt whenever zoom or rotation changes — construction is O(pages) and
/// allocation-light, so that's fine even for huge documents.
/// </summary>
public sealed class PageLayout
{
    public const double PageGap = 16;
    public const double Margin = 24;

    private readonly DipRect[] _pageRects;

    public double Zoom { get; }
    public int Rotation { get; }
    public double TotalWidth { get; }
    public double TotalHeight { get; }
    public int PageCount => _pageRects.Length;

    /// <param name="pageSizesPoints">Page sizes in PDF points.</param>
    /// <param name="zoom">DIPs per PDF point (1.0 ≈ 100% at 96 DPI).</param>
    /// <param name="rotation">View rotation in quarter turns clockwise (0–3).</param>
    /// <param name="minViewportWidth">Pages are centered within at least this width.</param>
    public PageLayout(IReadOnlyList<(float Width, float Height)> pageSizesPoints, double zoom, int rotation, double minViewportWidth = 0)
    {
        Zoom = zoom;
        Rotation = ((rotation % 4) + 4) % 4;

        _pageRects = new DipRect[pageSizesPoints.Count];

        double maxWidth = 0;
        for (int i = 0; i < pageSizesPoints.Count; i++)
        {
            maxWidth = Math.Max(maxWidth, RotatedWidth(pageSizesPoints[i]) * zoom);
        }
        TotalWidth = Math.Max(maxWidth + 2 * Margin, minViewportWidth);

        double y = Margin;
        for (int i = 0; i < pageSizesPoints.Count; i++)
        {
            double width = RotatedWidth(pageSizesPoints[i]) * zoom;
            double height = RotatedHeight(pageSizesPoints[i]) * zoom;
            _pageRects[i] = new DipRect((TotalWidth - width) / 2, y, width, height);
            y += height + PageGap;
        }
        TotalHeight = y - PageGap + Margin;
    }

    private double RotatedWidth((float Width, float Height) size) => Rotation % 2 == 0 ? size.Width : size.Height;
    private double RotatedHeight((float Width, float Height) size) => Rotation % 2 == 0 ? size.Height : size.Width;

    public DipRect GetPageRect(int pageIndex) => _pageRects[pageIndex];

    /// <summary>Pages whose rects intersect the vertical span [top, bottom). Binary search — O(log n).</summary>
    public (int First, int Last) PagesInVerticalRange(double top, double bottom)
    {
        if (_pageRects.Length == 0 || bottom <= top)
        {
            return (0, -1);
        }

        int first = FindFirstPageBelow(top);
        int last = first;
        while (last + 1 < _pageRects.Length && _pageRects[last + 1].Y < bottom)
        {
            last++;
        }
        return _pageRects[first].Y >= bottom ? (0, -1) : (first, last);
    }

    /// <summary>The page whose vertical span contains y (or the nearest page).</summary>
    public int PageAt(double y)
    {
        if (_pageRects.Length == 0)
        {
            return 0;
        }
        return FindFirstPageBelow(y);
    }

    /// <summary>Index of the first page whose bottom edge is below y.</summary>
    private int FindFirstPageBelow(double y)
    {
        int lo = 0, hi = _pageRects.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_pageRects[mid].Bottom + PageGap <= y)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }
}
