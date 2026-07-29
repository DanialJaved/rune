using System.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using Rune.Engine;

namespace Rune.Controls;

/// <summary>
/// One entry in the thumbnails strip. The <see cref="Image"/> starts null and
/// is filled in once the thumbnail has been rendered on a background thread, so
/// scrolling the strip never blocks on rendering.
///
/// The box carries its page's dimensions so it can size itself to that page's
/// aspect ratio *before* any bitmap exists — a fixed-height box letterboxes
/// landscape pages (presentation slides especially), and sizing only on arrival
/// makes the list reflow under the user as renders land.
/// </summary>
public sealed class ThumbnailItem : INotifyPropertyChanged
{
    /// <summary>Mirrors the SidebarThumb* tokens in Styles/Tokens.xaml.</summary>
    private const double BoxWidth = 168;
    private const double MinHeight = 96;
    private const double MaxHeight = 320;

    private BitmapSource? _image;
    private double _boxHeight;
    private int _rotation;

    public int PageIndex { get; }
    public string Label => (PageIndex + 1).ToString();
    public float PageWidthPt { get; }
    public float PageHeightPt { get; }

    public ThumbnailItem(int pageIndex, float pageWidthPt, float pageHeightPt, int rotationQuarterTurns = 0)
    {
        PageIndex = pageIndex;
        PageWidthPt = pageWidthPt;
        PageHeightPt = pageHeightPt;
        _rotation = rotationQuarterTurns;
        _boxHeight = ComputeHeight();
    }

    /// <summary>Height of this thumbnail's box, matching the page's shape.</summary>
    public double BoxHeight
    {
        get => _boxHeight;
        private set
        {
            if (Math.Abs(_boxHeight - value) < 0.5)
            {
                return;
            }
            _boxHeight = value;
            PropertyChanged?.Invoke(this, BoxHeightChangedArgs);
        }
    }

    /// <summary>Re-shapes the box for a new view rotation and drops the stale render.</summary>
    public void SetRotation(int rotationQuarterTurns)
    {
        _rotation = rotationQuarterTurns;
        BoxHeight = ComputeHeight();
        Image = null;
    }

    private double ComputeHeight() =>
        ThumbnailMetrics.BoxHeight(BoxWidth, PageWidthPt, PageHeightPt, _rotation, MinHeight, MaxHeight);

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
    private static readonly PropertyChangedEventArgs BoxHeightChangedArgs = new(nameof(BoxHeight));
    public event PropertyChangedEventHandler? PropertyChanged;
}
