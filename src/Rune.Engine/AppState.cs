using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rune.Services;

/// <summary>A user bookmark inside one document.</summary>
public sealed class BookmarkEntry
{
    public int PageIndex { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>A file the user has opened, with the reading position to restore.</summary>
public sealed class RecentFile
{
    public required string Path { get; set; }
    public string DisplayName { get; set; } = "";
    public DateTime LastOpenedUtc { get; set; }
    public int PageIndex { get; set; }
    public double Zoom { get; set; }
    public int Rotation { get; set; }
    public double ScrollFraction { get; set; }

    /// <summary>User bookmarks (Ctrl+B); old state files deserialize to empty.</summary>
    public List<BookmarkEntry> Bookmarks { get; set; } = [];
}

/// <summary>What was open last time, so a restart can reopen it.</summary>
public sealed class SessionState
{
    public List<string> OpenPaths { get; set; } = [];
    public int ActiveIndex { get; set; }
}

/// <summary>User preferences, persisted with the rest of the app state.</summary>
public sealed class AppSettings
{
    /// <summary>"System", "Light", or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Invert page colors (night reading mode).</summary>
    public bool NightMode { get; set; }

    /// <summary>Reopen last session's tabs on launch.</summary>
    public bool RestoreSession { get; set; } = true;

    /// <summary>Enable j/k/g/G-style navigation keys.</summary>
    public bool VimKeys { get; set; } = true;

    /// <summary>Check GitHub for a newer release once per launch (≥24h apart).</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>Last time an update check ran, to rate-limit automatic checks.</summary>
    public DateTime LastUpdateCheckUtc { get; set; } = DateTime.MinValue;

    /// <summary>Show a thumbnail grid of recent documents on the start page.</summary>
    public bool ShowRecentThumbnails { get; set; } = true;

    /// <summary>Open the sidebar (thumbnails/chapters/bookmarks) when a document loads.</summary>
    public bool SidebarOpenByDefault { get; set; } = true;

    /// <summary>Ink pen color as #RRGGBB (default red).</summary>
    public string InkColor { get; set; } = "#E22222";

    /// <summary>Ink pen width in points.</summary>
    public double InkWidth { get; set; } = 2.5;
}

/// <summary>The whole persisted app state (one JSON file).</summary>
public sealed class AppState
{
    public List<RecentFile> Recents { get; set; } = [];
    public SessionState Session { get; set; } = new();
    public AppSettings Settings { get; set; } = new();

    public const int MaxRecents = 30;

    public RecentFile? FindRecent(string path) =>
        Recents.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Records/updates a file's position and moves it to the top of recents.</summary>
    public void Remember(string path, string displayName, int pageIndex, double zoom, int rotation, double scrollFraction)
    {
        var existing = FindRecent(path);
        Recents.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        Recents.Insert(0, new RecentFile
        {
            Path = path,
            DisplayName = displayName,
            LastOpenedUtc = DateTime.UtcNow,
            PageIndex = pageIndex,
            Zoom = zoom,
            Rotation = rotation,
            ScrollFraction = scrollFraction,
            Bookmarks = existing?.Bookmarks ?? [],
        });
        TrimRecents();
    }

    /// <summary>
    /// Evicts past <see cref="MaxRecents"/>, but never an entry that carries
    /// bookmarks — silently losing bookmarks because other files were opened
    /// would be a betrayal.
    /// </summary>
    private void TrimRecents()
    {
        for (int i = Recents.Count - 1; i >= MaxRecents; i--)
        {
            if (Recents[i].Bookmarks.Count == 0)
            {
                Recents.RemoveAt(i);
            }
        }
    }
}

/// <summary>
/// Loads/saves <see cref="AppState"/> as JSON under %LOCALAPPDATA%\Rune.
/// A corrupt or missing file yields a fresh empty state — never throws to the
/// caller, since losing the recents list must not stop the app from opening.
/// </summary>
public sealed class AppStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    /// <param name="directory">Override the storage directory (tests); defaults to %LOCALAPPDATA%\Rune.</param>
    public AppStateStore(string? directory = null)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string dir = directory ?? System.IO.Path.Combine(localAppData, "Rune");
        Directory.CreateDirectory(dir);
        _path = System.IO.Path.Combine(dir, "state.json");

        // One-time migration: the app shipped its pre-release builds as "Folio".
        if (directory is null && !File.Exists(_path))
        {
            string legacy = System.IO.Path.Combine(localAppData, "Folio", "state.json");
            try
            {
                if (File.Exists(legacy))
                {
                    File.Copy(legacy, _path);
                }
            }
            catch
            {
                // Best-effort; a fresh state is fine.
            }
        }
    }

    public AppState Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<AppState>(File.ReadAllText(_path), Options) ?? new AppState();
            }
        }
        catch
        {
            // Corrupt state file: start fresh rather than block startup.
        }
        return new AppState();
    }

    public void Save(AppState state)
    {
        try
        {
            // Write-then-rename so a crash mid-write can't leave a truncated file.
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Non-fatal: persistence is best-effort.
        }
    }
}
