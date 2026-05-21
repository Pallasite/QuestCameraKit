#Requires -Version 5.1
<#
.SYNOPSIS
    Pull QuestBuild session bundles from a connected Quest headset and file
    each one into the matching local <apkBase>.sessions/ folder beside its APK.

.DESCRIPTION
    Each app launch produces a bundle at
    /sdcard/Android/data/<package>/files/Sessions/<sessionId>/
    containing session.log + session.json + the experiment CSV + any sample
    CSVs the run produced. This script pulls every session folder, reads its
    session.json (apkBaseName + build identity), and copies the WHOLE folder
    into the matching local <outputFolder>/<apkBase>.sessions/<sessionId>/.

    Backward compatibility for one transition window:
      - Legacy Phase 2 flat <id>.log + <id>.json pairs at the Sessions/ root
        are detected and pulled into the same local <apkBase>.sessions/ as
        flat files (no per-session subfolder).
      - Root-level *.csv files predating this refactor are pulled into
        <outputFolder>/_orphan_root_csvs/ so they aren't lost.
      - trial_conditions.csv (experimenter input, not output) is never pulled.

    Works over USB or wireless ADB (whatever `adb devices` reports).

.PARAMETER OutputFolder
    Override the output folder. Defaults to QuestBuildSettings.json.

.PARAMETER Package
    Override the Android package id. Defaults to last-build.json.packageName.

.PARAMETER Cleanup
    Delete pulled session files from the device after a successful pull.
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

