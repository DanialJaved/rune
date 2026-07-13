using Folio.Services;

namespace Folio.Tests;

public class AppStateTests
{
    [Fact]
    public void Remember_MovesFileToTopAndUpdatesPosition()
    {
        var state = new AppState();
        state.Remember(@"C:\a.pdf", "a.pdf", 1, 1.0, 0, 0.0);
        state.Remember(@"C:\b.pdf", "b.pdf", 2, 1.0, 0, 0.0);

        // Re-opening a.pdf should move it to the front and record its new page.
        state.Remember(@"C:\a.pdf", "a.pdf", 5, 1.5, 1, 0.42);

        Assert.Equal(2, state.Recents.Count);
        Assert.Equal(@"C:\a.pdf", state.Recents[0].Path);
        Assert.Equal(5, state.Recents[0].PageIndex);
        Assert.Equal(1.5, state.Recents[0].Zoom);
        Assert.Equal(1, state.Recents[0].Rotation);
        Assert.Equal(0.42, state.Recents[0].ScrollFraction);
    }

    [Fact]
    public void Remember_IsCaseInsensitiveOnPath()
    {
        var state = new AppState();
        state.Remember(@"C:\Docs\File.pdf", "File.pdf", 1, 1.0, 0, 0.0);
        state.Remember(@"c:\docs\file.pdf", "file.pdf", 3, 1.0, 0, 0.0);

        Assert.Single(state.Recents);
        Assert.Equal(3, state.Recents[0].PageIndex);
    }

    [Fact]
    public void Remember_CapsRecentsAtMax()
    {
        var state = new AppState();
        for (int i = 0; i < AppState.MaxRecents + 15; i++)
        {
            state.Remember($@"C:\file{i}.pdf", $"file{i}.pdf", 0, 1.0, 0, 0.0);
        }

        Assert.Equal(AppState.MaxRecents, state.Recents.Count);
        // Most recent insert is at the top.
        Assert.Equal($@"C:\file{AppState.MaxRecents + 14}.pdf", state.Recents[0].Path);
    }

    [Fact]
    public void FindRecent_ReturnsMatchOrNull()
    {
        var state = new AppState();
        state.Remember(@"C:\x.pdf", "x.pdf", 7, 2.0, 0, 0.1);

        Assert.Equal(7, state.FindRecent(@"C:\X.PDF")?.PageIndex);
        Assert.Null(state.FindRecent(@"C:\missing.pdf"));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsStateThroughDisk()
    {
        string dir = Path.Combine(Path.GetTempPath(), "folio-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AppStateStore(dir);
            var state = new AppState();
            state.Remember(@"C:\doc.pdf", "doc.pdf", 12, 1.25, 2, 0.33);
            state.Session = new SessionState { OpenPaths = [@"C:\doc.pdf"], ActiveIndex = 0 };
            store.Save(state);

            var reloaded = new AppStateStore(dir).Load();

            Assert.Single(reloaded.Recents);
            Assert.Equal(12, reloaded.Recents[0].PageIndex);
            Assert.Equal(1.25, reloaded.Recents[0].Zoom);
            Assert.Equal(2, reloaded.Recents[0].Rotation);
            Assert.Equal([@"C:\doc.pdf"], reloaded.Session.OpenPaths);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_CorruptFile_ReturnsFreshState()
    {
        string dir = Path.Combine(Path.GetTempPath(), "folio-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "state.json"), "{ this is not valid json ]");
            var state = new AppStateStore(dir).Load();
            Assert.Empty(state.Recents); // never throws; starts fresh
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
