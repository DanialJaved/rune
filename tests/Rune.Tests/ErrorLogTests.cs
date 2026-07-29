using Rune.Services;

namespace Rune.Tests;

public class ErrorLogTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "rune-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Write_CreatesFileWithSourceAndMessage()
    {
        var log = new ErrorLog(TempDir());

        log.Write("TestSource", "something broke");

        string text = File.ReadAllText(log.Path_);
        Assert.Contains("TestSource", text);
        Assert.Contains("something broke", text);
    }

    [Fact]
    public void Write_Exception_IncludesTypeAndMessage()
    {
        var log = new ErrorLog(TempDir());

        log.Write("Boom", new InvalidOperationException("only a single ContentDialog"));

        string text = File.ReadAllText(log.Path_);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("only a single ContentDialog", text);
    }

    [Fact]
    public void Write_Appends_DoesNotOverwrite()
    {
        var log = new ErrorLog(TempDir());

        log.Write("A", "first");
        log.Write("B", "second");

        string text = File.ReadAllText(log.Path_);
        Assert.Contains("first", text);
        Assert.Contains("second", text);
    }

    [Fact]
    public void Write_TrimsOnceOverTheCap()
    {
        var log = new ErrorLog(TempDir());
        // 256 KB cap; ~1 KB per entry means ~300 entries pushes it over.
        string filler = new('x', 1024);
        for (int i = 0; i < 400; i++)
        {
            log.Write("Bulk", $"{i} {filler}");
        }

        long size = new FileInfo(log.Path_).Length;
        Assert.True(size < 400 * 1024, $"expected the log to be trimmed, was {size} bytes");
        // The newest entry must survive the trim.
        Assert.Contains("399", File.ReadAllText(log.Path_));
    }

    [Fact]
    public void Write_NeverThrows_EvenWhenTheFileIsLocked()
    {
        string dir = TempDir();
        var log = new ErrorLog(dir);
        log.Write("Init", "create the file");

        // Hold an exclusive lock, then log again — must swallow, not throw.
        using var hold = new FileStream(log.Path_, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var ex = Record.Exception(() => log.Write("Locked", "should be swallowed"));
        Assert.Null(ex);
    }

    [Fact]
    public void Write_NeverThrows_OnAnUnusablePath()
    {
        // A path that cannot be created — the ctor and Write must both survive.
        var log = new ErrorLog("Z:\\definitely\\not\\a\\real\\drive\\rune");

        var ex = Record.Exception(() => log.Write("NoDisk", "should be swallowed"));
        Assert.Null(ex);
    }
}
