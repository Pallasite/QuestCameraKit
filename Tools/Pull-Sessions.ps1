#Requires -Version 5.1
<#
.SYNOPSIS
    Pull QuestBuild session logs from a connected Quest headset and file each
    one into the matching local <apkBase>.sessions/ folder beside its APK.

.DESCRIPTION
    Each launched session writes <sessionId>.log + <sessionId>.json into
    /sdcard/Android/data/<package>/files/Sessions/ on device. This script
    pulls every pair, reads the JSON sidecar (which carries the apkBaseName
    stamped in at build time), and copies them next to the matching APK in
    the output folder. Sessions whose build isn't local land in
    _unmatched_sessions/ so logs from another machine's builds aren't lost.

.PARAMETER OutputFolder
    Override the output folder. Defaults to QuestBuildSettings.json.

.PARAMETER Package
    Override the Android package id. Defaults to last-build.json.packageName.

.PARAMETER Cleanup
    Delete the session files from the device after a successful pull.
    Also honoured automatically if pullCleanupDevice is true in settings.

.PARAMETER DryRun
    Show what would be pulled and where it would go, without copying or
    deleting anything.

.EXAMPLE
    .\Pull-Sessions.ps1
.EXAMPLE
    .\Pull-Sessions.ps1 -Cleanup
.EXAMPLE
    .\Pull-Sessions.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [string]$OutputFolder,
    [string]$Package,
    [switch]$Cleanup,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\QuestBuildCommon.ps1"

# ---- Resolve config -----------------------------------------------------------
$adb = Resolve-Adb
Write-Host "adb: $adb"

$settings = Get-BuildSettings
if (-not $OutputFolder) {
    if ($settings -and $settings.outputFolder) { $OutputFolder = $settings.outputFolder }
}
if (-not $OutputFolder) {
    throw "No output folder. Pass -OutputFolder or build at least once so QuestBuildSettings.json exists."
}

$cloudFolder = if ($settings -and $settings.cloudMirrorFolder) { $settings.cloudMirrorFolder } else { $null }
$mirror = if ($settings -and ($settings.PSObject.Properties.Name -contains 'mirrorSessionLogs')) {
    [bool]$settings.mirrorSessionLogs
} else { $true }
$autoCleanup = if ($settings -and ($settings.PSObject.Properties.Name -contains 'pullCleanupDevice')) {
    [bool]$settings.pullCleanupDevice
} else { $false }
if ($autoCleanup) { $Cleanup = $true }

if (-not $Package) {
    $lb = Get-LastBuild
    if ($lb -and $lb.packageName) { $Package = $lb.packageName }
}
if (-not $Package) {
    throw "Package name unknown. Pass -Package, or rebuild once with the updated QuestBuilder so last-build.json carries packageName."
}
Write-Host "package: $Package"
Write-Host "output:  $OutputFolder"
if ($cloudFolder) { Write-Host "cloud:   $cloudFolder   (mirror=$mirror)" }

# ---- Check device -------------------------------------------------------------
$devicesOut = & $adb devices
$deviceLines = $devicesOut | Where-Object { $_ -match '\tdevice$' }
if (-not $deviceLines) {
    Write-Warning "No authorized device connected. Plug in via USB or connect wireless ADB."
    Write-Host ($devicesOut -join "`n")
    return
}
Write-Host "device:  $($deviceLines -join '; ')"

