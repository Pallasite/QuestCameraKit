#Requires -Version 5.1
<#
.SYNOPSIS
    Installs the newest QuestCameraKit APK onto a connected Quest headset via adb.
.PARAMETER OutputFolder
    Folder to search for APKs. Defaults to the outputFolder in QuestBuildSettings.json.
.EXAMPLE
    .\Install-LatestAPK.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputFolder
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\QuestBuildCommon.ps1"

$adb = Resolve-Adb
Write-Host "adb: $adb"

if (-not $OutputFolder) {
    $settings = Get-BuildSettings
    if ($settings -and $settings.outputFolder) { $OutputFolder = $settings.outputFolder }
}
if (-not $OutputFolder) {
    throw "No output folder. Pass -OutputFolder, or run a build first to create QuestBuildSettings.json."
}
Write-Host "APK folder: $OutputFolder"

$apk = Get-ChildItem -Path (Join-Path $OutputFolder '*.apk') -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $apk) { throw "No .apk files found in $OutputFolder" }

Write-Host ""
Write-Host "Connected devices:"
& $adb devices
Write-Host ""
Write-Host "Installing $($apk.Name) ..."

& $adb install -r $apk.FullName
if ($LASTEXITCODE -ne 0) { throw "adb install failed (exit code $LASTEXITCODE)" }

Write-Host "Install complete: $($apk.Name)" -ForegroundColor Green
