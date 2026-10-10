<#
.SYNOPSIS
    Builds Readdit's Windows installer (Releases\Readdit-win-Setup.exe) with Velopack.

.EXAMPLE
    ./build-installer.ps1 -Version 0.4.0
#>
param(
    [Parameter(Mandatory)] [string] $Version
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet tool restore
dotnet publish Readdit.csproj -c Release -r win-x64 --self-contained -p:Version=$Version -o publish
dotnet vpk pack --packId Readdit --packVersion $Version --packDir publish --mainExe Readdit.exe `
    --packTitle Readdit --icon Assets\readdit.ico --outputDir Releases

Write-Host "Installer: $(Resolve-Path Releases\Readdit-win-Setup.exe)"
