<#
.SYNOPSIS
    Builds Skein's Windows installer and portable exe into Releases\:
      Skein-win-Setup.exe   installer (Velopack) with automatic updates
      Skein-Portable.exe    single self-contained exe, runs from anywhere, no install

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
dotnet publish Skein.csproj -c Release -r win-x64 --self-contained -p:Version=$Version -o publish
dotnet vpk pack --packId Skein --packVersion $Version --packDir publish --mainExe Skein.exe `
    --packTitle Skein --icon Assets\skein.ico --shortcuts Desktop,StartMenuRoot --outputDir Releases

# Portable: everything (runtime and web assets) in one exe that unpacks itself on first run.
dotnet publish Skein.csproj -c Release -r win-x64 --self-contained -p:Version=$Version `
    -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o portable
Copy-Item portable\Skein.exe Releases\Skein-Portable.exe -Force

Write-Host "Installer: $(Resolve-Path Releases\Skein-win-Setup.exe)"
Write-Host "Portable:  $(Resolve-Path Releases\Skein-Portable.exe)"
