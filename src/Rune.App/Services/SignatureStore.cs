using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Rune.Services;

/// <summary>A signature as straight (non-premultiplied) BGRA pixels, plus where it came from.</summary>
public sealed record SavedSignature(string Path, byte[] Bgra, int Width, int Height);

/// <summary>
/// Keeps drawn and imported signatures on disk so they can be reused.
///
/// Lives in its own <c>%LOCALAPPDATA%\Rune\signatures</c> directory, NOT
/// alongside the homepage thumbnails: that folder deletes everything past the
/// newest 24 files after every render (ThumbnailCache.Prune), which would
/// silently eat saved signatures.
///
/// These are personal data — a picture of the user's handwriting — so they
/// never leave the device and are enumerated in PRIVACY.md.
/// </summary>
public sealed class SignatureStore
{
    private readonly string _dir;

    public SignatureStore(string? directory = null)
    {
        _dir = directory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Rune", "signatures");
        Directory.CreateDirectory(_dir);
    }

    /// <summary>Newest first, so the most recently added is the easiest to reach.</summary>
    /// <param name="maxDimension">Longest edge in pixels; 0 decodes at native size.</param>
    public async Task<IReadOnlyList<SavedSignature>> ListAsync(int maxDimension = 0)
    {
        var result = new List<SavedSignature>();
        List<FileInfo> files;
        try
        {
            files = [.. new DirectoryInfo(_dir).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc)];
        }
        catch (IOException)
        {
            return result;
        }

        foreach (var file in files)
        {
            var loaded = await TryLoadAsync(file.FullName, maxDimension);
            if (loaded is not null)
            {
                result.Add(loaded);
            }
        }
        return result;
    }

    /// <summary>Decodes an image file to straight BGRA. Returns null if it can't be read.</summary>
    /// <param name="maxDimension">Longest edge in pixels; 0 decodes at native size.</param>
    public static async Task<SavedSignature?> TryLoadAsync(string path, int maxDimension = 0)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return await DecodeAsync(stream.AsRandomAccessStream(), path, maxDimension);
        }
        catch (Exception ex)
        {
            // Deliberately not filtered by type. BitmapDecoder surfaces a WIC
            // HRESULT as a bare COMException for a truncated, renamed or simply
            // unsupported file, and this is called from an async void handler —
            // a narrow filter here takes the whole app down when someone picks
            // a .pdf that has been renamed to .jpg.
            ErrorLog.Default.Write(nameof(SignatureStore), ex);
            return null;
        }
    }

    /// <summary>
    /// Decodes from an already-open stream.
    ///
    /// The picker path uses this: <c>PickSingleFileAsync</c> can hand back a
    /// StorageFile whose <c>Path</c> is empty (a OneDrive placeholder, a Phone
    /// Link item), where opening by path throws, but <c>OpenReadAsync</c> works.
    /// </summary>
    public static async Task<SavedSignature?> TryLoadAsync(
        IRandomAccessStream stream, string path, int maxDimension = 0)
    {
        try
        {
            return await DecodeAsync(stream, path, maxDimension);
        }
        catch (Exception ex)
        {
            ErrorLog.Default.Write(nameof(SignatureStore), ex);
            return null;
        }
    }

    private static async Task<SavedSignature?> DecodeAsync(
        IRandomAccessStream stream, string path, int maxDimension)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);

        // The decoder applies the EXIF rotation below, so for a portrait phone
        // photo (orientation 5-8) it hands back a TRANSPOSED buffer while
        // PixelWidth/PixelHeight still describe the stored image. Reading those
        // would interpret the pixels with the axes swapped and stamp a diagonal
        // smear. Saved PNGs carry no EXIF, which is why this never showed up
        // before photos became a supported import.
        uint storedW = decoder.PixelWidth;
        uint storedH = decoder.PixelHeight;
        bool transposed =
            decoder.OrientedPixelWidth == storedH &&
            decoder.OrientedPixelHeight == storedW &&
            storedW != storedH;

        // BitmapTransform scales BEFORE the decoder applies the EXIF rotation,
        // so ScaledWidth/ScaledHeight are in STORED space while the returned
        // buffer arrives in oriented space. Deriving the output dimensions by
        // transposing the values actually asked for — rather than scaling the
        // oriented pair separately — keeps them exactly consistent with the
        // buffer, which independent rounding would not.
        var transform = new BitmapTransform();
        uint outW = storedW, outH = storedH;
        long longest = Math.Max(storedW, storedH);
        if (maxDimension > 0 && longest > maxDimension)
        {
            double scale = maxDimension / (double)longest;
            outW = (uint)Math.Max(1, Math.Round(storedW * scale));
            outH = (uint)Math.Max(1, Math.Round(storedH * scale));
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
            transform.ScaledWidth = outW;
            transform.ScaledHeight = outH;
        }

        int width = (int)(transposed ? outH : outW);
        int height = (int)(transposed ? outW : outH);

        // Straight, NOT Ignore. Ignore flattens exactly the transparency a
        // signature is made of, and would stamp an opaque white box over
        // the page. (ThumbnailCache.EncodePng uses Ignore because a page
        // render is opaque by construction — don't copy it here.)
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        var bgra = pixels.DetachPixelData();
        if (bgra.Length < (long)width * height * 4)
        {
            // Belt and braces: the two candidate orderings above produce buffers
            // of IDENTICAL length, so this cannot catch a transposition — but it
            // does catch a decoder that scaled to something else entirely, which
            // would otherwise read past the buffer downstream.
            ErrorLog.Default.Write(
                nameof(SignatureStore),
                $"decoded {bgra.Length} bytes for a {width}x{height} image from {path}");
            return null;
        }
        return new SavedSignature(path, bgra, width, height);
    }

    /// <summary>Saves straight BGRA as a PNG and returns its path.</summary>
    public async Task<string> SaveAsync(byte[] bgra, int width, int height)
    {
        string path = System.IO.Path.Combine(_dir, $"sig-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.png");

        using var memory = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memory);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            (uint)width, (uint)height, 96, 96, bgra);
        await encoder.FlushAsync();

        memory.Seek(0);
        var reader = new DataReader(memory.GetInputStreamAt(0));
        uint size = (uint)memory.Size;
        await reader.LoadAsync(size);
        var bytes = new byte[size];
        reader.ReadBytes(bytes);

        // Write-then-rename so a crash mid-write can't leave a truncated PNG,
        // matching AppStateStore.Save.
        string tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    public void Delete(string path)
    {
        try
        {
            // Only ever delete inside our own directory, whatever we're handed.
            if (string.Equals(System.IO.Path.GetDirectoryName(path), _dir, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort; a locked file just stays in the list.
        }
    }
}
