using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace Readdit;

/// <summary>
/// Checks GitHub releases for a newer Readdit (published with build-installer.ps1) and installs it.
/// Only active when Readdit was installed with its Setup.exe and an update URL is configured.
/// </summary>
public sealed class UpdateService(AppSettings settings)
{
    UpdateManager? _manager;
    UpdateInfo? _ready;

    public string CurrentVersion =>
        Manager()?.CurrentVersion?.ToString() ??
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";

    public bool IsUpdateReady => _ready != null;

    UpdateManager? Manager()
    {
        if (string.IsNullOrWhiteSpace(settings.UpdateUrl)) return null;
        return _manager ??= new UpdateManager(new GithubSource(settings.UpdateUrl.Trim(), null, false));
    }

    /// <summary>Forget the cached manager after the update URL changes.</summary>
    public void Reset()
    {
        _manager = null;
        _ready = null;
    }

    /// <summary>Checks for and downloads an update. Returns a message for the user.</summary>
    public async Task<string> CheckAsync()
    {
        var manager = Manager();
        if (manager == null) return "Set an update source (a GitHub repository URL) to enable updates.";
        if (!manager.IsInstalled)
            return "Updates work once Readdit is installed with its Setup.exe (this copy is a development build).";

        try
        {
            var update = await manager.CheckForUpdatesAsync();
            if (update == null) return $"You're up to date (version {CurrentVersion}).";
            await manager.DownloadUpdatesAsync(update);
            _ready = update;
            return $"Version {update.TargetFullRelease.Version} is ready. Restart Readdit to install it.";
        }
        catch (Exception ex)
        {
            return $"Couldn't check for updates: {ex.Message}";
        }
    }

    /// <summary>Installs a downloaded update and restarts Readdit.</summary>
    public void ApplyAndRestart()
    {
        if (_ready != null) Manager()?.ApplyUpdatesAndRestart(_ready.TargetFullRelease);
    }
}
