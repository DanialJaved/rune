namespace Rune.Engine;

/// <summary>
/// A point on the page, in PDF points from that page's top-left corner.
///
/// This is the one description of "where the user is looking" that survives a
/// zoom change untouched — which is exactly why zoom anchoring is expressed in
/// it rather than in document coordinates.
/// </summary>
public readonly record struct PageAnchor(int PageIndex, double LocalX, double LocalY);

/// <summary>
/// Keeps a chosen point stationary on screen across a zoom change.
///
/// The naive approach — scale the scroll offset by the zoom ratio — is wrong,
/// because <see cref="PageLayout"/> is affine rather than a pure scale:
/// <see cref="PageLayout.Margin"/> and <see cref="PageLayout.PageGap"/> are
/// constant DIPs that do not grow with zoom, pages are centred horizontally
/// inside the viewport width, and a document shorter than the viewport is
/// centred vertically too. Multiplying a document coordinate by the zoom ratio
/// therefore mis-scales all of those constants, and because the gap is added
/// once per page the error accumulates with page index — zoom drifts a little
/// on page 1 and badly on page 400.
///
/// Capturing the anchor in page space sidesteps the whole problem: the page
/// rectangle is looked up fresh from the new layout, so margins, gaps and
/// centring are whatever that layout says they are.
/// </summary>
public static class ZoomAnchor
{
    /// <summary>
    /// Describes the document point currently under <paramref name="viewportX"/>,
    /// <paramref name="viewportY"/> (a position within the viewport, e.g. the
    /// mouse cursor) as a page-relative anchor.
    /// </summary>
    /// <param name="layout">The layout in effect right now.</param>
    /// <param name="scrollX">Current horizontal scroll offset.</param>
    /// <param name="scrollY">Current vertical scroll offset.</param>
    public static PageAnchor Capture(
        PageLayout layout, double scrollX, double scrollY, double viewportX, double viewportY)
    {
        double documentX = scrollX + viewportX;
        double documentY = scrollY + viewportY;

        int page = layout.PageAt(documentY);
        var rect = layout.GetPageRect(page);

        // Divide out the zoom so the result is in PDF points — the same
        // convention TextRect, AnnotationInfo and FormFieldInfo all use.
        return new PageAnchor(
            page,
            (documentX - rect.X) / layout.Zoom,
            (documentY - rect.Y) / layout.Zoom);
    }

    /// <summary>
    /// The scroll offsets that put <paramref name="anchor"/> back under
    /// <paramref name="viewportX"/>, <paramref name="viewportY"/> in a new
    /// layout. Offsets may fall outside the scrollable range; the caller is
    /// expected to let the ScrollViewer clamp them.
    /// </summary>
    public static (double X, double Y) Restore(
        PageLayout layout, PageAnchor anchor, double viewportX, double viewportY)
    {
        // Clamp: a page mutation between capture and restore can leave the
        // anchor pointing past the end of a now-shorter document.
        int page = Math.Clamp(anchor.PageIndex, 0, Math.Max(0, layout.PageCount - 1));
        var rect = layout.GetPageRect(page);

        return (rect.X + anchor.LocalX * layout.Zoom - viewportX,
                rect.Y + anchor.LocalY * layout.Zoom - viewportY);
    }
}
