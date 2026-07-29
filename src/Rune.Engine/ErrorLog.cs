using System.Reflection;

namespace Rune.Services;

/// <summary>
/// Append-only crash/error log at <c>%LOCALAPPDATA%\Rune\errors.log</c>.
///
/// Deliberately paranoid: every method swallows its own failures. This is the
/// last line of defence — an exception thrown *by the error logger* while
/// handling an unhandled exception would take the process down for good.
///
/// Lives in Rune.Engine (like <see cref="AppStateStore"/>) purely so the test
/// project can reach it; it has no engine dependencies.
/// </summary>
public sealed class ErrorLog
{
    /// <summary>Trim the file once it passes this, keeping the newest half.</summary>
    private const long MaxBytes = 256 * 1024;

    private readonly string _path;
    private readonly object _gate = new();

    /// <param name="directory">Override the log directory (tests); defaults to %LOCALAPPDATA%\Rune.</param>
    public ErrorLog(string? directory = null)
    {
        string dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rune");
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // Logging is best-effort; Write() will fail harmlessly below.
        }
        _path = Path.Combine(dir, "errors.log");
    }

    public static ErrorLog Default { get; } = new();

    public string Path_ => _path;

    public void Write(string source, Exception exception) =>
        Write(source, exception.ToString());

    public void Write(string source, string message)
    {
        try
        {
            string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";
            string entry = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z] {source} (v{version}){Environment.NewLine}{message}{Environment.NewLine}{Environment.NewLine}";

            lock (_gate)
            {
                File.AppendAllText(_path, entry);
                TrimIfHugeLocked();
            }
        }
        catch
        {
            // Disk full, file locked, no permission — never let logging throw.
        }
    }

    /// <summary>Keeps the newest half of the file once it grows past the cap.</summary>
    private void TrimIfHugeLocked()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length <= MaxBytes)
            {
                return;
            }
            string[] lines = File.ReadAllLines(_path);
            File.WriteAllLines(_path, lines.Skip(lines.Length / 2));
        }
        catch
        {
            // A trim failure must not lose the entry we just wrote.
        }
    }
}
