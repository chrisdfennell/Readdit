<#
.SYNOPSIS
    Writes the winget manifests (version, installer, en-US locale) for a published Skein release,
    ready for `wingetcreate submit`. Run it after the release is on GitHub.
.EXAMPLE
    ./winget/New-WingetManifest.ps1 -Version 0.5.7 -License MIT
    wingetcreate submit --token <github-token> winget/manifests/c/ChrisFennell/Skein/0.5.7
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $License,
    [string] $Repo = 'chrisdfennell/Skein',
    [string] $Id = 'ChrisFennell.Skein'
)
$ErrorActionPreference = 'Stop'

$url = "https://github.com/$Repo/releases/download/v$Version/Skein-win-Setup.exe"
$temp = Join-Path ([IO.Path]::GetTempPath()) "Skein-$Version-Setup.exe"
Invoke-WebRequest $url -OutFile $temp
$sha = (Get-FileHash $temp -Algorithm SHA256).Hash
Remove-Item $temp
$date = (Get-Date).ToString('yyyy-MM-dd')

$folder = Join-Path $PSScriptRoot "manifests/c/$($Id.Replace('.', '/'))/$Version"
New-Item -ItemType Directory -Force $folder | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false

[IO.File]::WriteAllText((Join-Path $folder "$Id.yaml"), @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
PackageIdentifier: $Id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
"@, $utf8)

# Velopack's Setup.exe installs per user (no admin) and takes --silent.
[IO.File]::WriteAllText((Join-Path $folder "$Id.installer.yaml"), @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
PackageIdentifier: $Id
PackageVersion: $Version
InstallerType: exe
Scope: user
InstallModes:
- interactive
- silent
InstallerSwitches:
  Silent: --silent
  SilentWithProgress: --silent
UpgradeBehavior: install
ProductCode: Skein
ReleaseDate: $date
Installers:
- Architecture: x64
  InstallerUrl: $url
  InstallerSha256: $sha
ManifestType: installer
ManifestVersion: 1.6.0
"@, $utf8)

[IO.File]::WriteAllText((Join-Path $folder "$Id.locale.en-US.yaml"), @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json
PackageIdentifier: $Id
PackageVersion: $Version
PackageLocale: en-US
Publisher: Chris Fennell
PublisherUrl: https://github.com/chrisdfennell
PublisherSupportUrl: https://github.com/$Repo/issues
PackageName: Skein
PackageUrl: https://chrisdfennell.github.io/Skein/
License: $License
LicenseUrl: https://github.com/$Repo/blob/main/LICENSE
ShortDescription: A fast, keyboard-friendly Reddit client for Windows.
Description: Skein is a desktop Reddit client with tabs, folders, a picture viewer, an offline library, alerts and reminders, live threads, thread summaries and moderation tools.
Moniker: skein
Tags:
- reddit
- reddit-client
- social
- wpf
ReleaseNotesUrl: https://github.com/$Repo/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@, $utf8)

Write-Host "Manifests written to $folder"
Write-Host "Check them with: winget validate --manifest `"$folder`""
