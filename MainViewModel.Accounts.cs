using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Skein;

public sealed partial class MainViewModel
{
    // ---- Multiple accounts --------------------------------------------------------------------
    //
    // Each account lives in its own WebView2 profile. A WebView can't change profile once
    // created, so switching saves the choice and restarts Skein (takes a second or two).

    /// <summary>Raised when Skein must restart to switch account profiles.</summary>
    public event Action? RestartRequested;

    public IReadOnlyList<AccountInfo> Accounts => Settings.Accounts;
    public string ActiveProfile => Settings.ActiveProfile;

    void RememberAccount(string userName)
    {
        var entry = Settings.Accounts.FirstOrDefault(a => a.Profile == Settings.ActiveProfile);
        if (entry == null) Settings.Accounts.Add(entry = new AccountInfo { Profile = Settings.ActiveProfile });
        if (entry.UserName == userName) return;
        entry.UserName = userName;
        Settings.Save();
    }

    public void SwitchAccount(string profile)
    {
        if (profile == Settings.ActiveProfile) return;
        // Remember where we came from (for "Back to …" on the sign-in screen), and drop accounts
        // that were added but never signed in to.
        Settings.PreviousProfile = Settings.ActiveProfile;
        Settings.Accounts.RemoveAll(a => a.UserName.Length == 0 && a.Profile != profile && a.Profile != Settings.ActiveProfile);
        Settings.ActiveProfile = profile;
        Settings.OpenTabs = ["home"];
        Settings.SelectedTab = 0;
        Settings.Save();
        RestartRequested?.Invoke();
    }

    /// <summary>The signed-in account we switched from, offered on the sign-in screen.</summary>
    AccountInfo? PreviousAccount =>
        Settings.Accounts.FirstOrDefault(a => a.Profile == Settings.PreviousProfile && a.Profile != Settings.ActiveProfile &&
                                              a.UserName.Length > 0);

    public bool CanGoBackToAccount => PreviousAccount != null;
    public string BackToAccountLabel => PreviousAccount is { } a ? $"← Back to u/{a.UserName}" : "";

    public void AddAccount()
    {
        var profile = "acct-" + Guid.NewGuid().ToString("N")[..8];
        Settings.Accounts.Add(new AccountInfo { Profile = profile });
        SwitchAccount(profile);
    }

    public void RemoveAccount(string profile)
    {
        var entry = Settings.Accounts.FirstOrDefault(a => a.Profile == profile);
        if (entry == null) return;
        Settings.Accounts.Remove(entry);
        Settings.Save();
        if (profile == Settings.ActiveProfile)
            SwitchAccount(Settings.Accounts.FirstOrDefault()?.Profile ?? "");
    }

    // ---- Signing in again ------------------------------------------------------------------------

    async Task ShowAccountAsync()
    {
        _awaitingLogin = false;
        ShowLogin = true;
        LoginMessage = "This is your Reddit session. Sign out or switch accounts here, then click Continue.";
        await _services.Session.NavigateAsync(RedditSession.Home);
    }

    async Task ContinueAsync()
    {
        // Stop the navigation watcher from racing this explicit check.
        _awaitingLogin = false;
        await _services.Session.ParkAsync();
        if (await TryCompleteLoginAsync(force: false)) return;

        PromptLogin();
        LoginMessage = "Reddit says this session isn't signed in yet. Sign in below. " +
                       "If you're sure you already are, use \"Continue anyway\".";
        ShowContinueAnyway = true;
    }
}
