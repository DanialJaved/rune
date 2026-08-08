namespace Rune.Engine;

/// <summary>What <see cref="SignatureMatte.RemoveBackground"/> decided to do.</summary>
public enum MatteOutcome
{
    /// <summary>The paper was keyed out.</summary>
    Keyed,

    /// <summary>The source already carried transparency, so it was left alone.</summary>
    SkippedHasAlpha,

    /// <summary>Nothing in the frame was dark enough to be ink. The source was left alone.</summary>
    NoInkFound,
}

/// <summary>Knobs for <see cref="SignatureMatte.RemoveBackground"/>. All of them are structural — the
/// thresholds themselves are derived from the image, which is what lets the feature ship without a slider.</summary>
public sealed record MatteOptions
{
    /// <summary>Transparent margin left around the ink when cropping, in source pixels.</summary>
    public int Padding { get; init; } = 4;

    /// <summary>
    /// Crop to the ink's bounds.
    ///
    /// OFF for the live preview, so the previewed image keeps its dimensions and
    /// the <c>WriteableBitmap</c> can be allocated once. ON for the saved result,
    /// for the same reason SignaturePad crops drawn ink: without it the signature
    /// carries a wide transparent margin and the placed size never matches what
    /// the user saw.
    /// </summary>
    public bool Crop { get; init; } = true;

    /// <summary>Leave a source that already carries alpha alone — a hand-made transparent PNG is already done.</summary>
    public bool RespectExistingAlpha { get; init; } = true;
}

/// <summary>Straight (non-premultiplied) BGRA plus the decision that produced it.</summary>
public sealed record MatteResult(MatteOutcome Outcome, byte[] Bgra, int Width, int Height);

/// <summary>
/// Turns a photo or scan of a signature on paper into ink on transparency.
///
/// Lives in the engine rather than next to the WinUI import code so it is
/// unit-testable — the same reason <c>PdfDocument.AddStamp</c> takes raw pixels
/// rather than a file path. Rune.Tests cannot reference WinUI or Win2D.
///
/// Everything here is straight (non-premultiplied) BGRA, stride = width * 4,
/// matching AddStamp's contract on both sides.
///
/// WHY NOT A PLATFORM API. Windows AI Imaging (ImageObjectExtractor / Image
/// Foreground Extractor) needs a Copilot+ PC with an NPU and throws everywhere
/// else, needs MSIX plus the systemAIModels capability, and is a photo *subject*
/// segmentation model — it would cut out the sheet of paper, which is the
/// opposite of what a signature needs, and it cannot produce soft per-pixel
/// alpha along a pen stroke. Win2D's effect graph could do this on the GPU, but
/// the tests cannot reference Win2D and this costs ~17 ms in Release on a
/// 1024x768 buffer (measured on a 1600x1200 phone-style JPEG), so the GPU would
/// buy nothing anyone can perceive.
///
/// WHY NO SLIDER. Every threshold below is derived from the image: the paper
/// level per tile, and the ink level from the darkest marks actually present.
/// A fixed threshold is what forces a sensitivity control on the user, because
/// photographed "black" ink bottoms out around luma 40-80 rather than 0 and no
/// single constant covers both that and a clean 0-luma scan.
///
/// All arithmetic is integer, so the output is bit-exact across runs (pinned by
/// SignatureMatteTests.Deterministic_SameInputSameOutput) and tests can assert
/// exact alpha values instead of tolerances. Plain managed loops over byte[],
/// matching SignaturePad.ToStraightAlpha and DocumentView.ToBitmap —
/// AllowUnsafeBlocks is off outside Rune.PdfiumInterop.
/// </summary>
public static class SignatureMatte
{
    /// <summary>Roughly this many tiles across the long edge. Fine enough to track a hand shadow, coarse enough to be cheap.</summary>
    private const int TilesOnLongEdge = 24;

    /// <summary>Tiles never go below this, so a small image doesn't degenerate into per-pixel "paper".</summary>
    private const int MinTilePx = 8;

    /// <summary>Paper is the 90th percentile of a tile — the top 10% brightest. See <see cref="Percentile"/>.</summary>
    private const int PaperPermille = 100;

