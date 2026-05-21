#Requires -Version 5.1
<#
.SYNOPSIS
    Stream live logcat from a connected Quest headset into a session file
    next to the matching APK — the "tethered" alternative to the on-device
    SessionLogger + Pull-Sessions flow.

.DESCRIPTION
    Default behaviour: installs the newest APK in the output folder, launches
    the app on the headset, resolves its PID, and streams PID-filtered logcat
    to <outputFolder>/<apkBase>.sessions/live_<sessionId>.log with a JSON
    sidecar. Stop with Ctrl+C — the sidecar gets its sessionEndUtc on the
    way out.

    Flags:
      -AttachOnly  Skip install + launch; just attach to whatever's running.
      -NoLaunch    Install the latest APK but don't launch (you start it manually).
      -FromBuffer  One-shot capture of the current logcat buffer instead of a stream.

    Works over USB or wireless ADB.

.EXAMPLE
    .\Start-LiveSession.ps1
.EXAMPLE
    .\Start-LiveSession.ps1 -AttachOnly
.EXAMPLE
    .\Start-LiveSession.ps1 -FromBuffer
#>
[CmdletBinding()]
param(
    [string]$OutputFolder,
    [string]$Package,
    [switch]$AttachOnly,
    [switch]$NoLaunch,
    [switch]$FromBuffer
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\QuestBuildCommon.ps1"

# Helper used twice below; defined up here so it's available to both call sites.
function Update-SidecarOnExit {
    param($Sidecar, $Path)
    try {
        $Sidecar | Add-Member -NotePropertyName sessionEndUtc `
            -NotePropertyValue ((Get-Date).ToUniversalTime().ToString('o')) -Force
        $Sidecar | ConvertTo-Json | Set-Content -Path $Path -Encoding utf8
    } catch { }
}

# ---- Resolve config -----------------------------------------------------------
$adb = Resolve-Adb
Write-Host "adb: $adb"

$settings = Get-BuildSettings
if (-not $OutputFolder) {
    if ($settings -and $settings.outputFolder) { $OutputFolder = $settings.outputFolder }
}
if (-not $OutputFolder) {
    throw "No output folder. Pass -OutputFolder or build at least once."
}

$lb = Get-LastBuild
if (-not $Package -and $lb -and $lb.packageName) { $Package = $lb.packageName }
if (-not $Package) {
    throw "Package name unknown. Pass -Package, or rebuild once so last-build.json carries packageName."
}

$apkBase = $null
if ($lb -and $lb.apkFileName) {
    $apkBase = [System.IO.Path]::GetFileNameWithoutExtension($lb.apkFileName)
}

Write-Host "package: $Package"
Write-Host "output:  $OutputFolder"
if ($apkBase) { Write-Host "build:   $apkBase" }

# ---- Device check -------------------------------------------------------------
$devicesOut = & $adb devices
$deviceLines = $devicesOut | Where-Object { $_ -match '\tdevice$' }
if (-not $deviceLines) {
    Write-Warning "No authorized device connected."
    Write-Host ($devicesOut -join "`n")
    return
}
Write-Host "device:  $($deviceLines -join '; ')"

# ---- Optional install + launch -----------------------------------------------
if (-not $AttachOnly) {
    Write-Host ""
    Write-Host "Installing latest APK ..."
    & "$PSScriptRoot\Install-LatestAPK.ps1" -OutputFolder $OutputFolder
}

if (-not $AttachOnly -and -not $NoLaunch) {
    Write-Host "Launching $Package ..."
    & $adb shell monkey -p $Package -c android.intent.category.LAUNCHER 1 | Out-Null
    Start-Sleep -Seconds 2
}

# ---- Resolve PID -------------------------------------------------------------
$appPid = (& $adb shell pidof $Package | Out-String).Trim()
if (-not $appPid) {
    Write-Warning "Could not resolve PID for $Package — logging all output (noisier)."
}

# ---- Output paths -------------------------------------------------------------
$sessionStamp = Get-Date -Format 'yyyy-MM-dd_HHmmss'
$sessionId    = "live_$sessionStamp"
$dir = if ($apkBase) {
    Join-Path $OutputFolder ($apkBase + '.sessions')
} else {
    Join-Path $OutputFolder '_live_sessions'
}
New-Item -ItemType Directory -Path $dir -Force | Out-Null

$logFile     = Join-Path $dir ($sessionId + '.log')
$sidecarFile = Join-Path $dir ($sessionId + '.json')

# Initial sidecar — overwritten on exit with sessionEndUtc.
$sidecar = [PSCustomObject]@{
    sessionId       = $sessionId
    apkBaseName     = $apkBase
    packageName     = $Package
    captureMode     = if ($FromBuffer) { 'snapshot' } else { 'live-stream' }
    sessionStartUtc = (Get-Date).ToUniversalTime().ToString('o')
    sessionEndUtc   = ''
    appPid          = $appPid
    logFile         = $logFile
}
$sidecar | ConvertTo-Json | Set-Content -Path $sidecarFile -Encoding utf8

# ---- Capture ------------------------------------------------------------------
$adbArgs = @('logcat')
if ($appPid) { $adbArgs += @('--pid', $appPid) }

if ($FromBuffer) {
    Write-Host ""
    Write-Host "Snapshot mode — capturing current buffer ..."
    & $adb @adbArgs '-d' | Out-File -FilePath $logFile -Encoding utf8
    Update-SidecarOnExit -Sidecar $sidecar -Path $sidecarFile
    Write-Host "Saved: $logFile" -ForegroundColor Green
    return
}

Write-Host ""
Write-Host "Streaming logcat -> $logFile"
Write-Host "(Ctrl+C to stop)" -ForegroundColor Yellow
Write-Host ""

try {
    & $adb @adbArgs | Tee-Object -FilePath $logFile
}
finally {
    Update-SidecarOnExit -Sidecar $sidecar -Path $sidecarFile
    Write-Host ""
    Write-Host "Stopped. Log: $logFile" -ForegroundColor Green
}
