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
    bool IsInstalled => _isInstalled ??= Manager()?.IsInstalled == true;

    /// <summary>Works out what kind of copy this is in the background, so the UI never waits on it.</summary>
    public Task WarmUpAsync() => Task.Run(() => _ = IsInstalled);

    public string Kind => IsInstalled ? "installed" : IsPortable ? "portable" : "development build";

    public string CurrentVersion =>
        (IsInstalled ? Manager()?.CurrentVersion?.ToString() : null) ??
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "dev";

    public bool IsUpdateReady => _readyVersion != null;
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
        _manager = null;
        _isInstalled = null;
        _velopackUpdate = null;
        _portableDownload = null;
        _readyVersion = null;
    }

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
        return _check = Task.Run(async () =>
        {
            try
            {
                if (IsInstalled) return await CheckInstalledAsync().ConfigureAwait(false);
                if (IsPortable) return await CheckPortableAsync().ConfigureAwait(false);
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

    async Task<string> CheckInstalledAsync()
    {
        var manager = Manager()!;
        var update = await manager.CheckForUpdatesAsync();
        if (update == null) return $"You're up to date (version {CurrentVersion}).";
        await manager.DownloadUpdatesAsync(update);
        _velopackUpdate = update;
        _readyVersion = update.TargetFullRelease.Version.ToString();
        return ReadyMessage;
    }

    async Task<string> CheckPortableAsync()
    {
        var repo = RepoPath().Match(settings.UpdateUrl.Trim());
        if (!repo.Success) return "The update source must be a GitHub repository URL.";

        using var res = await Http.GetAsync($"https://api.github.com/repos/{repo.Groups[1].Value}/releases/latest");
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return "No releases have been published yet.";
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var tag = doc.RootElement.Str("tag_name").TrimStart('v');
        if (!Version.TryParse(tag.Split('-')[0], out var latest)) return $"Couldn't read the latest release version ({tag}).";
        var current = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);
        if (latest <= new Version(current.Major, current.Minor, Math.Max(0, current.Build)))
            return $"You're up to date (version {CurrentVersion}).";

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
            if (Environment.ProcessPath is { } exe && File.Exists(exe + ".old")) File.Delete(exe + ".old");
        }
        catch
        {
            // Still in use for a moment after restart; next launch will get it.
        }
    }

    [GeneratedRegex(@"github\.com/([^/\s]+/[^/\s#?]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RepoPath();
}
