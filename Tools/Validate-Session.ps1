#Requires -Version 5.1
<#
.SYNOPSIS
    Structural integrity check for a pulled session bundle (or a single
    experiment CSV). PASS/FAIL verdict + summary; exit code 0 on PASS,
    1 on FAIL, 2 on usage error.

    Nothing anywhere else ever opens the experiment CSV after a pull - a
    truncated or empty 90-minute file used to be discovered at analysis time,
    weeks after the participant left. Run this in the lab (Pull-Sessions.ps1
    runs it automatically) so a bad session is a same-day decision.

    FAIL means structural corruption: no experiment CSV, unexpected header,
    no session_start, a truncated final row, or no data rows. Softer issues
    (no session_end, backward timestamps, walks without a terminal row) are
    WARNINGS - they describe how the session ended, not whether the file is
    readable.
.PARAMETER SessionFolder
    A pulled session bundle folder (contains session.json + the experiment
    CSV). The experiment CSV is auto-detected by its header.
.PARAMETER Csv
    Validate one experiment CSV directly instead of a bundle folder.
.PARAMETER Quiet
    Only print the verdict line (used by Pull-Sessions.ps1).
.EXAMPLE
    .\Validate-Session.ps1 -SessionFolder "D:\builds\app.sessions\20260804_101500"
.EXAMPLE
    .\Validate-Session.ps1 -Csv "D:\P014_1754300000000.csv"
