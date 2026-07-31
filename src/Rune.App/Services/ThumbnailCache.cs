using System.Security.Cryptography;
using System.Text;
using Rune.Engine;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Rune.Services;

/// <summary>
/// Renders and disk-caches first-page thumbnails for the recent-documents
/// homepage. Cache key is sha256(path|mtime) so a modified file re-renders.
/// Everything runs on background threads; results are returned as encoded PNG
/// bytes the caller turns into a bitmap on the UI thread.
/// </summary>
public sealed class ThumbnailCache : IDisposable
{
    private const int TargetWidthPx = 320;
    private const int MaxCachedFiles = 24;

    private readonly string _dir;

    /// <summary>
    /// A serialized PDFium thread of this cache's own. Homepage thumbnails open
    /// short-lived documents unrelated to any open tab, so they have no viewer
    /// scheduler to borrow — but they must not run on pool threads either: two
    /// threads inside PDFium contend on the global lock, and a homepage render
    /// would stall the render thread mid-scroll. No tiles are ever scheduled
    /// here, so the tile callback is never invoked.
    /// </summary>
    private readonly RenderScheduler _pdfThread = new((_, _) => { });

    public ThumbnailCache()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Rune", "thumbnails");
        Directory.CreateDirectory(_dir);
    }

    /// <summary>
    /// Returns PNG bytes for the file's first-page thumbnail, from cache or
    /// freshly rendered. Returns null if the file can't be opened/rendered.
    /// </summary>
    public async Task<byte[]?> GetAsync(string pdfPath)
    {
        return await Task.Run(async () =>
        {
            try
            {
                if (!File.Exists(pdfPath))
                {
                    return null;
                }

                string key = CacheKey(pdfPath);
                string cachePath = Path.Combine(_dir, key + ".png");
                if (File.Exists(cachePath))
                {
                    return await File.ReadAllBytesAsync(cachePath);
                }

                byte[] png = EncodePng(await _pdfThread.RunAsync(PdfWorkPriority.Thumbnail, () => RenderRaw(pdfPath)));
                await File.WriteAllBytesAsync(cachePath, png);
                Prune();
                return png;
            }
            catch
            {
                return null;
            }
        });
    }

    /// <summary>Raw BGRA pixels detached from the pooled render buffer, safe to hand off the PDFium thread.</summary>
    private readonly record struct RawImage(byte[] Pixels, int Width, int Height);

    /// <summary>Runs on the PDFium thread. PNG encoding deliberately does not — it needs no PDFium state.</summary>
    private static RawImage RenderRaw(string pdfPath)
    {
        using var doc = PdfDocument.Open(pdfPath);
        var (ptWidth, _) = doc.GetPageSize(0);
        float scale = TargetWidthPx / Math.Max(1f, ptWidth);
        var page = doc.RenderPage(0, scale);
        try
        {
            // The pooled buffer may be longer than the image; copy the exact bytes.
            var pixels = new byte[page.Stride * page.Height];
            Array.Copy(page.Pixels, pixels, pixels.Length);
            return new RawImage(pixels, page.Width, page.Height);
        }
        finally
        {
            page.Return();
        }
    }

    private static byte[] EncodePng(RawImage image)
    {
        // BitmapEncoder needs a random-access stream; encode in-memory to bytes.
        using var stream = new InMemoryRandomAccessStream();
        var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask().GetAwaiter().GetResult();

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)image.Width, (uint)image.Height, 96, 96, image.Pixels);
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();

        stream.Seek(0);
        var reader = new DataReader(stream.GetInputStreamAt(0));
        uint size = (uint)stream.Size;
        reader.LoadAsync(size).AsTask().GetAwaiter().GetResult();
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static string CacheKey(string pdfPath)
    {
        string stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(pdfPath).Ticks.ToString();
        }
        catch
        {
            stamp = "0";
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(pdfPath.ToLowerInvariant() + "|" + stamp));
        return Convert.ToHexString(hash)[..24];
    }

    private void Prune()
    {
        try
        {
            var files = new DirectoryInfo(_dir).GetFiles("*.png")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(MaxCachedFiles);
            foreach (var f in files)
            {
                f.Delete();
            }
        }
        catch
        {
            // Pruning is best-effort.
        }
    }

    public void Dispose() => _pdfThread.Dispose();
}
