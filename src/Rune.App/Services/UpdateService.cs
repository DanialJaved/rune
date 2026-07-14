using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Rune.Services;

public sealed record UpdateInfo(Version Version, string TagName, string Notes, string? ZipUrl, string HtmlUrl);

/// <summary>
/// Checks GitHub Releases for a newer version and, for the portable build,
/// stages and applies an update. The only network access is to the GitHub
/// API and the release asset, and only when the user enables auto-check or
/// asks explicitly — no telemetry.
/// </summary>
public sealed class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/DanialJaved/rune/releases/latest";
    private const string ReleasesPage = "https://github.com/DanialJaved/rune/releases";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub requires a User-Agent on every API request.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Rune-PDF-Reader");
        return client;
    }

    public static Version CurrentVersion =>
        // RUNE_FAKE_VERSION lets a test pretend to be an older build so the
        // live "update available" path can be exercised end-to-end.
        Environment.GetEnvironmentVariable("RUNE_FAKE_VERSION") is { } fake && Version.TryParse(fake, out var faked)
            ? faked
            : Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public string ReleasesPageUrl => ReleasesPage;

    /// <summary>
    /// Returns update info if the latest GitHub release is newer than the
    /// running build, otherwise null. Never throws — a failed check is silent.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseApi, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!TryParseVersion(tag, out var version) || version <= CurrentVersion)
            {
                return null;
            }

            string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            string html = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;

            // Find the win-x64 portable zip asset.
            string? zipUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                        name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
                    {
                        zipUrl = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }

            return new UpdateInfo(version, tag, notes, zipUrl, html);
        }
        catch
        {
            return null; // offline, rate-limited, malformed — treat as "no update"
        }
    }

    /// <summary>True when running as an unpackaged (portable) build we can self-update.</summary>
    public static bool IsPortable()
    {
        try
        {
            _ = Windows.ApplicationModel.Package.Current;
            return false; // packaged (MSIX) — can't swap our own files
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Downloads and extracts the update, then launches a detached script that
    /// waits for this process to exit, copies the new files over the app
    /// directory, and relaunches Rune. Returns false (without exiting) if the
    /// app directory isn't writable.
    /// </summary>
    public async Task<bool> DownloadAndApplyAsync(UpdateInfo update, CancellationToken ct = default)
    {
        if (update.ZipUrl is null || !IsPortable())
        {
            return false;
        }

        string appDir = AppContext.BaseDirectory.TrimEnd('\\');
        if (!IsDirectoryWritable(appDir))
        {
            return false;
        }

        string work = Path.Combine(Path.GetTempPath(), "Rune-update-" + Guid.NewGuid().ToString("N"));
        string staging = Path.Combine(work, "staging");
        Directory.CreateDirectory(staging);

        string zipPath = Path.Combine(work, "rune.zip");
        await using (var src = await Http.GetStreamAsync(update.ZipUrl, ct))
        await using (var dst = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        {
            await src.CopyToAsync(dst, ct);
        }
        ZipFile.ExtractToDirectory(zipPath, staging);

        int pid = Environment.ProcessId;
        string exe = Path.Combine(appDir, "Rune.exe");
        string script = Path.Combine(work, "apply-update.cmd");
        // Wait for our PID to exit, mirror staging over the app dir, relaunch.
        await File.WriteAllTextAsync(script, $"""
            @echo off
            :waitloop
            tasklist /FI "PID eq {pid}" 2>NUL | find "{pid}" >NUL
            if not errorlevel 1 (
                timeout /t 1 /nobreak >NUL
                goto waitloop
            )
            robocopy "{staging}" "{appDir}" /E /R:3 /W:1 >NUL
            start "" "{exe}"
            rmdir /S /Q "{work}"
            """, ct);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{script}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
        return true;
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        // "v0.2.0" or "0.2.0"; normalize to at least 3 components.
        string cleaned = tag.TrimStart('v', 'V').Trim();
        if (Version.TryParse(cleaned, out var parsed))
        {
            version = parsed.Build < 0 ? new Version(parsed.Major, parsed.Minor, 0) : parsed;
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    private static bool IsDirectoryWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".rune-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
