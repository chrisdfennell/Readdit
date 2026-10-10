<p align="center">
  <img src="Assets/readdit.ico" width="96" alt="Readdit icon">
</p>

<h1 align="center">Readdit</h1>

<p align="center">A fast, keyboard-friendly desktop Reddit client for Windows.</p>

<p align="center">
  <a href="https://github.com/chrisdfennell/Readdit/actions/workflows/release.yml"><img src="https://github.com/chrisdfennell/Readdit/actions/workflows/release.yml/badge.svg" alt="Build and release"></a>
  <a href="https://github.com/chrisdfennell/Readdit/releases/latest"><img src="https://img.shields.io/github/v/release/chrisdfennell/Readdit?include_prereleases&label=release" alt="Latest release"></a>
</p>

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

**Moderation** (for communities you moderate)
- Mod queues in the sidebar: one for all your communities plus one per community, covering the queue, reports, unmoderated, spam and edited items.
- Approve, remove, mark as spam, lock, pin, distinguish, mark NSFW or spoiler, ignore reports, and ban users with a reason, duration and message.
- Report reasons and approved/removed badges are shown inline. Quick approve, remove and spam are in the post list's right-click menu.

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

From the [latest release](https://github.com/chrisdfennell/Readdit/releases/latest), download one of these:

- **`Readdit-win-Setup.exe`** installs Readdit with a desktop icon and a Start menu entry. It updates itself when new releases come out.
- **`Readdit-Portable.exe`** is a single file you can run from anywhere, including a USB stick, with no install. It doesn't auto-update. On first run it unpacks itself to a temp folder, so the first start takes a moment.

The installer isn't code-signed yet, so Windows SmartScreen may warn you. Choose **More info → Run anyway**.

Readdit needs the Microsoft Edge WebView2 Runtime, which comes with Windows 11.

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet run                                # run a development build
./tests/run-smoke.ps1                     # post view smoke test (needs Edge or Chrome)
./build-installer.ps1 -Version 0.4.0      # build the installer and Readdit-Portable.exe into Releases/
```

## CI/CD

Releases are automatic.

- **Every push to `main`** runs [release.yml](.github/workflows/release.yml). It first runs CI ([ci.yml](.github/workflows/ci.yml)): a build with warnings treated as errors and the post view smoke test in headless Edge. If CI passes and the push changed the app, it builds the installer and portable exe and publishes the next patch release (0.4.0 → 0.4.1 → …).
  - Pushes that only change docs, tests, workflows or tooling are tested but not released.
  - To skip releasing a push, put `[skip release]` in any of its commit messages. It's still tested.
- **Pull requests** run CI and package a test installer and portable exe. Download them from the run's **Artifacts**.
- **For a minor or major version,** push your commit with `[skip release]`, then open **Actions → Release → Run workflow** on `main` and pick `minor` or `major`, or type an exact version. That way the commit isn't released twice.

**Updates:** installed copies update through Velopack. The portable exe downloads the new `Readdit-Portable.exe` beside itself and swaps it in. Both check every few hours, and install when Readdit closes or when you click **Restart to update**. You can turn this off in Preferences.

## Where Readdit keeps data

| What | Where |
| --- | --- |
| Settings | `%AppData%\Readdit\settings.json` |
| Sign-in (browser profile), read history, offline library | `%LocalAppData%\ReadditData\` |
| Crash log | `%LocalAppData%\ReadditData\crash.log` |
| The installed app itself | `%LocalAppData%\Readdit\` (managed by the installer) |
| Downloads | `Pictures\Readdit` (configurable) |