# ---- Stage pull ---------------------------------------------------------------
$devicePath = "/sdcard/Android/data/$Package/files/Sessions"
$staging = Join-Path $env:TEMP ("questbuild_pull_" + (Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Path $staging -Force | Out-Null

Write-Host ""
Write-Host "Pulling $devicePath ..."
$pullOut = & $adb pull $devicePath $staging
if ($pullOut) { $pullOut | ForEach-Object { Write-Host "  $_" } }

# adb may either put files directly in $staging or in $staging\Sessions
$pulledRoot = if (Test-Path (Join-Path $staging 'Sessions')) { Join-Path $staging 'Sessions' } else { $staging }
$logs = Get-ChildItem $pulledRoot -Filter '*.log' -ErrorAction SilentlyContinue
if (-not $logs) {
    Write-Warning "No session logs found on device under $devicePath."
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    return
}

# ---- File each log into the right local home ---------------------------------
$matchedCount = 0
$unmatchedCount = 0
$affectedBases = @{}   # apkBaseName -> $true (so we rebuild the right indexes)

foreach ($log in $logs) {
    $sidecarPath = Join-Path $pulledRoot ($log.BaseName + '.json')
    $apkBase = $null
    $meta = $null
    if (Test-Path $sidecarPath) {
        try {
            $meta = Get-Content $sidecarPath -Raw | ConvertFrom-Json
            $apkBase = $meta.apkBaseName
        } catch {
            Write-Warning "Could not parse sidecar for $($log.Name): $_"
        }
    } else {
        Write-Warning "$($log.Name) has no sidecar — treating as unmatched."
    }

    $hasLocalApk = $false
    if ($apkBase) {
        $hasLocalApk = Test-Path (Join-Path $OutputFolder ($apkBase + '.apk'))
    }

    if ($apkBase -and $hasLocalApk) {
        $dest = Join-Path $OutputFolder ($apkBase + '.sessions')
        $matchedCount++
        $affectedBases[$apkBase] = $true
    } else {
        $dest = Join-Path $OutputFolder '_unmatched_sessions'
        $unmatchedCount++
    }

    if ($DryRun) {
        Write-Host "  [dry-run] $($log.Name)  ->  $dest"
        continue
    }

    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Copy-Item $log.FullName -Destination $dest -Force
    if (Test-Path $sidecarPath) { Copy-Item $sidecarPath -Destination $dest -Force }
    Write-Host "  pulled $($log.Name)  ->  $dest"

    if ($mirror -and $cloudFolder) {
        $cloudDest = if ($apkBase -and $hasLocalApk) {
            Join-Path $cloudFolder ($apkBase + '.sessions')
        } else {
            Join-Path $cloudFolder '_unmatched_sessions'
        }
        try {
            New-Item -ItemType Directory -Path $cloudDest -Force | Out-Null
            Copy-Item $log.FullName -Destination $cloudDest -Force
            if (Test-Path $sidecarPath) { Copy-Item $sidecarPath -Destination $cloudDest -Force }
        } catch {
            Write-Warning "Cloud mirror copy failed for $($log.Name): $_"
        }
    }
}

# ---- Rebuild sessions-index.json for each affected APK -----------------------
if (-not $DryRun) {
    foreach ($apkBase in $affectedBases.Keys) {
        $dir = Join-Path $OutputFolder ($apkBase + '.sessions')
        $summaries = @()
        Get-ChildItem $dir -Filter '*.json' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne 'sessions-index.json' } |
            ForEach-Object {
                try {
                    $m = Get-Content $_.FullName -Raw | ConvertFrom-Json
                    $summaries += [PSCustomObject]@{
                        sessionId       = $m.sessionId
                        sessionStartUtc = $m.sessionStartUtc
                        sessionEndUtc   = $m.sessionEndUtc
                        cleanExit       = [bool]$m.cleanExit
                        durationSec     = [double]$m.durationSec
                        lineCount       = [int]$m.lineCount
                        warningCount    = [int]$m.warningCount
                        errorCount      = [int]$m.errorCount
                        exceptionCount  = [int]$m.exceptionCount
                    }
                } catch { }
            }
        $summaries = $summaries | Sort-Object sessionStartUtc -Descending
        $index = [PSCustomObject]@{
            sessions       = @($summaries)
            lastUpdatedUtc = (Get-Date).ToUniversalTime().ToString('o')
        }
        $indexPath = Join-Path $dir 'sessions-index.json'
        $index | ConvertTo-Json -Depth 6 | Set-Content -Path $indexPath -Encoding utf8
        Write-Host "indexed $apkBase  ($($summaries.Count) total sessions)"
    }

    # ---- last-pull.json ------------------------------------------------------
    $usDir = Find-UserSettingsDir
    if ($usDir) {
        $lastPull = [PSCustomObject]@{
            timestampUtc = (Get-Date).ToUniversalTime().ToString('o')
            pulled       = $matchedCount
            unmatched    = $unmatchedCount
            devicePath   = $devicePath
        }
        $lastPull | ConvertTo-Json | Set-Content -Path (Join-Path $usDir 'last-pull.json') -Encoding utf8
    } else {
        Write-Warning "Could not locate UserSettings/ directory; skipped last-pull.json."
    }
}

# ---- Optional device cleanup --------------------------------------------------
if ($Cleanup -and -not $DryRun) {
    Write-Host ""
    Write-Host "Cleaning device folder $devicePath ..."
    & $adb shell "rm -rf $devicePath/*"
}

Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Done — pulled $matchedCount matched, $unmatchedCount unmatched session(s)." -ForegroundColor Green
