#Requires -Version 5.1
<#
.SYNOPSIS
    Live session status over USB adb logcat - no network required.

    The app emits one "[SessionHeartbeat] {json}" Unity log line per second
    (RemoteConsoleServer.logcatHeartbeat). The lab network blocks adb-forward,
    but logcat over a USB cable does not care: plug the headset in and run
    this during development, device-test passes, or docked pre/post-session
    checks. (The experiment itself runs untethered - this is not a
    mid-session monitor.)
.PARAMETER Raw
    Print the raw JSON lines instead of the rendered status line.
.PARAMETER Log
    Also append every heartbeat JSON line to a timestamped file under the
    build outputFolder's 'logs' subfolder (same location Get-DeviceLog uses).
.EXAMPLE
    .\Watch-Session.ps1
.EXAMPLE
    .\Watch-Session.ps1 -Raw -Log
#>
[CmdletBinding()]
param(
    [switch]$Raw,
    [switch]$Log
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\QuestBuildCommon.ps1"

$adb = Resolve-Adb
Write-Host "adb: $adb"

$logFile = $null
if ($Log) {
    $folder = $null
    $settings = Get-BuildSettings
    if ($settings -and $settings.outputFolder) { $folder = $settings.outputFolder }
    if (-not $folder) { $folder = Join-Path $PSScriptRoot '..' }
    $logFolder = Join-Path $folder 'logs'
    New-Item -ItemType Directory -Path $logFolder -Force | Out-Null
    $stamp = Get-Date -Format 'yyyy-MM-dd_HHmmss'
    $logFile = Join-Path $logFolder "heartbeat_$stamp.jsonl"
    Write-Host "Appending heartbeats to: $logFile"
}

# Start from now, not from the (possibly hours-old) buffer.
& $adb logcat -c 2>$null

Write-Host "Waiting for [SessionHeartbeat] lines (Ctrl+C to stop)..."
& $adb logcat -v raw -s Unity:I | ForEach-Object {
    if ($_ -notmatch '\[SessionHeartbeat\]\s*(\{.*\})') { return }
    $json = $Matches[1]
    if ($logFile) { Add-Content -Path $logFile -Value $json -Encoding utf8 }
    if ($Raw) { Write-Host $json; return }

    try { $s = $json | ConvertFrom-Json } catch { return }

    $tag = if ($null -ne $s.tagAgeS) {
        if ($s.tagAgeS -lt 0) { 'never' } else { "$($s.tagAgeS)s" }
    } else { '?' }
    $batt = if ($null -ne $s.batteryPct -and $s.batteryPct -ge 0) { "$($s.batteryPct)%" } else { '?' }
    $lg = if ($null -ne $s.logRows) { $s.logRows } else { '?' }

    $line = "phase=$($s.phase) trial=$($s.trial) placed=$($s.placed) anchor=$($s.anchor) tag=$tag corr=$($s.lastCorrectionMm)mm occl=$($s.occlusion) rows=$lg batt=$batt t=$($s.time)s"

    # Overwrite one console line in place; fall back to plain lines when the
    # host has no usable console width (e.g. redirected output).
    $width = 0
    try { $width = [console]::WindowWidth } catch { }
    if ($width -gt 10) {
        if ($line.Length -gt $width - 1) { $line = $line.Substring(0, $width - 1) }
        Write-Host ("`r" + $line.PadRight($width - 1)) -NoNewline
    }
    else {
        Write-Host $line
    }
}
