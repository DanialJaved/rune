using System.ComponentModel;

namespace Rune.Controls;

/// <summary>A user bookmark row in the sidebar's Bookmarks pane.</summary>
public sealed class BookmarkItem : INotifyPropertyChanged
{
    private string _name;
    private int _pageIndex;

    public BookmarkItem(int pageIndex, string name)
    {
        _pageIndex = pageIndex;
        _name = name;
    }

    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            PropertyChanged?.Invoke(this, NameChanged);
        }
    }

    public int PageIndex
    {
        get => _pageIndex;
        set
        {
            _pageIndex = value;
            PropertyChanged?.Invoke(this, PageLabelChanged);
        }
    }

    public string PageLabel => $"Page {_pageIndex + 1}";

    private static readonly PropertyChangedEventArgs NameChanged = new(nameof(Name));
    private static readonly PropertyChangedEventArgs PageLabelChanged = new(nameof(PageLabel));
    public event PropertyChangedEventHandler? PropertyChanged;
}
