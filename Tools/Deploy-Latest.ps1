#Requires -Version 5.1
<#
.SYNOPSIS
    One-click "pre-pull + install + optional launch" for a connected Quest
    headset. Composes the existing Pull-Sessions + Install-LatestAPK tools.

.DESCRIPTION
    Steps:
      1. Resolve adb. If no authorised device is connected, warn and exit 0
         (non-failure - lets a "Build + Deploy" flow proceed cleanly when no
         headset is plugged in).
      2. Pre-pull any existing sessions off the device into the local
         outputFolder (and cloud mirror if configured) by invoking
         Pull-Sessions.ps1. Cleanup is intentionally NOT requested - sessions
         survive `adb install -r` because Android preserves persistentDataPath,
         and the in-app SessionLogger trims by maxSessionsRetainedOnDevice on
         the next launch anyway. The pre-pull keeps the local mirror current.
      3. Install the newest *.apk in outputFolder by invoking
         Install-LatestAPK.ps1.
      4. If -Launch (or settings.launchAfterDeploy is true) launch the app on
         the headset via `adb shell monkey`.

    Works over USB or wireless ADB.

.PARAMETER OutputFolder
    Override the local APK folder. Defaults to QuestBuildSettings.json.outputFolder.

.PARAMETER Package
    Override the Android package id. Defaults to last-build.json.packageName.

.PARAMETER Launch
    Force-enable the post-install launch step regardless of the
    launchAfterDeploy setting.

.PARAMETER NoLaunch
    Force-disable the post-install launch step regardless of the setting.

.PARAMETER SkipPull
    Skip the pre-install Pull-Sessions step.

.EXAMPLE
    .\Deploy-Latest.ps1
.EXAMPLE
    .\Deploy-Latest.ps1 -Launch
.EXAMPLE
    .\Deploy-Latest.ps1 -SkipPull -Launch
#>
[CmdletBinding()]
param(
    [string]$OutputFolder,
    [string]$Package,
    [switch]$Launch,
    [switch]$NoLaunch,
    [switch]$SkipPull
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\QuestBuildCommon.ps1"

$adb = Resolve-Adb
Write-Host "adb: $adb"

# ---- Settings + last-build resolution ----------------------------------------
$settings = Get-BuildSettings
if (-not $OutputFolder -and $settings -and $settings.outputFolder) {
    $OutputFolder = $settings.outputFolder
}
if (-not $OutputFolder) {
    throw "No output folder. Pass -OutputFolder or build at least once so QuestBuildSettings.json exists."
}

if (-not $Package) {
    $lb = Get-LastBuild
    if ($lb -and $lb.packageName) { $Package = $lb.packageName }
}
if (-not $Package) {
    throw "Package name unknown. Pass -Package, or rebuild once with the updated QuestBuilder so last-build.json carries packageName."
}

# ---- Decide whether to launch ------------------------------------------------
$shouldLaunch = $false
if ($Launch) {
    $shouldLaunch = $true
}
elseif ($NoLaunch) {
    $shouldLaunch = $false
}
elseif ($settings -and ($settings.PSObject.Properties.Name -contains 'launchAfterDeploy')) {
    $shouldLaunch = [bool]$settings.launchAfterDeploy
}

Write-Host "package: $Package"
Write-Host "output:  $OutputFolder"
Write-Host "launch:  $shouldLaunch"

# ---- Device check ------------------------------------------------------------
$devicesOut = & $adb devices
$deviceLines = $devicesOut | Where-Object { $_ -match '\tdevice$' }
if (-not $deviceLines) {
    Write-Warning "No authorised device connected. Plug in via USB or run 'adb connect <ip>'."
    Write-Host ($devicesOut -join "`n")
    return
}
Write-Host "device:  $($deviceLines -join '; ')"

# ---- Step 1: pre-pull existing sessions (no cleanup) -------------------------
if (-not $SkipPull) {
    Write-Host ""
    Write-Host "[1/3] Pre-pulling existing sessions ..."
    & "$PSScriptRoot\Pull-Sessions.ps1" -OutputFolder $OutputFolder -Package $Package
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Pull-Sessions returned exit code $LASTEXITCODE. Continuing with install."
    }
}
else {
    Write-Host ""
    Write-Host "[1/3] -SkipPull set; skipping pre-pull."
}

# ---- Step 2: install newest APK ---------------------------------------------
Write-Host ""
Write-Host "[2/3] Installing latest APK ..."
& "$PSScriptRoot\Install-LatestAPK.ps1" -OutputFolder $OutputFolder
if ($LASTEXITCODE -ne 0) {
    throw "Install-LatestAPK failed (exit $LASTEXITCODE)"
}

# ---- Step 3: optional launch -------------------------------------------------
Write-Host ""
if ($shouldLaunch) {
    Write-Host "[3/3] Launching $Package on headset ..."
    & $adb shell monkey -p $Package -c android.intent.category.LAUNCHER 1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Launched." -ForegroundColor Green
    }
    else {
        Write-Warning "Launch returned exit code $LASTEXITCODE."
    }
}
else {
    Write-Host "[3/3] Launch skipped (use -Launch or set launchAfterDeploy)."
}

Write-Host ""
Write-Host "Deploy complete." -ForegroundColor Green
