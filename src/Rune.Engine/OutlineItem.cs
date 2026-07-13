namespace Rune.Engine;

/// <summary>
/// One entry in a document's table of contents. <see cref="PageIndex"/> is the
/// zero-based target page, or -1 if the entry has no in-document destination
/// (e.g. it only opens a URL). Children are the nested sub-entries.
/// </summary>
public sealed class OutlineItem
{
    public required string Title { get; init; }
    public int PageIndex { get; init; } = -1;
    public IReadOnlyList<OutlineItem> Children { get; init; } = [];
}
