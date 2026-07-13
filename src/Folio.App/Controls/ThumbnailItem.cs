using System.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Folio.Controls;

/// <summary>
/// One entry in the thumbnails strip. The <see cref="Image"/> starts null and
/// is filled in once the thumbnail has been rendered on a background thread, so
/// scrolling the strip never blocks on rendering.
/// </summary>
public sealed class ThumbnailItem : INotifyPropertyChanged
{
    private BitmapSource? _image;

    public int PageIndex { get; }
    public string Label => (PageIndex + 1).ToString();

    public ThumbnailItem(int pageIndex) => PageIndex = pageIndex;

    public BitmapSource? Image
    {
        get => _image;
        set
        {
            _image = value;
            PropertyChanged?.Invoke(this, ImageChangedArgs);
        }
    }

    public bool IsRendered => _image is not null;

    private static readonly PropertyChangedEventArgs ImageChangedArgs = new(nameof(Image));
    public event PropertyChangedEventHandler? PropertyChanged;
}
