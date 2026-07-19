namespace Rune.Engine;

/// <summary>
/// Pure index arithmetic for keeping user bookmarks (or anything page-indexed)
/// correct across page mutations. Mirrors the semantics of the page-editing
/// operations: delete, insert-at, and FPDF_MovePages (moved block lands at
/// destIndex in the FINAL ordering).
/// </summary>
public static class BookmarkRemap
{
    /// <summary>New index of a page after deleting <paramref name="deletedPages"/>, or null if it was deleted.</summary>
    public static int? AfterDelete(int page, IReadOnlyCollection<int> deletedPages)
    {
        if (deletedPages.Contains(page))
        {
            return null;
        }
        return page - deletedPages.Count(d => d < page);
    }

    /// <summary>New index of a page after inserting <paramref name="count"/> pages at <paramref name="insertAt"/>.</summary>
    public static int AfterInsert(int page, int insertAt, int count)
        => page >= insertAt ? page + count : page;

    /// <summary>New index of a page after a move (see <see cref="MovePermutation"/>).</summary>
    public static int AfterMove(int page, int pageCount, IReadOnlyList<int> movedPages, int destIndex)
        => MovePermutation(pageCount, movedPages, destIndex)[page];

    /// <summary>
    /// old-index → new-index map for moving <paramref name="movedPages"/>
    /// (order preserved) so the block starts at <paramref name="destIndex"/>
    /// in the final ordering — the same contract as FPDF_MovePages.
    /// </summary>
    public static int[] MovePermutation(int pageCount, IReadOnlyList<int> movedPages, int destIndex)
    {
        var moved = movedPages.Distinct().Where(i => i >= 0 && i < pageCount).OrderBy(i => i).ToList();
        var movedSet = new HashSet<int>(moved);
        var rest = new List<int>(pageCount - moved.Count);
        for (int i = 0; i < pageCount; i++)
        {
            if (!movedSet.Contains(i))
            {
                rest.Add(i);
            }
        }

        destIndex = Math.Clamp(destIndex, 0, rest.Count);
        var final = new List<int>(pageCount);
        final.AddRange(rest.Take(destIndex));
        final.AddRange(moved);
        final.AddRange(rest.Skip(destIndex));

        var map = new int[pageCount];
        for (int newIndex = 0; newIndex < final.Count; newIndex++)
        {
            map[final[newIndex]] = newIndex;
        }
        return map;
    }
}
