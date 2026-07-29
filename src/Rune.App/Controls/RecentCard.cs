using System.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Rune.Controls;

/// <summary>
/// A recent-document card for the homepage grid. <see cref="Thumbnail"/> starts
/// null and is filled in once rendered on a background thread.
/// </summary>
public sealed class RecentCard : INotifyPropertyChanged
{
    private BitmapImage? _thumbnail;

    public string Path { get; }
    public string DisplayName { get; }
    public string Folder { get; }

    public RecentCard(string path, string displayName)
    {
        Path = path;
        DisplayName = displayName;
        try
        {
            Folder = System.IO.Path.GetDirectoryName(path) ?? "";
        }
        catch
        {
            Folder = "";
        }
    }

    /// <summary>Card cell size — mirrors the CardWidth/CardHeight tokens.</summary>
    private const double CellWidth = 168;
    private const double CellHeight = 212;

    private double _imageWidth = CellWidth;
    private double _imageHeight = CellHeight;

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            // Size the image box to the page's real shape. The cached PNG's own
            // pixel dimensions carry the aspect ratio, so nothing extra needs
            // rendering or storing. PixelWidth is 0 until decode completes,
            // hence the guard — the caller re-sets this on ImageOpened.
            if (value is { PixelWidth: > 0, PixelHeight: > 0 })
            {
                double scale = Math.Min(CellWidth / value.PixelWidth, CellHeight / value.PixelHeight);
                ImageWidth = Math.Round(value.PixelWidth * scale);
                ImageHeight = Math.Round(value.PixelHeight * scale);
            }
            PropertyChanged?.Invoke(this, ThumbnailChanged);
            PropertyChanged?.Invoke(this, HasThumbnailChanged);
            PropertyChanged?.Invoke(this, ShowPlaceholderChanged);
        }
    }

    /// <summary>Width of the bordered page box inside the card cell.</summary>
    public double ImageWidth
    {
        get => _imageWidth;
        private set
        {
            _imageWidth = value;
            PropertyChanged?.Invoke(this, ImageWidthChanged);
        }
    }

    /// <summary>Height of the bordered page box inside the card cell.</summary>
    public double ImageHeight
    {
        get => _imageHeight;
        private set
        {
            _imageHeight = value;
            PropertyChanged?.Invoke(this, ImageHeightChanged);
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    /// <summary>Document-glyph placeholder shown until (or instead of) a thumbnail.</summary>
    public bool ShowPlaceholder => _thumbnail is null;

    private static readonly PropertyChangedEventArgs ThumbnailChanged = new(nameof(Thumbnail));
    private static readonly PropertyChangedEventArgs HasThumbnailChanged = new(nameof(HasThumbnail));
    private static readonly PropertyChangedEventArgs ShowPlaceholderChanged = new(nameof(ShowPlaceholder));
    private static readonly PropertyChangedEventArgs ImageWidthChanged = new(nameof(ImageWidth));
    private static readonly PropertyChangedEventArgs ImageHeightChanged = new(nameof(ImageHeight));
    public event PropertyChangedEventHandler? PropertyChanged;
}
