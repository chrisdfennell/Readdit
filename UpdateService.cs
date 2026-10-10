using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Velopack;
using Velopack.Sources;

namespace Readdit;

/// <summary>
/// Keeps Readdit up to date from GitHub releases (published by the Release workflow):
/// <list type="bullet">
/// <item>Installed copies (Setup.exe) update through Velopack.</item>
/// <item>The portable exe downloads the new Readdit-Portable.exe beside itself and swaps it in on restart.</item>
/// </list>
/// Development builds (dotnet run) never update.
/// </summary>
public sealed partial class UpdateService(AppSettings settings)
{
    const string PortableAssetName = "Readdit-Portable.exe";

    static readonly HttpClient Http = CreateHttp();

    UpdateManager? _manager;
    UpdateInfo? _velopackUpdate;
    string? _portableDownload;   // new exe waiting next to the current one
    string? _readyVersion;

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Readdit-Updater");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        return http;
    }

    /// <summary>
    /// A single-file exe unpacks itself to a temp folder, so the exe's own folder differs from
    /// the folder the app runs from.
    /// </summary>
    public static bool IsPortable
    {
        get
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            return exeDir != null &&
                   !string.Equals(Path.GetFullPath(exeDir).TrimEnd('\\'),
                       Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
    }

    bool? _isInstalled;

    // Asking Velopack means locating the install on disk; do it once (WarmUp does it off the UI thread).
    // Without an update source there's no manager to ask, so fall back to Velopack's layout:
    // <install>\Update.exe next to <install>\current\Readdit.exe.
    bool IsInstalled => _isInstalled ??= Manager() is { } manager
        ? manager.IsInstalled
        : File.Exists(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "..", "Update.exe"));

    /// <summary>Works out what kind of copy this is in the background, so the UI never waits on it.</summary>
    public Task WarmUpAsync() => Task.Run(() => _ = IsInstalled);

    public string Kind => IsInstalled ? "installed" : IsPortable ? "portable" : "development build";

    public string CurrentVersion =>
        (IsInstalled ? Manager()?.CurrentVersion?.ToString() : null) ??
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "dev";

    public bool IsUpdateReady => _readyVersion != null;
    public bool IsInstalledCopy => _velopackUpdate != null;
    public string? ReadyVersion => _readyVersion;

    UpdateManager? Manager()
    {
        if (string.IsNullOrWhiteSpace(settings.UpdateUrl)) return null;
        try
        {
            return _manager ??= new UpdateManager(new GithubSource(settings.UpdateUrl.Trim(), null, false));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Forget cached state after the update source changes.</summary>
    public void Reset()
    {
        // A check still running for the old source must not deliver its result afterwards.
        Interlocked.Increment(ref _generation);
        _check = null;
        _manager = null;
        _isInstalled = null;
        _velopackUpdate = null;
        _portableDownload = null;
        _readyVersion = null;
    }

    int _generation;

    bool IsCurrent(int generation) => generation == Volatile.Read(ref _generation);

    /// <summary>Checks for an update and downloads it. Returns a message for the user.</summary>
    Task<string>? _check;

    public Task<string> CheckAsync()
    {
        if (string.IsNullOrWhiteSpace(settings.UpdateUrl))
            return Task.FromResult("Set an update source (a GitHub repository URL) to enable updates.");
        if (_readyVersion != null) return Task.FromResult(ReadyMessage);

        // One check at a time: the timer and "Check now" share the same one.
        if (_check is { IsCompleted: false }) return _check;

        // Everything runs on a background thread. Velopack does part of its work (finding the
        // install, checking and verifying downloads of ~80 MB) synchronously, which froze the
        // window for seconds when it ran on the UI thread.
        var generation = Volatile.Read(ref _generation);
        return _check = Task.Run(async () =>
        {
            try
            {
                if (IsInstalled) return await CheckInstalledAsync(generation).ConfigureAwait(false);
                if (IsPortable) return await CheckPortableAsync(generation).ConfigureAwait(false);
                return "This is a development build, so it doesn't update itself. " +
                       "Installed and portable copies from GitHub releases do.";
            }
            catch (Exception ex)
            {
                return $"Couldn't check for updates: {ex.Message}";
            }
        });
    }

    string ReadyMessage => $"Version {_readyVersion} is ready and installs when you restart Readdit.";

    const string SourceChanged = "The update source changed; check again.";

    async Task<string> CheckInstalledAsync(int generation)
    {
        var manager = Manager()!;
        var update = await manager.CheckForUpdatesAsync();
        if (update == null) return $"You're up to date (version {CurrentVersion}).";
        await manager.DownloadUpdatesAsync(update);
        if (!IsCurrent(generation)) return SourceChanged;
        _velopackUpdate = update;
        _readyVersion = update.TargetFullRelease.Version.ToString();
        return ReadyMessage;
    }

    /// <summary>"1.2.3-beta.1+abc" → (1.2.3, "beta.1").</summary>
    static (Version Number, string Prerelease) ParseVersion(string text)
    {
        var core = text.Trim().TrimStart('v').Split('+')[0];
        var parts = core.Split('-', 2);
        var number = Version.TryParse(parts[0], out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);
        return (number, parts.Length > 1 ? parts[1] : "");
    }

    /// <summary>Newer number, or the same number as a final release when we're on a prerelease of it.</summary>
    static bool IsNewer(string latest, string current)
    {
        var (l, lPre) = ParseVersion(latest);
        var (c, cPre) = ParseVersion(current);
        return l > c || (l == c && cPre.Length > 0 && lPre.Length == 0);
    }

    static string RunningVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    async Task<string> CheckPortableAsync(int generation)
    {
        var repo = RepoPath().Match(settings.UpdateUrl.Trim());
        if (!repo.Success) return "The update source must be a GitHub repository URL.";

        using var res = await Http.GetAsync($"https://api.github.com/repos/{repo.Groups[1].Value}/releases/latest");
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return "No releases have been published yet.";
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var tag = doc.RootElement.Str("tag_name").TrimStart('v');
        if (!Version.TryParse(tag.Split('-')[0], out _)) return $"Couldn't read the latest release version ({tag}).";
        if (!IsNewer(tag, RunningVersion)) return $"You're up to date (version {CurrentVersion}).";

        var asset = doc.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => a.Str("name") == PortableAssetName);
        var url = asset.ValueKind == JsonValueKind.Object ? asset.Str("browser_download_url") : "";
        if (url.Length == 0) return $"Version {tag} is out, but it has no portable exe to update to.";

        // Download beside the current exe so the swap on restart is a cheap rename.
        var target = Environment.ProcessPath! + ".new";
        try
        {
            using var download = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            download.EnsureSuccessStatusCode();
            await using (var file = File.Create(target))
                await download.Content.CopyToAsync(file);
        }
        catch (UnauthorizedAccessException)
        {
            return $"Version {tag} is out, but Readdit can't write to its folder. Download it from the releases page.";
        }

        if (!IsCurrent(generation))
        {
            File.Delete(target);
            return SourceChanged;
        }
        _portableDownload = target;
        _readyVersion = tag;
        return ReadyMessage;
    }

    /// <summary>
    /// Installs the downloaded update. Installed copies restart themselves through Velopack;
    /// for the portable exe this swaps the files and returns true, and the caller restarts.
    /// </summary>
    public bool Apply()
    {
        if (_velopackUpdate != null)
        {
            Manager()?.ApplyUpdatesAndRestart(_velopackUpdate.TargetFullRelease);
            return false; // Velopack exits and relaunches the app itself
        }
        if (_portableDownload == null || !File.Exists(_portableDownload)) return false;

        // Windows lets a running exe be renamed, just not overwritten.
        var exe = Environment.ProcessPath!;
        File.Move(exe, exe + ".old", overwrite: true);
        try
        {
            File.Move(_portableDownload, exe);
        }
        catch
        {
            // Never leave the user without an exe (e.g. antivirus is holding the download).
            File.Move(exe + ".old", exe);
            throw;
        }
        _portableDownload = null;
        return true;
    }

    /// <summary>Installs a ready update quietly once Readdit exits (no restart).</summary>
    public void ApplyOnExit()
    {
        if (_velopackUpdate != null)
        {
            Manager()?.WaitExitThenApplyUpdates(_velopackUpdate.TargetFullRelease, silent: true, restart: false);
        }
        else if (_portableDownload != null)
        {
            try
            {
                Apply();
            }
            catch
            {
                // Leave the download in place; the next check will retry.
            }
        }
    }

    /// <summary>Removes the previous portable exe left behind by an update.</summary>
    public static void CleanUpAfterPortableUpdate()
    {
        try
        {
            if (Environment.ProcessPath is not { } exe) return;
            if (File.Exists(exe + ".old")) File.Delete(exe + ".old");
            // A download from a previous session that was never applied (~80 MB); a fresh check
            // fetches it again if it's still the latest.
            if (File.Exists(exe + ".new")) File.Delete(exe + ".new");
        }
        catch
        {
            // Still in use for a moment after restart; next launch will get it.
        }
    }

    [GeneratedRegex(@"github\.com/([^/\s]+/[^/\s#?]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RepoPath();
}
