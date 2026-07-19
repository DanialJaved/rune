using Rune.Engine;
using Rune.Services;

namespace Rune.Tests;

public class BookmarkPersistenceTests
{
    private static AppStateStore TempStore() =>
        new(Path.Combine(Path.GetTempPath(), "rune-tests", Guid.NewGuid().ToString("N")));

    [Fact]
    public void Bookmarks_RoundTripThroughDisk()
    {
        var store = TempStore();
        var state = new AppState();
        state.Remember(@"C:\docs\a.pdf", "a.pdf", 3, 1.0, 0, 0.1);
        state.FindRecent(@"C:\docs\a.pdf")!.Bookmarks =
        [
            new BookmarkEntry { PageIndex = 2, Name = "Intro" },
            new BookmarkEntry { PageIndex = 9, Name = "Chapter 2" },
        ];
        store.Save(state);

        var loaded = store.Load();
        var entry = loaded.FindRecent(@"C:\docs\a.pdf");
        Assert.NotNull(entry);
        Assert.Equal(2, entry!.Bookmarks.Count);
        Assert.Equal("Intro", entry.Bookmarks[0].Name);
        Assert.Equal(9, entry.Bookmarks[1].PageIndex);
    }

    [Fact]
    public void Remember_CarriesBookmarksForward()
    {
        var state = new AppState();
        state.Remember(@"C:\docs\a.pdf", "a.pdf", 0, 1.0, 0, 0);
        state.FindRecent(@"C:\docs\a.pdf")!.Bookmarks = [new BookmarkEntry { PageIndex = 5, Name = "Keep me" }];

        // Re-remember (e.g. app close) must not drop the bookmarks.
        state.Remember(@"C:\docs\a.pdf", "a.pdf", 7, 1.5, 0, 0.5);

        Assert.Single(state.FindRecent(@"C:\docs\a.pdf")!.Bookmarks);
    }

    [Fact]
    public void Remember_NeverEvictsBookmarkedEntries()
    {
        var state = new AppState();
        state.Remember(@"C:\docs\bookmarked.pdf", "bookmarked.pdf", 0, 1.0, 0, 0);
        state.FindRecent(@"C:\docs\bookmarked.pdf")!.Bookmarks = [new BookmarkEntry { PageIndex = 1, Name = "b" }];

        // Push far more files than MaxRecents through the list.
        for (int i = 0; i < AppState.MaxRecents + 10; i++)
        {
            state.Remember($@"C:\docs\file{i}.pdf", $"file{i}.pdf", 0, 1.0, 0, 0);
        }

        Assert.NotNull(state.FindRecent(@"C:\docs\bookmarked.pdf"));
        // Plain entries were evicted down to the cap (+1 protected entry).
        Assert.Equal(AppState.MaxRecents + 1, state.Recents.Count);
    }

    [Fact]
    public void OldStateFile_WithoutBookmarksField_Deserializes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rune-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.json"),
            """{"Recents":[{"Path":"C:\\docs\\old.pdf","DisplayName":"old.pdf","PageIndex":4,"Zoom":1.2}]}""");

        var loaded = new AppStateStore(dir).Load();
        var entry = loaded.FindRecent(@"C:\docs\old.pdf");
        Assert.NotNull(entry);
        Assert.Empty(entry!.Bookmarks);
        Assert.Equal(4, entry.PageIndex);
    }
}

public class BookmarkRemapTests
{
    [Fact]
    public void AfterDelete_ShiftsAndDrops()
    {
        int[] deleted = [1, 3];
        Assert.Equal(0, BookmarkRemap.AfterDelete(0, deleted));
        Assert.Null(BookmarkRemap.AfterDelete(1, deleted));
        Assert.Equal(1, BookmarkRemap.AfterDelete(2, deleted));
        Assert.Null(BookmarkRemap.AfterDelete(3, deleted));
        Assert.Equal(2, BookmarkRemap.AfterDelete(4, deleted));
    }

    [Fact]
    public void AfterInsert_ShiftsAtAndAfterInsertionPoint()
    {
        Assert.Equal(0, BookmarkRemap.AfterInsert(0, insertAt: 1, count: 2));
        Assert.Equal(3, BookmarkRemap.AfterInsert(1, insertAt: 1, count: 2));
        Assert.Equal(4, BookmarkRemap.AfterInsert(2, insertAt: 1, count: 2));
    }

    [Fact]
    public void MovePermutation_MovesBlockToDestInFinalOrdering()
    {
        // Pages 0..4; move pages [3,4] to the front.
        var map = BookmarkRemap.MovePermutation(5, [3, 4], 0);
        Assert.Equal(2, map[0]);
        Assert.Equal(3, map[1]);
        Assert.Equal(4, map[2]);
        Assert.Equal(0, map[3]);
        Assert.Equal(1, map[4]);
    }

    [Fact]
    public void MovePermutation_MoveForward()
    {
        // Pages 0..4; move page 0 so it lands at final index 3.
        var map = BookmarkRemap.MovePermutation(5, [0], 3);
        Assert.Equal(3, map[0]);
        Assert.Equal(0, map[1]);
        Assert.Equal(1, map[2]);
        Assert.Equal(2, map[3]);
        Assert.Equal(4, map[4]);
    }

    [Fact]
    public void MovePermutation_IsAPermutation()
    {
        var map = BookmarkRemap.MovePermutation(10, [2, 5, 6], 4);
        Assert.Equal(Enumerable.Range(0, 10), map.OrderBy(i => i));
    }
}
