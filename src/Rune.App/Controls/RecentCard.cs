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

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, ThumbnailChanged);
            PropertyChanged?.Invoke(this, HasThumbnailChanged);
            PropertyChanged?.Invoke(this, ShowPlaceholderChanged);
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    /// <summary>Document-glyph placeholder shown until (or instead of) a thumbnail.</summary>
    public bool ShowPlaceholder => _thumbnail is null;

    private static readonly PropertyChangedEventArgs ThumbnailChanged = new(nameof(Thumbnail));
    private static readonly PropertyChangedEventArgs HasThumbnailChanged = new(nameof(HasThumbnail));
    private static readonly PropertyChangedEventArgs ShowPlaceholderChanged = new(nameof(ShowPlaceholder));
    public event PropertyChangedEventHandler? PropertyChanged;
}
