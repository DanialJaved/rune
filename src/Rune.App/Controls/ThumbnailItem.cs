using System.ComponentModel;
using Microsoft.UI.Xaml;
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

    /// <summary>
    /// Whether this is the page currently being read.
    ///
    /// The ring is drawn by the item rather than left to the ListViewItem's
    /// selection tint, because the thumbnail Border's opaque background paints
    /// over that tint — which is why the current page was nearly invisible in
    /// the light theme while looking fine in dark.
    /// </summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value)
            {
                return;
            }
            _isCurrent = value;
            PropertyChanged?.Invoke(this, RingThicknessChangedArgs);
        }
    }

    /// <summary>
    /// Thickness of the accent ring overlay: 2 when this is the current page,
    /// 0 otherwise. Only the thickness is data-bound — the accent brush itself
    /// stays in XAML as a {ThemeResource}, because resolving a theme brush from
    /// code returns the dark-theme value whatever the active theme is.
    /// </summary>
    public Thickness RingThickness => new(_isCurrent ? 2 : 0);

    private bool _isCurrent;

    private static readonly PropertyChangedEventArgs ImageChangedArgs = new(nameof(Image));
    private static readonly PropertyChangedEventArgs BoxHeightChangedArgs = new(nameof(BoxHeight));
    private static readonly PropertyChangedEventArgs RingThicknessChangedArgs = new(nameof(RingThickness));
    public event PropertyChangedEventHandler? PropertyChanged;
}