#>
[CmdletBinding()]
param(
    [string]$SessionFolder,
    [string]$Csv,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

# Expected header = LogEvent.CsvHeader (schema v1, 58 columns). A mismatch
# means the build and this validator disagree about the contract - update
# BOTH (and SessionLoggerSchema.md) in the same commit.
$ExpectedHeader = 'schema_version,timestamp_session,frame_number,event_type,correction_source,mode,' +
    'subtype,detail,' +
    'anchor_pos_xyz,anchor_rot_xyzw,headset_pos_xyz,headset_rot_xyzw,' +
    'controller_L_pos_xyz,controller_L_rot_xyzw,controller_R_pos_xyz,controller_R_rot_xyzw,' +
    'position_valid_L,position_valid_R,orientation_valid_L,orientation_valid_R,' +
    'connected_L,connected_R,velocity_L_mps,velocity_R_mps,' +
    'battery_L_percent,battery_R_percent,' +
    'inter_controller_distance_m,inter_controller_rotation_deg,' +
    'deviation_from_baseline_m,deviation_from_baseline_deg,validation_enforced,' +
    'sleep_event_type,time_since_last_pulse_s,' +
    'walk_index,walk_phase,trial_active,move_towards_user,trigger_distance_m,perturbation_distance_m,' +
    'walk_duration_s,corrections_applied_count,max_correction_magnitude_m,rejection_reason_histogram,' +
    'calibration_step,calibration_sample_index,mean_distance_m,stddev_distance_m,mean_rot_deg,stddev_rot_deg,' +
    'accepted,rejection_reason,delta_position_m,delta_rotation_deg,ema_alpha_applied,correction_applied_m,' +
    'controller_distance_m,controller_velocity_mps,context_for'

function Find-ExperimentCsv([string]$folder) {
    $candidates = Get-ChildItem $folder -Filter '*.csv' -File -ErrorAction SilentlyContinue
    foreach ($f in $candidates) {
        $first = Get-Content $f.FullName -TotalCount 1 -ErrorAction SilentlyContinue
        if ($first -and $first.TrimStart([char]0xFEFF).StartsWith('schema_version,timestamp_session,')) {
            return $f.FullName
        }
    }
    return $null
}

# ---- Resolve target -----------------------------------------------------------
if (-not $Csv -and -not $SessionFolder) {
    Write-Host "Pass -SessionFolder <bundle> or -Csv <file>." -ForegroundColor Yellow
    exit 2
}
if (-not $Csv) {
    if (-not (Test-Path $SessionFolder)) { Write-Host "No such folder: $SessionFolder"; exit 2 }
    $Csv = Find-ExperimentCsv $SessionFolder
}

$failures = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

if (-not $Csv -or -not (Test-Path $Csv)) {
    $failures.Add("no experiment CSV found (header 'schema_version,timestamp_session,...')")
}

# ---- Stream the CSV -----------------------------------------------------------
$rows = 0
$eventCounts = @{}
$backwardSteps = 0
$lastTs = [double]::NegativeInfinity
$sessionStartSeen = $false
$terminalSeen = $false          # session_end / application_quit
$conventionsSeen = $false
$trialCsvSeen = $false
$walkOpen = @{}                 # walk_index -> count of starts without terminal
$walkCompleted = 0
$walkAbandoned = 0
$lastLine = $null
$headerOk = $false

if ($Csv -and (Test-Path $Csv)) {
    $reader = New-Object System.IO.StreamReader($Csv)
    try {
        $header = $reader.ReadLine()
        if ($null -eq $header) {
            $failures.Add('file is empty')
        }
        else {
            $header = $header.TrimStart([char]0xFEFF)
            if ($header -ne $ExpectedHeader) {
                $failures.Add('header does not match the schema v1 contract (58 columns) - build/validator mismatch or corrupt file')
            } else { $headerOk = $true }
        }

        while ($null -ne ($line = $reader.ReadLine())) {
            if ($line.Length -eq 0) { continue }
            $lastLine = $line
            $rows++

            # Fields 0-6 (schema_version..subtype) never contain quoted commas
            # by construction - safe to prefix-split. Deeper fields may.
            $parts = $line.Split(',', 9)
            if ($parts.Count -lt 7) { continue }

            $ts = 0.0
            if ([double]::TryParse($parts[1], [System.Globalization.NumberStyles]::Float,
                    [System.Globalization.CultureInfo]::InvariantCulture, [ref]$ts)) {
                if ($ts -lt $lastTs) { $backwardSteps++ }
                $lastTs = $ts
            }

            $eventType = $parts[3]
            if ($eventCounts.ContainsKey($eventType)) { $eventCounts[$eventType]++ }
            else { $eventCounts[$eventType] = 1 }

            if ($eventType -eq 'session_event') {
                switch ($parts[6]) {
                    'session_start' { $sessionStartSeen = $true }
                    'session_end' { $terminalSeen = $true }
                    'application_quit' { $terminalSeen = $true }
                    'conventions' { $conventionsSeen = $true }
                    'trial_csv' { $trialCsvSeen = $true }
                }
            }
            elseif ($eventType -eq 'walk_event' -and $line.IndexOf('"') -lt 0) {
                # walk_event rows carry no quoted fields today (histogram is
                # null in phase 1); full split is safe on these lines.
                $all = $line.Split(',')
                if ($all.Count -ge 35) {
                    $wIdx = $all[33]
                    $wPhase = $all[34]
                    switch ($wPhase) {
                        'start' {
                            if ($walkOpen.ContainsKey($wIdx)) { $walkOpen[$wIdx]++ } else { $walkOpen[$wIdx] = 1 }
                        }
                        'end' { $walkCompleted++; if ($walkOpen.ContainsKey($wIdx)) { $walkOpen[$wIdx] = 0 } }
                        'abandoned' { $walkAbandoned++; if ($walkOpen.ContainsKey($wIdx)) { $walkOpen[$wIdx] = 0 } }
                    }
                }
            }
        }
    }
    finally { $reader.Dispose() }

    if ($rows -lt 2) { $failures.Add("only $rows data row(s)") }
    if (-not $sessionStartSeen) { $failures.Add('no session_start row') }

    # Truncation check: the writer terminates every row; a mid-row cut leaves
    # a final line with too few commas (quote-bearing lines are exempted from
    # the count check).
    if ($lastLine -and $lastLine.IndexOf('"') -lt 0) {
        $commaCount = ($lastLine.ToCharArray() | Where-Object { $_ -eq ',' }).Count
        if ($commaCount -lt 57) { $failures.Add("final row is truncated ($commaCount of 57 commas)") }
    }

    if (-not $terminalSeen) {
        $warnings.Add('no session_end/application_quit row - crash, battery death, or force-stop (tail data still flushed within the flush window)')
    }
    if ($backwardSteps -gt 0) { $warnings.Add("$backwardSteps backward timestamp step(s)") }

    $openWalks = @($walkOpen.GetEnumerator() | Where-Object { $_.Value -gt 0 })
    if ($openWalks.Count -gt 1) {
        $warnings.Add("$($openWalks.Count) walks have a start but no end/abandoned row (1 in-flight walk at shutdown is normal)")
    }
    if ($headerOk -and -not $conventionsSeen) {
        $warnings.Add('no conventions row (pre-2026-08-04 build?)')
    }
    if ($headerOk -and -not $trialCsvSeen) {
        $warnings.Add('no trial_csv summary row (pre-2026-08-04 build, or trial CSV never loaded)')
    }
}

# ---- Bundle-level checks ------------------------------------------------------
if ($SessionFolder -and (Test-Path $SessionFolder)) {
    $sidecar = Join-Path $SessionFolder 'session.json'
    if (Test-Path $sidecar) {
        try {
            $meta = Get-Content $sidecar -Raw | ConvertFrom-Json
            if ($meta.PSObject.Properties.Name -contains 'cleanExit' -and -not [bool]$meta.cleanExit) {
                $warnings.Add('session.json reports cleanExit=false')
            }
        } catch { $warnings.Add("session.json unparseable: $_") }
    } else {
        $warnings.Add('no session.json sidecar in bundle')
    }
}

# ---- Verdict ------------------------------------------------------------------
$name = if ($Csv) { Split-Path $Csv -Leaf } elseif ($SessionFolder) { Split-Path $SessionFolder -Leaf } else { '?' }
$pass = ($failures.Count -eq 0)

if ($pass) {
    Write-Host ("PASS  {0}  ({1} rows, {2} walks completed, {3} abandoned, {4} warning(s))" -f `
        $name, $rows, $walkCompleted, $walkAbandoned, $warnings.Count) -ForegroundColor Green
} else {
    Write-Host ("FAIL  {0}" -f $name) -ForegroundColor Red
    foreach ($f in $failures) { Write-Host ("  FAIL: {0}" -f $f) -ForegroundColor Red }
}
if (-not $Quiet) {
    foreach ($w in $warnings) { Write-Host ("  warn: {0}" -f $w) -ForegroundColor Yellow }
    if ($eventCounts.Count -gt 0) {
        $summary = ($eventCounts.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join '  '
        Write-Host "  rows: $summary"
    }
}
elseif (-not $pass) {
    foreach ($w in $warnings) { Write-Host ("  warn: {0}" -f $w) -ForegroundColor Yellow }
}

if ($pass) { exit 0 } else { exit 1 }
