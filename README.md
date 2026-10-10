<p align="center">
  <img src="Assets/readdit.ico" width="96" alt="Readdit icon">
</p>

<h1 align="center">Readdit</h1>

<p align="center">A fast, keyboard-friendly desktop Reddit client for Windows.</p>

---

Readdit is a native Windows app (WPF on .NET 10) for browsing Reddit. It signs in through Reddit's own login page and reads Reddit through your session, so you don't need an API key. Your password only ever goes to Reddit.

> Readdit is an independent project and isn't affiliated with or endorsed by Reddit.

## Features

**Browsing**
- Compact, card and picture-grid layouts, with infinite scroll.
- Tabs that are restored on restart, and a sidebar with your subscriptions and favorites, including community icons.
- Search with community suggestions as you type, plus user profiles and subreddit info banners.
- Reading mode for distraction-free reading, with adjustable text size.
- Hover previews for thumbnails, usernames and image links.
- An in-app browser for links, signed in to Reddit.

**Posts and comments**
- Formatted text, Reddit video with sound, GIFs, galleries and a fullscreen image viewer.
- Collapsible comment threads, "load more replies", and find-in-thread (Ctrl+F) that can load the whole thread.
- Comments added since your last visit are highlighted.
- Vote, save, reply, post, and edit or delete your own posts and comments, with a live formatting preview.

**Power features**
- Filters: keywords, domains, subreddits, minimum score, auto-collapsing low-scored comments, muted users and highlighted users.
- Read tracking (hide or dim posts you've opened) and hiding posts.
- Notifications for replies and messages, with a system tray icon.
- An offline library: saved posts are kept with their comments and media, and can be searched and tagged.
- One-click download of images, galleries and video (merged with sound if [ffmpeg](https://ffmpeg.org) is installed).
- Multiple accounts.
- Light, dark and true-black themes, with accent colors and an interface size setting.
- Automatic updates.

Press <kbd>?</kbd> in the app for all keyboard shortcuts (`j`/`k` to move, `a`/`z` to vote, `f` for reading mode, and more).

## Install

Download `Readdit-win-Setup.exe` from the [latest release](https://github.com/chrisdfennell/Readdit/releases/latest) and run it. Readdit updates itself when new releases come out.

The installer isn't code-signed yet, so Windows SmartScreen may warn you. Choose **More info → Run anyway**.

Readdit needs the Microsoft Edge WebView2 Runtime, which comes with Windows 11.

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet run                                # run a development build
./build-installer.ps1 -Version 0.4.0      # build Releases/Readdit-win-Setup.exe
```

## Releasing

Push a version tag. GitHub Actions builds the installer and publishes a release, and installed copies of Readdit pick it up:

```powershell
git tag v0.4.1
git push origin v0.4.1
```

## Where Readdit keeps data

| What | Where |
| --- | --- |
| Settings | `%AppData%\Readdit\settings.json` |
| Sign-in (browser profile), read history, offline library | `%LocalAppData%\Readdit\` |
| Crash log | `%LocalAppData%\Readdit\crash.log` |
| Downloads | `Pictures\Readdit` (configurable) |
