# Shared helpers for the Quest build tooling. Dot-source this file:  . "$PSScriptRoot\QuestBuildCommon.ps1"

function Resolve-Adb {
    # 1. adb already on PATH
    $cmd = Get-Command adb -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    # 2. Standard Android SDK environment variables
    $candidates = @()
    foreach ($var in @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT)) {
        if ($var) { $candidates += (Join-Path $var 'platform-tools\adb.exe') }
    }

    # 3. adb bundled with any installed Unity Editor (newest first)
    $hubGlob = 'C:\Program Files\Unity\Hub\Editor\*\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe'
    $candidates += (Get-ChildItem -Path $hubGlob -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -ExpandProperty FullName)

    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    throw "adb.exe not found. Install Android platform-tools, set ANDROID_HOME, or install the Android module via Unity Hub."
}

function Get-BuildSettings {
    # Locates UserSettings/QuestBuildSettings.json below the repo root, so the Tools
    # scripts work regardless of how the Unity project subfolder is named.
    $root = Resolve-Path (Join-Path $PSScriptRoot '..')
    $hit = Get-ChildItem -Path $root -Recurse -Depth 2 -Filter 'QuestBuildSettings.json' -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'UserSettings' } | Select-Object -First 1
    if ($hit) {
        return Get-Content $hit.FullName -Raw | ConvertFrom-Json
    }
    return $null
}