# ---- Stage pull of the Sessions/ tree -----------------------------------------
$deviceFilesRoot = "/sdcard/Android/data/$Package/files"
$deviceSessions  = "$deviceFilesRoot/Sessions"
$staging = Join-Path $env:TEMP ("questbuild_pull_" + (Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Path $staging -Force | Out-Null

Write-Host ""
Write-Host "Pulling $deviceSessions ..."
$pullOut = & $adb pull $deviceSessions $staging
if ($pullOut) { $pullOut | ForEach-Object { Write-Host "  $_" } }

# adb may put files directly into $staging or inside $staging\Sessions
$pulledRoot = if (Test-Path (Join-Path $staging 'Sessions')) { Join-Path $staging 'Sessions' } else { $staging }

# ---- Counters / index targets -------------------------------------------------
$newSessionsPulled    = 0
$legacyLogsPulled     = 0
$unmatchedCount       = 0
$affectedBases        = @{}

# ---- NEW-LAYOUT sessions: subfolders with session.json ------------------------
$sessionDirs = if (Test-Path $pulledRoot) {
    Get-ChildItem $pulledRoot -Directory -ErrorAction SilentlyContinue
} else { @() }

foreach ($sessionDir in $sessionDirs) {
    $sidecarPath = Join-Path $sessionDir.FullName 'session.json'
    if (-not (Test-Path $sidecarPath)) {
        Write-Warning "Skipping $($sessionDir.Name): no session.json inside (not a new-layout session)."
        continue
    }

    $meta = $null
    $apkBase = $null
    try {
        $meta = Get-Content $sidecarPath -Raw | ConvertFrom-Json
        $apkBase = $meta.apkBaseName
    } catch {
        Write-Warning "Could not parse $sidecarPath - $_"
    }

    $hasLocalApk = $false
    if ($apkBase) {
        $hasLocalApk = Test-Path (Join-Path $OutputFolder ($apkBase + '.apk'))
    }

    if ($apkBase -and $hasLocalApk) {
        $destBase = Join-Path $OutputFolder ($apkBase + '.sessions')
        $newSessionsPulled++
        $affectedBases[$apkBase] = $true
    } else {
        $destBase = Join-Path $OutputFolder '_unmatched_sessions'
        $unmatchedCount++
    }
    $dest = Join-Path $destBase $sessionDir.Name

    if ($DryRun) {
        Write-Host "  [dry-run] $($sessionDir.Name)/  ->  $dest"
        continue
    }

    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Copy-Item -Path (Join-Path $sessionDir.FullName '*') -Destination $dest -Recurse -Force
    Write-Host "  pulled $($sessionDir.Name)/  ->  $dest"

    if ($mirror -and $cloudFolder) {
        $cloudBase = if ($apkBase -and $hasLocalApk) {
            Join-Path $cloudFolder ($apkBase + '.sessions')
        } else {
            Join-Path $cloudFolder '_unmatched_sessions'
        }
        $cloudDest = Join-Path $cloudBase $sessionDir.Name
        try {
            New-Item -ItemType Directory -Path $cloudDest -Force | Out-Null
            Copy-Item -Path (Join-Path $sessionDir.FullName '*') -Destination $cloudDest -Recurse -Force
        } catch {
            Write-Warning "Cloud mirror copy failed for $($sessionDir.Name): $_"
        }
    }
}

# ---- LEGACY flat <id>.log + <id>.json pairs at $pulledRoot --------------------
$flatLogs = if (Test-Path $pulledRoot) {
    Get-ChildItem $pulledRoot -Filter '*.log' -File -ErrorAction SilentlyContinue
} else { @() }

foreach ($log in $flatLogs) {
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
        Write-Warning "$($log.Name) has no sidecar - treating as unmatched."
    }

    $hasLocalApk = $false
    if ($apkBase) {
        $hasLocalApk = Test-Path (Join-Path $OutputFolder ($apkBase + '.apk'))
    }

    if ($apkBase -and $hasLocalApk) {
        $dest = Join-Path $OutputFolder ($apkBase + '.sessions')
        $legacyLogsPulled++
        $affectedBases[$apkBase] = $true
    } else {
        $dest = Join-Path $OutputFolder '_unmatched_sessions'
        $unmatchedCount++
    }

    if ($DryRun) {
        Write-Host "  [dry-run, legacy] $($log.Name)  ->  $dest"
        continue
    }

    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Copy-Item $log.FullName -Destination $dest -Force
    if (Test-Path $sidecarPath) { Copy-Item $sidecarPath -Destination $dest -Force }
    Write-Host "  pulled (legacy) $($log.Name)  ->  $dest"

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

# ---- Root-level orphan *.csv files (pre-refactor leftovers) -------------------
$rootCsvsPulled = 0
$rootListing = & $adb shell "ls -1 $deviceFilesRoot/"
if ($rootListing) {
    $orphans = $rootListing | ForEach-Object { $_.ToString().Trim() } | Where-Object {
        $_ -match '\.csv$' -and $_ -notmatch '^trial_conditions'
    }
    if ($orphans) {
        $orphanDir = Join-Path $OutputFolder '_orphan_root_csvs'
        $orphanCloudDir = if ($mirror -and $cloudFolder) { Join-Path $cloudFolder '_orphan_root_csvs' } else { $null }

        foreach ($csv in $orphans) {
            if (-not $csv) { continue }
            $remote = "$deviceFilesRoot/$csv"
            if ($DryRun) {
                Write-Host "  [dry-run] root orphan $csv  ->  $orphanDir"
                continue
            }
            New-Item -ItemType Directory -Path $orphanDir -Force | Out-Null
            $localPath = Join-Path $orphanDir $csv
            $pullCsv = & $adb pull $remote $localPath
            if ($LASTEXITCODE -eq 0) {
                $rootCsvsPulled++
                Write-Host "  pulled root orphan $csv  ->  $orphanDir"
                if ($orphanCloudDir) {
                    try {
                        New-Item -ItemType Directory -Path $orphanCloudDir -Force | Out-Null
                        Copy-Item $localPath -Destination $orphanCloudDir -Force
                    } catch {
                        Write-Warning "Cloud mirror for orphan $csv failed: $_"
                    }
                }
            } else {
                Write-Warning "adb pull failed for $csv (exit $LASTEXITCODE)"
            }
        }
    }
}

# ---- Rebuild sessions-index.json for each affected APK ------------------------
if (-not $DryRun) {
    foreach ($apkBase in $affectedBases.Keys) {
        $dir = Join-Path $OutputFolder ($apkBase + '.sessions')
        $summaries = @()

        # New-layout: every <sessionId>/session.json
        Get-ChildItem $dir -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $sj = Join-Path $_.FullName 'session.json'
            if (Test-Path $sj) {
                try {
                    $m = Get-Content $sj -Raw | ConvertFrom-Json
                    $files = @(Get-ChildItem $_.FullName -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
                    $measure = Get-ChildItem $_.FullName -File -Recurse -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum
                    $totalBytes = if ($measure.Sum) { [long]$measure.Sum } else { [long]0 }
                    $summaries += [PSCustomObject]@{
                        sessionId       = $m.sessionId
                        layout          = 'folder'
                        folderName      = $_.Name
                        sessionStartUtc = $m.sessionStartUtc
                        sessionEndUtc   = $m.sessionEndUtc
                        cleanExit       = [bool]$m.cleanExit
                        durationSec     = [double]$m.durationSec
                        lineCount       = [int]$m.lineCount
                        warningCount    = [int]$m.warningCount
                        errorCount      = [int]$m.errorCount
                        exceptionCount  = [int]$m.exceptionCount
                        files           = $files
                        totalBytes      = $totalBytes
                    }
                } catch { }
            }
        }

        # Legacy flat pairs at the root of $dir (.json next to .log)
        Get-ChildItem $dir -Filter '*.json' -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne 'sessions-index.json' } |
            ForEach-Object {
                try {
                    $m = Get-Content $_.FullName -Raw | ConvertFrom-Json
                    $logPath = Join-Path $_.DirectoryName ($_.BaseName + '.log')
                    $totalBytes = [long]$_.Length
                    if (Test-Path $logPath) { $totalBytes += [long](Get-Item $logPath).Length }
                    $summaries += [PSCustomObject]@{
                        sessionId       = $m.sessionId
                        layout          = 'legacy-flat'
                        folderName      = ''
                        sessionStartUtc = $m.sessionStartUtc
                        sessionEndUtc   = $m.sessionEndUtc
                        cleanExit       = [bool]$m.cleanExit
                        durationSec     = [double]$m.durationSec
                        lineCount       = [int]$m.lineCount
                        warningCount    = [int]$m.warningCount
                        errorCount      = [int]$m.errorCount
                        exceptionCount  = [int]$m.exceptionCount
                        files           = @($_.Name, ($_.BaseName + '.log'))
                        totalBytes      = $totalBytes
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
            timestampUtc   = (Get-Date).ToUniversalTime().ToString('o')
            pulled         = $newSessionsPulled + $legacyLogsPulled
            newSessions    = $newSessionsPulled
            legacySessions = $legacyLogsPulled
            unmatched      = $unmatchedCount
            orphanRootCsvs = $rootCsvsPulled
            devicePath     = $deviceSessions
        }
        $lastPull | ConvertTo-Json | Set-Content -Path (Join-Path $usDir 'last-pull.json') -Encoding utf8
    } else {
        Write-Warning "Could not locate UserSettings/ directory; skipped last-pull.json."
    }
}

# ---- Optional device cleanup --------------------------------------------------
if ($Cleanup -and -not $DryRun) {
    Write-Host ""
    Write-Host "Cleaning device folder $deviceSessions ..."
    & $adb shell "rm -rf $deviceSessions/*"
}

Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  new-layout sessions:  $newSessionsPulled"
Write-Host "  legacy sessions:      $legacyLogsPulled"
Write-Host "  unmatched:            $unmatchedCount"
Write-Host "  root orphan CSVs:     $rootCsvsPulled"
