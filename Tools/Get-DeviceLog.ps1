#Requires -Version 5.1
<#
.SYNOPSIS
    Captures a logcat snapshot from a connected Quest headset to a timestamped file
    so build/runtime issues can be reviewed and parsed.
.PARAMETER OutputFolder
    Base folder; logs are written to its 'logs' subfolder. Defaults to the
    outputFolder in QuestBuildSettings.json.
.PARAMETER Clear
    Clear the device logcat buffer before capturing.
.PARAMETER Follow
    Stream the log live to the console instead of taking a one-shot snapshot.
.EXAMPLE
    .\Get-DeviceLog.ps1 -Clear
.EXAMPLE
    .\Get-DeviceLog.ps1 -Follow
#>
[CmdletBinding()]
param(
    [string]$OutputFolder,
    [switch]$Clear,
    [switch]$Follow
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
    $OutputFolder = Join-Path $PSScriptRoot '..'
}

$logFolder = Join-Path $OutputFolder 'logs'
New-Item -ItemType Directory -Path $logFolder -Force | Out-Null

# Keep Unity + crash-relevant tags, silence everything else.
$tagArgs = @('Unity:V', 'CRASH:V', 'AndroidRuntime:E', 'DEBUG:V', 'libc:E', '*:S')

if ($Clear) {
    & $adb logcat -c
    Write-Host "logcat buffer cleared."
}

if ($Follow) {
    Write-Host "Streaming logcat (Ctrl+C to stop)..."
    & $adb logcat @tagArgs
}
else {
    $stamp = Get-Date -Format 'yyyy-MM-dd_HHmmss'
    $logFile = Join-Path $logFolder "logcat_$stamp.txt"
    Write-Host "Capturing logcat snapshot..."
    & $adb logcat -d @tagArgs | Out-File -FilePath $logFile -Encoding utf8
    Write-Host "Saved: $logFile" -ForegroundColor Green
}
