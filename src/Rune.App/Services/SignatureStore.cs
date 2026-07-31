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
    public async Task<IReadOnlyList<SavedSignature>> ListAsync()
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
            var loaded = await TryLoadAsync(file.FullName);
            if (loaded is not null)
            {
                result.Add(loaded);
            }
        }
        return result;
    }

    /// <summary>Decodes a PNG to straight BGRA. Returns null if it can't be read.</summary>
    public static async Task<SavedSignature?> TryLoadAsync(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream());

            // Straight, NOT Ignore. Ignore flattens exactly the transparency a
            // signature is made of, and would stamp an opaque white box over
            // the page. (ThumbnailCache.EncodePng uses Ignore because a page
            // render is opaque by construction — don't copy it here.)
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            return new SavedSignature(
                path, pixels.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
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