    /// <summary>
    /// The ink level is the darkest 5% OF THE INK — of the pixels already past
    /// <see cref="NoiseFloor"/> — not of the whole frame.
    ///
    /// Measuring against the frame breaks on the ordinary case. A thin-stroked
    /// signature photographed at arm's length covers well under 1% of the
    /// pixels, so any whole-frame percentile lands in the paper, the ink level
    /// collapses to noise, and the image is rejected as blank. Restricting the
    /// population to plausible ink makes the estimate independent of how much of
    /// the frame the signature fills, while still ignoring a handful of dust
    /// specks darker than the pen.
    /// </summary>
    private const int InkPermille = 50;

    /// <summary>
    /// Contrast below this is paper texture, JPEG blocking or flat-field
    /// residual near a tile seam, not ink. 20/255 is about 8%, roughly twice
    /// the worst residual measured on a hand-shadowed phone photo.
    /// </summary>
    private const int NoiseFloor = 20;

    /// <summary>
    /// If the darkest thing in the frame is under this much contrast there is no
    /// signature here — a photo of a blank sheet, or a badly underexposed one.
    /// Without the guard the data-driven ink level collapses onto sensor noise
    /// and the whole page keys as a grey wash.
    /// </summary>
    private const int MinInkContrast = 40;

    /// <summary>
    /// A tile whose paper estimate falls below this fraction of the global paper
    /// level is ink, not paper. Black pen photographs at 8-32% of paper; paper
    /// in shadow rarely drops past 50%, which would be a 2.5-stop falloff across
    /// a single frame. 45% splits them with room on both sides.
    /// </summary>
    private const int InkTilePercent = 45;

    /// <summary>
    /// Below this alpha the unmix is skipped and the source colour passes
    /// through. Dividing by alpha amplifies noise by 1/a, so at a=0.05 a +/-3
    /// luma sensor wobble becomes +/-60 — colour confetti along faint edges.
    /// </summary>
    private const int UnmixFloor = 64;

    /// <summary>
    /// A row or column counts as holding ink once its alpha sums past this —
    /// about two solid pixels' worth. A plain "any ink" bounding box is defeated
    /// by one surviving speck in a far corner, and then the crop stops matching
    /// the signature, which is the only reason the crop exists.
    /// </summary>
    private const int OccupiedAlphaSum = 2 * 255;

