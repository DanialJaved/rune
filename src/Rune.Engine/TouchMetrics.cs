namespace Rune.Engine;

/// <summary>
/// The handful of numbers that have to differ between a fingertip and a mouse
/// pointer, in one place so they can be pinned by test.
///
/// A mouse cursor is one pixel and lands exactly where it is aimed, so every hit
/// test in Rune was written with no tolerance at all: a link, a form field or a
/// placed picture was hit only inside its own rect. A fingertip covers roughly
/// 9 mm of glass and reports a single point somewhere near the middle of that
/// contact, so the same tests miss constantly. Every value here is therefore
/// zero (or the old constant) for a mouse and nothing about mouse behaviour
/// moves.
///
/// The DIP-to-page-point conversions divide by the zoom because the tolerance
/// belongs to the finger on the glass, not to the document: half a centimetre
/// stays half a centimetre whether the page is at 25% or 400%, which in page
/// points means a much larger number when zoomed out and a much smaller one when
/// zoomed in.
/// </summary>
public static class TouchMetrics
{
    /// <summary>How far off a target a finger may land and still hit it, in DIPs.</summary>
    public const double SlopDip = 8;

    /// <summary>The drawn size of a corner resize handle, in DIPs.</summary>
    public const double HandleDrawDip = 10;

    /// <summary>
    /// How far a finger may miss a corner handle and still take it, in DIPs.
    /// Deliberately far larger than the drawn size: 10 px is a fair mouse target
    /// and an unhittable touch one, but drawing them at 24 px would swamp the
    /// short signature the handles are most often attached to.
    /// </summary>
    public const double HandleTouchReachDip = 24;

    /// <summary>Below this much travel, placing a stamp counts as a tap rather than a drag-to-size.</summary>
    public const double PlacementDragDip = 8;

    /// <summary>
    /// The same threshold for a finger. A finger never lands as still as a mouse
    /// click, so 8 px of jitter read as a deliberate drag and sized the picture
    /// at whatever the wobble happened to be.
    /// </summary>
    public const double PlacementDragTouchDip = 16;

    /// <summary>The zoom floor every conversion clamps to, so a degenerate zoom cannot divide by ~0.</summary>
    private const double MinZoom = 0.05;

    /// <summary>Hit-test tolerance in page points. Always 0 for a mouse or pen.</summary>
    public static double SlopPt(bool touch, double zoom)
        => touch ? SlopDip / Math.Max(MinZoom, zoom) : 0;

    /// <summary>
    /// How close to a corner a press has to land to count as grabbing its
    /// handle, in page points. The 0.9 keeps the grab just inside the drawn
    /// square for a mouse, so the handle cannot be taken from visibly off it.
    /// </summary>
    public static double HandleReachPt(bool touch, double zoom)
        => ((touch ? HandleTouchReachDip : HandleDrawDip) / Math.Max(MinZoom, zoom)) * 0.9;

    /// <summary>Tap-versus-drag threshold for stamp placement, in document DIPs.</summary>
    public static double PlacementDragThreshold(bool touch)
        => touch ? PlacementDragTouchDip : PlacementDragDip;
}
