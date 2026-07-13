namespace Rune.Engine;

/// <summary>
/// Identity of one rendered tile. ScaleKey is the render scale in thousandths
/// (px per PDF point × 1000) so float scales compare exactly.
/// Row/Col are (0,0) for whole-page renders and previews.
/// </summary>
public readonly record struct TileKey(int PageIndex, int ScaleKey, int Rotation, int Row, int Col, bool IsPreview)
{
    public static int ToScaleKey(float scale) => (int)MathF.Round(scale * 1000f);
}

/// <summary>Everything the render thread needs to produce one tile.</summary>
public sealed record TileRequest(
    TileKey Key,
    float Scale,
    int SrcX, int SrcY,
    int WidthPx, int HeightPx);

public static class TileMath
{
    /// <summary>Pages up to this size (px, either dimension) render as one bitmap.</summary>
    public const int MaxSingleTilePx = 2048;

    /// <summary>Tile edge for pages that exceed <see cref="MaxSingleTilePx"/>.</summary>
    public const int TileSizePx = 1024;

    public static (int Cols, int Rows) GridFor(int pageWidthPx, int pageHeightPx)
    {
        if (pageWidthPx <= MaxSingleTilePx && pageHeightPx <= MaxSingleTilePx)
        {
            return (1, 1);
        }
        return ((pageWidthPx + TileSizePx - 1) / TileSizePx,
                (pageHeightPx + TileSizePx - 1) / TileSizePx);
    }

    /// <summary>Pixel window of a tile within its page.</summary>
    public static (int SrcX, int SrcY, int Width, int Height) TileWindow(
        int pageWidthPx, int pageHeightPx, int row, int col)
    {
        var (cols, rows) = GridFor(pageWidthPx, pageHeightPx);
        if (cols == 1 && rows == 1)
        {
            return (0, 0, pageWidthPx, pageHeightPx);
        }
        int x = col * TileSizePx;
        int y = row * TileSizePx;
        return (x, y, Math.Min(TileSizePx, pageWidthPx - x), Math.Min(TileSizePx, pageHeightPx - y));
    }
}
