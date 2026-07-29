namespace Rune.Engine;

/// <summary>
/// View-rotation arithmetic, in quarter turns clockwise (0–3).
///
/// Split out because C#'s <c>%</c> keeps the sign of the left operand, so a
/// naive <c>(r - 1) % 4</c> yields -1 when rotating left from 0 — which then
/// indexes arrays and reaches PDFium as a negative rotation.
/// </summary>
public static class ViewRotationMath
{
    /// <summary>Any quarter-turn count (including negatives) → 0–3.</summary>
    public static int Normalize(int quarterTurns) => ((quarterTurns % 4) + 4) % 4;

    /// <summary>True when the rotation swaps a page's width and height.</summary>
    public static bool SwapsAxes(int quarterTurns) => Normalize(quarterTurns) % 2 == 1;
}
