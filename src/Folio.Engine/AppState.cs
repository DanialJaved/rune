using System.Text.Json;
using System.Text.Json.Serialization;

namespace Folio.Services;

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
        });
        if (Recents.Count > MaxRecents)
        {
            Recents.RemoveRange(MaxRecents, Recents.Count - MaxRecents);
        }
    }
}

/// <summary>
/// Loads/saves <see cref="AppState"/> as JSON under %LOCALAPPDATA%\Folio.
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

    /// <param name="directory">Override the storage directory (tests); defaults to %LOCALAPPDATA%\Folio.</param>
    public AppStateStore(string? directory = null)
    {
        string dir = directory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Folio");
        Directory.CreateDirectory(dir);
        _path = System.IO.Path.Combine(dir, "state.json");
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
