<#
.SYNOPSIS
    Builds Readdit's Windows installer and portable exe into Releases\:
      Readdit-win-Setup.exe   installer (Velopack) with automatic updates
      Readdit-Portable.exe    single self-contained exe, runs from anywhere, no install

.EXAMPLE
    ./build-installer.ps1 -Version 0.4.0
#>
param(
    [Parameter(Mandatory)] [string] $Version
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet tool restore

# Installer: Velopack wants a normal (multi-file) self-contained publish.
dotnet publish Readdit.csproj -c Release -r win-x64 --self-contained -p:Version=$Version -o publish
dotnet vpk pack --packId Readdit --packVersion $Version --packDir publish --mainExe Readdit.exe `
    --packTitle Readdit --icon Assets\readdit.ico --shortcuts Desktop,StartMenuRoot --outputDir Releases

# Portable: everything (runtime and web assets) in one exe that unpacks itself on first run.
dotnet publish Readdit.csproj -c Release -r win-x64 --self-contained -p:Version=$Version `
    -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o portable
Copy-Item portable\Readdit.exe Releases\Readdit-Portable.exe -Force

Write-Host "Installer: $(Resolve-Path Releases\Readdit-win-Setup.exe)"
Write-Host "Portable:  $(Resolve-Path Releases\Readdit-Portable.exe)"