    /// <summary>
    /// Keys the paper out of a photographed or scanned signature.
    /// </summary>
    /// <param name="bgra">Straight (non-premultiplied) BGRA, 4 bytes each, stride = width * 4.</param>
    /// <returns>
    /// The keyed pixels, or — for <see cref="MatteOutcome.SkippedHasAlpha"/> and
    /// <see cref="MatteOutcome.NoInkFound"/> — <paramref name="bgra"/> itself, so
    /// callers have one code path either way.
    /// </returns>
    public static MatteResult RemoveBackground(byte[] bgra, int width, int height, MatteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (bgra.Length < (long)width * height * 4)
        {
            throw new ArgumentException(
                $"Buffer holds {bgra.Length} bytes, short of the {(long)width * height * 4} a " +
                $"{width}x{height} BGRA image needs.",
                nameof(bgra));
        }

        var opts = options ?? new MatteOptions();

        if (opts.RespectExistingAlpha && HasMeaningfulAlpha(bgra, width, height))
        {
            return new MatteResult(MatteOutcome.SkippedHasAlpha, bgra, width, height);
        }

        int pixels = width * height;
        int tile = Math.Max(MinTilePx, Math.Max(width, height) / TilesOnLongEdge);
        int cols = Math.Max(1, (width + tile - 1) / tile);
        int rows = Math.Max(1, (height + tile - 1) / tile);

        byte[] paper = EstimatePaperField(
            bgra, width, height, tile, cols, rows,
            out int paperB, out int paperG, out int paperR, out int paperLuma);

        // How far below its own local paper each pixel sits, 0-255. Held as a
        // plane rather than recomputed, because the ink level that turns it into
        // alpha isn't known until the whole histogram has been seen.
        var contrast = new byte[pixels];
        var contrastHistogram = new int[256];
        int inkPixels = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = (row + x) * 4;
                int luma = Luma(bgra[i], bgra[i + 1], bgra[i + 2]);
                int local = PaperAt(paper, cols, rows, tile, x, y);
                int t = local <= 0 ? 0 : (local - luma) * 255 / local;
                t = Math.Clamp(t, 0, 255);
                contrast[row + x] = (byte)t;
                contrastHistogram[t]++;
                if (t > NoiseFloor)
                {
                    inkPixels++;
                }
            }
        }

        int ink = Percentile(contrastHistogram, inkPixels, InkPermille, NoiseFloor);
        if (ink < MinInkContrast)
        {
            return new MatteResult(MatteOutcome.NoInkFound, bgra, width, height);
        }

        // Illumination scales all three channels together, but the paper's own
        // tint (cream, legal-pad yellow) is global. So one interpolated luma
        // field plus three global ratios covers tinted paper for three extra
        // numbers, instead of interpolating three fields.
        int ratioB = (paperB << 8) / Math.Max(1, paperLuma);
        int ratioG = (paperG << 8) / Math.Max(1, paperLuma);
        int ratioR = (paperR << 8) / Math.Max(1, paperLuma);

        int x0 = 0, y0 = 0, cropW = width, cropH = height;
        if (opts.Crop && InkBounds(contrast, width, height, ink, opts.Padding) is { } box)
        {
            (x0, y0, cropW, cropH) = box;
        }

        var output = new byte[cropW * cropH * 4];
        for (int y = 0; y < cropH; y++)
        {
            int sourceRow = (y0 + y) * width;
            int targetRow = y * cropW;
            for (int x = 0; x < cropW; x++)
            {
                int sx = x0 + x;
                int alpha = AlphaFor(contrast[sourceRow + sx], ink);
                if (alpha == 0)
                {
                    continue; // already zeroed, and straight alpha ignores the colour anyway
                }

                int si = (sourceRow + sx) * 4;
                int di = (targetRow + x) * 4;
                if (alpha >= UnmixFloor)
                {
                    // The observed pixel is ink composited over paper, so recover
                    // the ink: I = (O - (1-a)P) / a. At a=255 this is exactly O,
                    // so stroke cores are untouched — it exists purely so soft
                    // edges aren't washed out. A true 50%-coverage black pixel
                    // reads 128 on white paper; keyed without the unmix it
                    // composites back to 191, visibly lighter than photographed.
                    // It also keeps hue: a blue pen's paper-blended edges recover
                    // as saturated blue rather than pale lavender.
                    int local = PaperAt(paper, cols, rows, tile, sx, y0 + y);
                    output[di] = Unmix(bgra[si], (local * ratioB) >> 8, alpha);
                    output[di + 1] = Unmix(bgra[si + 1], (local * ratioG) >> 8, alpha);
                    output[di + 2] = Unmix(bgra[si + 2], (local * ratioR) >> 8, alpha);
                }
                else
                {
                    output[di] = bgra[si];
                    output[di + 1] = bgra[si + 1];
                    output[di + 2] = bgra[si + 2];
                }
                output[di + 3] = (byte)alpha;
            }
        }

        return new MatteResult(MatteOutcome.Keyed, output, cropW, cropH);
    }

    /// <summary>
    /// True when enough pixels are non-opaque that the source was authored with
    /// transparency. A JPEG decodes to all-255 and scores zero; a hand-made
    /// signature PNG is mostly transparent and scores far over the bar, while a
    /// stray semi-transparent hairline border does not trip it.
    /// </summary>
    public static bool HasMeaningfulAlpha(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        int pixels = width * height;
        if (pixels <= 0 || bgra.Length < (long)pixels * 4)
        {
            return false;
        }

        int soft = 0;
        int bar = pixels / 100;
        for (int i = 3; i < pixels * 4; i += 4)
        {
            if (bgra[i] < 250 && ++soft > bar)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Tight bounds of the opaque content plus padding, or null when nothing is solid enough.</summary>
    public static (int X, int Y, int Width, int Height)? AlphaBounds(byte[] bgra, int width, int height, int padding)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || bgra.Length < (long)width * height * 4)
        {
            return null;
        }

        int top = -1, bottom = -1;
        var columns = new int[width];
        for (int y = 0; y < height; y++)
        {
            int rowSum = 0;
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int a = bgra[(row + x) * 4 + 3];
                rowSum += a;
                columns[x] += a;
            }
            if (rowSum > OccupiedAlphaSum)
            {
                if (top < 0)
                {
                    top = y;
                }
                bottom = y;
            }
        }
        return Bounds(top, bottom, columns, width, height, padding);
    }

    /// <summary>
    /// Converts straight (non-premultiplied) BGRA to premultiplied, into a new buffer.
    ///
    /// The inverse of SignaturePad.ToStraightAlpha, and needed for the opposite
    /// reason: PDFium composites straight alpha, but Direct2D only accepts
    /// PREMULTIPLIED (or ignored) alpha for a bitmap it has to draw. Handing
    /// Win2D a straight-alpha bitmap fails with WINCODEC_ERR_UNSUPPORTED-
    /// PIXELFORMAT (0x88982F80) rather than rendering badly, which is why the
    /// on-page hover preview drew nothing at all until this existed.
    ///
    /// Returns a copy: the caller's buffer is the one that gets stamped into the
    /// PDF and must stay straight.
    /// </summary>
    public static byte[] ToPremultiplied(byte[] straightBgra)
    {
        ArgumentNullException.ThrowIfNull(straightBgra);

        var output = new byte[straightBgra.Length];
        for (int i = 0; i + 3 < straightBgra.Length; i += 4)
        {
            byte a = straightBgra[i + 3];
            output[i + 3] = a;
            if (a == 255)
            {
                output[i] = straightBgra[i];
                output[i + 1] = straightBgra[i + 1];
                output[i + 2] = straightBgra[i + 2];
            }
            else if (a != 0)
            {
                output[i] = (byte)(straightBgra[i] * a / 255);
                output[i + 1] = (byte)(straightBgra[i + 1] * a / 255);
                output[i + 2] = (byte)(straightBgra[i + 2] * a / 255);
            }
            // a == 0 leaves the pixel at zero, which is what premultiplied means.
        }
        return output;
    }

    /// <summary>Copies a sub-rectangle out of a BGRA buffer. Shared by every crop path.</summary>
    public static (byte[] Bgra, int Width, int Height) CropTo(
        byte[] bgra, int width, int height, int x, int y, int cropWidth, int cropHeight)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cropWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cropHeight);
        if (x < 0 || y < 0 || x + cropWidth > width || y + cropHeight > height)
        {
            throw new ArgumentException(
                $"Crop {x},{y} {cropWidth}x{cropHeight} falls outside a {width}x{height} image.", nameof(bgra));
        }

        var output = new byte[cropWidth * cropHeight * 4];
        for (int row = 0; row < cropHeight; row++)
        {
            Array.Copy(
                bgra, ((y + row) * width + x) * 4,
                output, row * cropWidth * 4,
                cropWidth * 4);
        }
        return (output, cropWidth, cropHeight);
    }

    // ---- internals ----

    /// <summary>ITU-R BT.601 luma, scaled by 256 so the weights sum exactly and no float is involved.</summary>
    private static int Luma(byte b, byte g, byte r) => (77 * r + 150 * g + 29 * b) >> 8;

    /// <summary>Recovers one channel of the ink from the observed pixel: I = (O - (1-a)P) / a.</summary>
    private static byte Unmix(byte observed, int paper, int alpha)
    {
        int value = (observed * 255 - (255 - alpha) * paper) / alpha;
        return (byte)Math.Clamp(value, 0, 255);
    }

    /// <summary>Contrast to alpha: a hard floor for noise, then straight proportion up to the ink level.</summary>
    /// <remarks>
    /// A knee at the floor rather than a ramp from it, deliberately. A ramp
    /// compresses the whole mid-range by the floor's width, so a true 50% edge
    /// keys to 44% and composites back visibly light; the knee keeps every value
    /// above the floor proportional and costs only a step of NoiseFloor/ink at
    /// the very bottom, which over paper is a ~10%-opacity pixel — invisible.
    /// </remarks>
    private static int AlphaFor(int contrast, int ink)
    {
        if (contrast <= NoiseFloor)
        {
            return 0;
        }
        return Math.Min(255, contrast * 255 / ink);
    }

    /// <summary>Bounds of the ink in the contrast plane, without materialising an alpha buffer first.</summary>
    private static (int X, int Y, int Width, int Height)? InkBounds(
        byte[] contrast, int width, int height, int ink, int padding)
    {
        int top = -1, bottom = -1;
        var columns = new int[width];
        for (int y = 0; y < height; y++)
        {
            int rowSum = 0;
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int a = AlphaFor(contrast[row + x], ink);
                rowSum += a;
                columns[x] += a;
            }
            if (rowSum > OccupiedAlphaSum)
            {
                if (top < 0)
                {
                    top = y;
                }
                bottom = y;
            }
        }
        return Bounds(top, bottom, columns, width, height, padding);
    }

    /// <summary>Turns row extents plus column sums into a padded rectangle. Shared by both bounds passes.</summary>
    private static (int X, int Y, int Width, int Height)? Bounds(
        int top, int bottom, int[] columns, int width, int height, int padding)
    {
        if (top < 0)
        {
            return null;
        }

        int left = -1, right = -1;
        for (int x = 0; x < width; x++)
        {
            if (columns[x] > OccupiedAlphaSum)
            {
                if (left < 0)
                {
                    left = x;
                }
                right = x;
            }
        }
        if (left < 0)
        {
            return null;
        }

        padding = Math.Max(0, padding);
        left = Math.Max(0, left - padding);
        top = Math.Max(0, top - padding);
        right = Math.Min(width - 1, right + padding);
        bottom = Math.Min(height - 1, bottom + padding);
        return (left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>
    /// Paper brightness per tile, as a coarse grid to be interpolated back up.
    ///
    /// A phone photo of paper is never evenly lit — a hand shadow across one
    /// corner drops luma 30-60, which is wider than the entire ink ramp, so a
    /// single global threshold keys that corner as opaque grey. Estimating paper
    /// locally is what makes the shadow a non-event.
    /// </summary>
    private static byte[] EstimatePaperField(
        byte[] bgra, int width, int height, int tile, int cols, int rows,
        out int paperB, out int paperG, out int paperR, out int paperLuma)
    {
        var field = new byte[cols * rows];
        var tileHistogram = new int[256];
        var globalLuma = new int[256];
        var globalB = new int[256];
        var globalG = new int[256];
        var globalR = new int[256];

        for (int r = 0; r < rows; r++)
        {
            int yStart = r * tile;
            int yEnd = Math.Min(height, yStart + tile);
            for (int c = 0; c < cols; c++)
            {
                int xStart = c * tile;
                int xEnd = Math.Min(width, xStart + tile);

                Array.Clear(tileHistogram);
                int count = 0;
                for (int y = yStart; y < yEnd; y++)
                {
                    int i = (y * width + xStart) * 4;
                    for (int x = xStart; x < xEnd; x++, i += 4)
                    {
                        byte b = bgra[i], g = bgra[i + 1], red = bgra[i + 2];
                        int luma = Luma(b, g, red);
                        tileHistogram[luma]++;
                        globalLuma[luma]++;
                        globalB[b]++;
                        globalG[g]++;
                        globalR[red]++;
                        count++;
                    }
                }

                field[r * cols + c] = count == 0 ? (byte)0 : (byte)Percentile(tileHistogram, count, PaperPermille);
            }
        }

        int pixels = width * height;
        paperLuma = Percentile(globalLuma, pixels, PaperPermille);
        paperB = Percentile(globalB, pixels, PaperPermille);
        paperG = Percentile(globalG, pixels, PaperPermille);
        paperR = Percentile(globalR, pixels, PaperPermille);

        FillInkTiles(field, cols, rows, paperLuma);
        return field;
    }

    /// <summary>
    /// Replaces tiles that are mostly ink with paper borrowed from their neighbours.
    ///
    /// A tile's 90th percentile only lands on ink when more than 90% of the tile
    /// IS ink — a bold marker signature shot close up. Left alone, that tile's
    /// "paper is this dark" estimate punches a hole straight through the middle
    /// of the stroke. A 3x3 dilation is not enough to fix it: a stroke five tiles
    /// thick has a centre tile whose entire neighbourhood is also ink, so the
    /// fill has to propagate ring by ring until it reaches real paper.
    /// </summary>
    private static void FillInkTiles(byte[] field, int cols, int rows, int globalPaper)
    {
        int floor = globalPaper * InkTilePercent / 100;
        var known = new bool[field.Length];
        int unknown = 0;
        for (int i = 0; i < field.Length; i++)
        {
            known[i] = field[i] >= floor;
            if (!known[i])
            {
                unknown++;
            }
        }

        if (unknown == 0)
        {
            return;
        }
        if (unknown == field.Length)
        {
            // Every tile reads as ink — a close-up of a stroke and nothing else.
            // There is no local paper left to trust, so fall back to the global.
            Array.Fill(field, (byte)globalPaper);
            return;
        }

        // One ring per pass, bounded by the grid's diameter so it terminates even
        // if a region is fully enclosed. Reads only from the previous pass's
        // known set, so a tile never seeds itself from another tile's guess.
        for (int pass = 0; pass < cols + rows && unknown > 0; pass++)
        {
            var next = (bool[])known.Clone();
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    if (known[i])
                    {
                        continue;
                    }

                    int best = 0;
                    for (int dr = -1; dr <= 1; dr++)
                    {
                        int nr = r + dr;
                        if (nr < 0 || nr >= rows)
                        {
                            continue;
                        }
                        for (int dc = -1; dc <= 1; dc++)
                        {
                            int nc = c + dc;
                            if (nc < 0 || nc >= cols)
                            {
                                continue;
                            }
                            int j = nr * cols + nc;
                            if (known[j] && field[j] > best)
                            {
                                best = field[j];
                            }
                        }
                    }

                    if (best > 0)
                    {
                        field[i] = (byte)best;
                        next[i] = true;
                        unknown--;
                    }
                }
            }
            known = next;
        }
    }

    /// <summary>
    /// Bilinearly samples the tile grid at a pixel.
    ///
    /// Tile c's centre sits at pixel c*tile + tile/2, so the sample position in
    /// tile units is (x - tile/2) / tile. Kept in 8-bit fixed point so the whole
    /// pass stays integer and the result is reproducible.
    /// </summary>
    private static int PaperAt(byte[] field, int cols, int rows, int tile, int x, int y)
    {
        Locate(x, tile, cols, out int c0, out int c1, out int fx);
        Locate(y, tile, rows, out int r0, out int r1, out int fy);

        int topLeft = field[r0 * cols + c0];
        int topRight = field[r0 * cols + c1];
        int bottomLeft = field[r1 * cols + c0];
        int bottomRight = field[r1 * cols + c1];

        int top = topLeft + (((topRight - topLeft) * fx) >> 8);
        int bottom = bottomLeft + (((bottomRight - bottomLeft) * fx) >> 8);
        return top + (((bottom - top) * fy) >> 8);
    }

    /// <summary>One axis of <see cref="PaperAt"/>: the two tiles to blend and the 0-255 fraction between them.</summary>
    private static void Locate(int p, int tile, int count, out int i0, out int i1, out int fraction)
    {
        int scaled = (p << 8) - (tile << 7);
        if (scaled <= 0)
        {
            // Left of the first tile's centre: clamp rather than extrapolate.
            i0 = 0;
            i1 = 0;
            fraction = 0;
            return;
        }

        int step = tile << 8;
        i0 = scaled / step;
        if (i0 >= count - 1)
        {
            i0 = count - 1;
            i1 = i0;
            fraction = 0;
            return;
        }
        i1 = i0 + 1;
        fraction = (scaled - (i0 * step)) / tile;
    }

    /// <summary>
    /// The value the top <paramref name="upperPermille"/> of samples sit at or above.
    ///
    /// A percentile rather than the maximum, because a single specular highlight
    /// or one JPEG ringing pixel sets the maximum and would inflate the paper
    /// estimate for a whole tile.
    /// </summary>
    /// <param name="floor">Bins at or below this are excluded from the search entirely.</param>
    private static int Percentile(int[] histogram, int total, int upperPermille, int floor = 0)
    {
        long target = (long)total * upperPermille / 1000;
        long seen = 0;
        for (int value = 255; value > floor; value--)
        {
            seen += histogram[value];
            if (seen > target)
            {
                return value;
            }
        }
        return 0;
    }
}
