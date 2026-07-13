using Rune.Engine;

namespace Rune.Controls;

/// <summary>Bindable table-of-contents node for the outline TreeView.</summary>
public sealed class OutlineNode
{
    public string Title { get; }
    public int PageIndex { get; }
    public IList<OutlineNode> Children { get; }

    public OutlineNode(OutlineItem item)
    {
        Title = string.IsNullOrWhiteSpace(item.Title) ? "(untitled)" : item.Title;
        PageIndex = item.PageIndex;
        Children = [.. item.Children.Select(c => new OutlineNode(c))];
    }
}
