# Session Log Analysis Handoff

You are about to analyze CSV session logs from a Meta Quest 3 mixed-reality
gait research APK. This document is **self-contained** — you should not need
to ask the project owner anything to interpret the data. Read the TL;DR
first; the rest is reference.

**Scope.** This document is for **development analysis** — drift-correction
quality, build health, runtime diagnostics. The actual kinematic science
data (gait timing, joint angles, etc.) comes from external sources
(instrumented gait mat, OptiTrack, etc.) and is intentionally out of scope
here.

**Maintaining this document.** Whenever the code changes what is logged, how
it is logged, or where it is written, update this document in the same commit.
The schema below is the contract with downstream analysis; doc drift is silent
breakage. (Same rule lives in the repo-root `CLAUDE.md`.)

---

## TL;DR

- **What it is**: One CSV per session, wide and sparse (58 columns), written
  by a Unity app called `Unity-QuestVisionKit` running on a Quest 3 headset.
- **Where it comes from**: `Application.persistentDataPath/<participantId>_<unixMs>.csv`
  on the device, pulled to a host machine via ADB.
- **What the experiment is**: A participant walks back and forth across a
  gait mat in mixed reality, stepping over a virtual obstacle. The
  experiment relies on the obstacle staying fixed in physical space to
  better than 1cm across a 45–90 minute session. The whole project exists
  to characterize and fix the **anchor drift** that breaks this.
- **First useful thing to do**: Filter rows where
  `event_type=='state_snapshot'` and `correction_source=='anchor_baseline'`,
  parse `anchor_pos_xyz` (pipe-separated `"x|y|z"`), and plot the X/Y/Z over
  `timestamp_session`. That's the AprilTag spatial-anchor's pose over the
  session — drift in those traces is the headline metric.

You'll see two parallel data streams when the session was actively testing:
`correction_source=='anchor_baseline'` (the AprilTag-based anchor, always
on) and `correction_source=='controller_placer'` (a controller-derived
anchor, on only during locked test windows). Comparing the two over a
shared time window is the headline analysis.

---

## Project context (just enough to interpret the data)

### Hardware setup

- **Quest 3 headset** in mixed-reality mode (passthrough on).
- **Two Touch Plus controllers** seated in a fixed rig at the edge of a
  gait mat, at least 1.5m apart, baseline parallel to the obstacle's long
  axis. They are stationary throughout each session (held in place by the
  rig).
- **AprilTag constellation board** placed in the room. Stereo cameras on
  the Quest detect the tags; the app uses them to anchor a virtual
  obstacle in physical space.
- **Gait mat** on the floor between the controllers; the participant walks
  back and forth across it.

### Experimental flow

1. Experimenter calibrates the AprilTag constellation (or it auto-calibrates
   when ≥3 tags are visible for 5 consecutive frames).
2. A virtual yoga-block obstacle (1m × 6cm × 6cm) is placed at the anchor.
3. Participant walks across the mat, stepping over the obstacle. **Each
   walk is one trial** with a `TrialCondition` from a CSV that controls
   whether/how the obstacle perturbs during that walk.
4. Sessions run 50–100 walks at 15–20 s each, total ~45–90 minutes.

### The drift problem

Meta's spatial anchor system alone drifts more than the 1cm tolerance the
experiment needs over a session. The project explores multiple correction
strategies:

- **AprilTag-based correction** (mostly implemented): a constellation of
  AprilTags anchors an `OVRSpatialAnchor`; per-frame RANSAC + Kabsch on tag
  detections produces a small rigid correction lerped onto a `CorrectionRoot`
  child of the anchor.
- **Controller-based placement / correction**: uses the rig-mounted Touch
  controllers' optical+IMU tracking as a high-precision pose reference.
  Two components ship: `ControllerObstaclePlacer` (spawns an obstacle
  between the controllers and anchors it on a button press to its own
  dedicated `OVRSpatialAnchor`) and `ControllerDriftCorrector` (per-frame
  gate evaluation, EMA application, snap detection — emits
  `correction_event`, `snap_event`, and `source_state_change` rows).

The two systems run in parallel and the log captures both anchors' poses
over time so their drift can be compared offline. Phase 2 correction
(gates, EMA, snap detection) **is now active** — see the `correction_event`
and `snap_event` sections.

### Two anchors, tracked in parallel

The headline architectural fact for analysis: **there are two independent
`OVRSpatialAnchor` instances** that can both be active at the same time:

| Anchor                     | Owned by                       | When active           | Source label in log     |
|----------------------------|--------------------------------|------------------------|-------------------------|
| Constellation (AprilTag)   | `ConstellationDriftCorrector`  | Once calibrated, on for the session | `anchor_baseline` |
| Controller-placer          | `ControllerObstaclePlacer`     | Only while a "lock" is held | `controller_placer` |

These do **not** share state. Their poses can diverge by an arbitrary
amount; that divergence is exactly what the analysis is trying to
characterize.

---

## CSV file format

### File location and pull

Each app launch produces a per-session bundle on the Quest device:

```
/sdcard/Android/data/<package>/files/Sessions/<sessionId>/
  session.log                          ← Unity Console capture (dev)
  session.json                         ← dev sidecar with build identity + counters
  <participantId>_<unixMs>.csv         ← experiment CSV (this document's main subject)
  apriltag_solver_comparison.csv       ← optional sample CSV (when its component is enabled)
```

where `<package>` is the Unity build's bundle ID (e.g.
`com.BlackWhaleStudio.UnityQuestVisionKit`) and `<sessionId>` is
`yyyy-MM-dd_HHmmss_xxxxxxxx` (UTC stamp + 8 hex chars).

Recommended pull (organizes everything by APK build automatically):

```powershell
.\Tools\Pull-Sessions.ps1
```

This pulls each session folder into a matching local
`<outputFolder>/<apkBase>.sessions/<sessionId>/` next to the APK it came
from, builds a `sessions-index.json` summary, and optionally mirrors to a
cloud-synced folder. Sessions whose APK isn't on this machine land in
`_unmatched_sessions/`. Works over USB or wireless ADB. See the QuestBuild
plan for full options.

Fallback raw `adb` (loses the per-build attribution):

```bash
adb pull /sdcard/Android/data/<package>/files/Sessions/ ./session_logs/
```

### What's in a session folder

Each `<sessionId>/` is one app launch's bundle of outputs:

| File | Purpose | Present in | Documented in |
|---|---|---|---|
| `<participantId>_<unixMs>.csv` | The wide experiment CSV (drift, walks, snapshots). Main subject of this document. | All builds | This file (the schema below). |
| `session.log` | Timestamped capture of every Unity `Debug.Log*` line from the launch — info, warning, error, exception with stack trace. | All builds | "Dev session log" section below. |
| `session.json` | Dev sidecar with the build identity baked into the APK + lifecycle counters (errors, exceptions, clean-exit flag). | All builds | "Dev session log" section below. |
| `reference_anchors.csv` | Pose of N reference `OVRSpatialAnchor`s (center + corners) over time — drift-uniformity probe. | **Dev builds only** | "Reference anchors" section below. |
| `tracking_events.csv` | Sparse rows on head / controller / user-presence state transitions. | **Dev builds only** | "Tracking events" section below. |
| `headset_poses.csv` | Per-frame headset world pose + linear/angular velocity. Large (~30–50 MB / 45 min). | **Dev builds only** | "High-frequency pose + jitter stats" section below. |
| `headset_pose_stats.csv` | 1 Hz rolling-window RMS jitter (translation mm, rotation deg) computed on device. | **Dev builds only** | "High-frequency pose + jitter stats" section below. |
| `controller_poses.csv` | Per-frame L+R controller poses + validity. | **Dev builds only** | "High-frequency pose + jitter stats" section below. |
| `controller_pose_stats.csv` | 1 Hz rolling-window RMS jitter per controller. | **Dev builds only** | "High-frequency pose + jitter stats" section below. |
| `apriltag_solver_comparison.csv` | Per-frame AprilTag solver comparison rows. | Only when the sample is enabled this run | Header in the file itself. |

The experiment CSV is what the rest of this document describes in detail; the
dev `session.*` files are mostly for diagnosing crashes and tying CSV anomalies
to specific builds (see "Joining build metadata to CSV analyses" below). The
dev-only CSVs above ship in development builds only — their **absence** in a
session folder is the signal that the session ran on a production build.

**On-device session retention across re-installs.** `adb install -r` (used by
`Tools/Install-LatestAPK.ps1` and the Build Panel's auto-deploy flow) preserves
the app's `persistentDataPath`, so `Sessions/<sessionId>/` folders from previous
builds remain on the device after a new install. The dev-side `SessionLogger`
trims folders beyond `maxSessionsRetainedOnDevice` (default 50) on each launch.
The deploy flow also pre-pulls existing sessions before installing, so the
local `<apkBase>.sessions/` mirror is always current — you can additionally run
`Tools/Pull-Sessions.ps1` (with optional `-Cleanup`) at any time to refresh or
clear the device side.

### Format

- **Single CSV per session**, header row first, one event per row thereafter.
- **58 columns**, intentionally wide and sparse — most cells are empty for
  any given row. Cells are only populated by the event type that needs them.
- **Encoding**:
  - All numeric values use **invariant culture** (`.` as decimal separator).
  - Floats are written in "R" round-trip format (full precision).
  - **Booleans** are `0` or `1` (empty cell = null/unknown).
  - **`Vector3`** values are encoded as `"x|y|z"` in a single cell (three
    `R`-format floats, pipe-separated).
  - **`Quaternion`** values are encoded as `"x|y|z|w"` in a single cell
    (four `R`-format floats, pipe-separated).
  - String cells containing `,`, `"`, `\n`, or `\r` are RFC-4180 quoted with
    `""` doubling.
  - **Empty cell** means the field is not populated for that row — treat as
    null / missing data, not as zero.

### `timestamp_session` vs wall clock

- `timestamp_session` is **seconds since the session started** (from
  `Time.realtimeSinceStartupAsDouble` minus the value at session start). Use
  this as the primary time axis — it's monotonic and high-precision within
  the session.
- The **wall-clock time** of the session start is recorded once, in the
  first row's `detail` field (see [Session header](#session-header) below).
  Subtract `timestamp_session=0` from that wall clock to map any row to a
  wall-clock time.
- `frame_number` is `Time.frameCount` at construction — useful as a
  per-frame correlator but not as a time axis (frame count tracks render
  frames, not wall time).

---

## Full schema (58 columns)

### Required on every row (6 columns)

| Column              | Type    | Meaning |
|---------------------|---------|---------|
| `schema_version`    | int     | Schema version. Currently **1**. Bumps invalidate older files. |
| `timestamp_session` | float64 | Seconds since session start (`Time.realtimeSinceStartupAsDouble` - start). Monotonic, high precision. |
| `frame_number`      | int     | `Time.frameCount` at the row's construction. |
| `event_type`        | string  | One of the [event types](#event-types) below. |
| `correction_source` | string  | One of the [correction sources](#correction-sources) below. |
| `mode`              | string  | `applied`, `observe`, or `n/a`. |

### Subtype / detail (used by `session_event`, `source_state_change`)

| Column      | Type   | Meaning |
|-------------|--------|---------|
| `subtype`   | string | Event-type-specific tag (e.g. `session_start`, `obstacle_placer_lock`). |
| `detail`    | string | Free-form `;`-delimited `key=value` payload. |

### Pose snapshot fields (used by `state_snapshot`, also by `snap_event` /
### `validation_walk` if/when those land)

| Column                   | Type        | Meaning |
|--------------------------|-------------|---------|
| `anchor_pos_xyz`         | `x\|y\|z`   | World position of the anchor associated with this `correction_source`. |
| `anchor_rot_xyzw`        | `x\|y\|z\|w`| World rotation of that anchor. For `correction_source=apriltag_single`, yaw-flattened (upright, pitch/roll = 0) as of 2026-07-14 — see `SessionLoggerSchema.md`; earlier sessions carried the tag's full 3-D rotation. |
| `headset_pos_xyz`        | `x\|y\|z`   | World position of the headset (Camera.main). |
| `headset_rot_xyzw`       | `x\|y\|z\|w`| World rotation of the headset. |
| `controller_L_pos_xyz`   | `x\|y\|z`   | Left controller world position. Empty if the controller is outside the per-source working range. |
| `controller_L_rot_xyzw`  | `x\|y\|z\|w`| Left controller world rotation. |
| `controller_R_pos_xyz`   | `x\|y\|z`   | Right controller world position. Empty if out of range. |
| `controller_R_rot_xyzw`  | `x\|y\|z\|w`| Right controller world rotation. |

### Controller validity / connection

| Column                  | Type | Meaning |
|-------------------------|------|---------|
| `position_valid_L`      | 0/1  | `OVRInput.GetControllerPositionValid(LTouch)`. **See caveat below — true for extrapolated poses.** |
| `position_valid_R`      | 0/1  | Same, right hand. |
| `orientation_valid_L`   | 0/1  | `OVRInput.GetControllerOrientationValid(LTouch)`. Same caveat. |
| `orientation_valid_R`   | 0/1  | Same, right hand. |
| `connected_L`           | 0/1  | `OVRInput.IsControllerConnected(LTouch)`. |
| `connected_R`           | 0/1  | Same, right hand. |
| `velocity_L_mps`        | float| Self-computed linear-velocity magnitude of left controller (m/s, pose-delta over the previous frame). |
| `velocity_R_mps`        | float| Same, right hand. |
| `battery_L_percent`     | float (0-100) | Last sampled battery (sampled once per minute). |
| `battery_R_percent`     | float (0-100) | Same, right hand. |

### Rigid body validator

| Column                          | Type  | Meaning |
|---------------------------------|-------|---------|
| `inter_controller_distance_m`   | float | Live distance between the two controllers, in meters. |
| `inter_controller_rotation_deg` | float | Live relative rotation between controllers, degrees from identity. |
| `deviation_from_baseline_m`     | float | Distance deviation from the calibrated rigid-body baseline. |
| `deviation_from_baseline_deg`   | float | Rotation deviation from the calibrated baseline. |
| `validation_enforced`           | 0/1   | Whether the rigid-body validation gate is currently enforced (Phase 1 = false / log-only). |

### Sleep events

| Column                    | Type   | Meaning |
|---------------------------|--------|---------|
| `sleep_event_type`        | string | One of `pulse`, `disconnect`, `reconnect`, `battery_sample`. |
| `time_since_last_pulse_s` | float  | Seconds since the previous keep-alive pulse (on `pulse` rows). |

### Walk / trial

| Column                       | Type   | Meaning |
|------------------------------|--------|---------|
| `walk_index`                 | int    | Walk number, sourced from `TrialCondition.TrialNumber`. |
| `walk_phase`                 | string | `start`, `moved`, `reset`, or `end`. |
| `trial_active`               | 0/1    | Does the obstacle perturb during this trial? |
| `move_towards_user`          | 0/1    | Direction of the perturbation. As of 2026-07-14 the motion is guaranteed **horizontal** along the obstacle's placement-yaw forward axis, and toward/away is computed on that same axis. Earlier sessions could move along the tag's full 3-D forward (vertical for a flat tag) with a world-Z sign that disagreed with the motion axis. |
| `trigger_distance_m`         | float  | Proximity (XZ) at which the obstacle perturbs. |
| `perturbation_distance_m`    | float  | How far the obstacle moves on trigger. |
| `walk_duration_s`            | float  | Walk end - walk start, in seconds. Populated on `walk_phase=end`. |
| `corrections_applied_count`  | int    | **Reserved**: count of accepted controller corrections during the walk. Always empty/zero in current data — Phase 2. |
| `max_correction_magnitude_m` | float  | **Reserved**: largest correction magnitude. Phase 2. |
| `rejection_reason_histogram` | string | **Reserved**: JSON histogram of gate rejection reasons. Phase 2. |

### Calibration (rigid body baseline capture)

| Column                     | Type   | Meaning |
|----------------------------|--------|---------|
| `calibration_step`         | string | `rigid_body_baseline_start`, `rigid_body_sample`, `rigid_body_sample_invalid`, `rigid_body_baseline_captured`, `rigid_body_baseline_failed`. |
| `calibration_sample_index` | int    | Sample index during capture (0..N-1). |
| `mean_distance_m`          | float  | Mean inter-controller distance over the captured samples (on the summary row). |
| `stddev_distance_m`        | float  | Distance stddev. |
| `mean_rot_deg`             | float  | Mean angular deviation from the reference rotation. |
| `stddev_rot_deg`           | float  | Rotation stddev. |

### Correction events / snap events

These columns are **actively emitted** as of commit `541c16b`:

- `correction_source="apriltag"` rows come from `AprilTagCorrectionLogger`
  (observer on `ConstellationDriftCorrector.OnCorrectionTriggered` /
  `OnCorrectionRejected`). Populates `accepted`, `rejection_reason` (on
  reject), `delta_position_m`, `delta_rotation_deg`.
- `correction_source="controller"` rows come from `ControllerDriftCorrector`'s
  per-frame gate evaluation + EMA application. Populates the full column
  set including `ema_alpha_applied`, `correction_applied_m`,
  `controller_distance_m`, `controller_velocity_mps`.
- `snap_event` rows come from `ControllerDriftCorrector` when the EMA-snap
  threshold trips. `context_for` tags the parent snap-event ID on
  ring-buffer dump rows.

| Column                  | Type   | Meaning |
|-------------------------|--------|---------|
| `accepted`              | 0/1    | Did the correction proposal pass all gates? |
| `rejection_reason`      | string | Gate name that rejected (validity / range / velocity / rigid_body / facing / step_over). |
| `delta_position_m`      | float  | Magnitude of the proposed correction delta. |
| `delta_rotation_deg`    | float  | Magnitude of the proposed rotation delta. |
| `ema_alpha_applied`     | float  | EMA filter coefficient used. |
| `correction_applied_m`  | float  | Magnitude actually written to the anchor. |
| `controller_distance_m` | float  | Distance from headset to the closer controller. |
| `controller_velocity_mps` | float| Max of the two controller velocities. |
| `context_for`           | string | Ring-buffer dump rows tag the parent `snap_event` ID here. |

---

## Event types

Every row has an `event_type`. Six are actually emitted by the current
build; four are defined but reserved for future code.

### Emitted in current data

#### `session_event`

Sparse lifecycle markers. The `subtype` column distinguishes them.

| `subtype` value             | When                                      | `detail` contents |
|-----------------------------|-------------------------------------------|-------------------|
| `session_start`             | App start, when logger comes alive        | `build=...;scene=...;participant=...;unix_ms=...;schema_version=...;flush_interval_s=...;notes=...` |
| `session_end`               | Logger OnDisable                          | (empty) |
| `application_quit`          | App quit                                  | (empty) |
| `rigid_body_baseline`       | Once after a successful baseline capture  | `mean_distance_m=...;stddev_distance_m=...;mean_rot_deg=...;stddev_rot_deg=...;distance_tolerance_m=...;rotation_tolerance_deg=...;validation_enforced=...;samples=...` |
| `reconnect_moved`           | A controller reconnected far from its last-known-good pose | `side=L\|R;moved_m=...;warn_threshold_m=...` |
| `obstacle_placer_lock`      | The `ControllerObstaclePlacer` lock-toggle fired | `locked=0\|1;anchor=0\|1` |
| `controller_corrector_activated`   | `ControllerDriftCorrector.Activate()` succeeded — anchor reference captured | varies (write-mode + gate config) |
| `controller_corrector_deactivated` | `ControllerDriftCorrector.Deactivate()` called    | (empty) |
| `controller_corrector_recapture`   | `ControllerDriftCorrector.RecaptureReference()` re-anchored | varies (see source) |
| `synth_load_test_start/end` | If the verification harness was run        | `target_events=...;rate_hz=...` or `emitted=...` |

`correction_source` is always `system` for `session_event`s. `mode` is `n/a`.

Example row:
```
1,0.123,42,session_event,system,n/a,session_start,"build=1.0.0;scene=Triangle Constellation Pairing Down (Corrected);participant=P000;unix_ms=1747234567890;schema_version=1;flush_interval_s=2.00;notes=",,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,
```

#### `state_snapshot`

The bulk of the data. ~30Hz periodic pose + state captures (raised from 5Hz
for finer drift resolution — the three snapshot streams below now sample at
30Hz and stay row-aligned for offline comparison).

- `correction_source=='anchor_baseline'` rows come from `AnchorBaselineLogger`,
  ~30Hz, always on once the AprilTag constellation is calibrated. They
  populate `anchor_pos_xyz` / `anchor_rot_xyzw` (the AprilTag anchor's bare
  world pose), `headset_pos_xyz` / `headset_rot_xyzw` (head pose), and (when
  the controllers are within 2m of the head) the controller pose, validity,
  velocity, and rigid-body fields. Note: controller pose cells are left empty
  when a controller is >2m from the head, and `position_valid_*` reads true
  even during IMU dead-reckoning — cross-check `velocity_*_mps` before trusting
  a static-controller pose.
- `correction_source=='controller_placer'` rows come from
  `ControllerObstaclePlacer`, ~30Hz, **only while a placer-anchor lock is
  active**. They populate `anchor_pos_xyz` / `anchor_rot_xyzw` (the
  placer's dedicated anchor pose). Other pose fields are empty.

#### `sleep_event`

Sparse rows from `ControllerSleepMitigation`. `sleep_event_type` tells you
which:

- `pulse` — keep-alive haptic fired (every ~5s by default). `time_since_last_pulse_s` populated.
- `disconnect` / `reconnect` — controller transitioned. The corresponding `controller_L_pos_xyz` / `controller_R_pos_xyz` and `connected_L/R` flags are populated.
- `battery_sample` — ~1/min, populates `battery_L_percent` / `battery_R_percent` + `connected_L/R`.

#### `calibration_event`

Emitted during a rigid-body baseline capture (30 samples over ~1s) and once
afterward as a summary. `calibration_step` tells you the row's role:

- `rigid_body_baseline_start` — capture starting.
- `rigid_body_sample` — one per captured sample, with `inter_controller_distance_m` + `inter_controller_rotation_deg`.
- `rigid_body_sample_invalid` — controller validity dropped during this sample.
- `rigid_body_baseline_captured` — summary row with `mean_distance_m` / `stddev_distance_m` / `mean_rot_deg` / `stddev_rot_deg`.
- `rigid_body_baseline_failed` — too few valid samples; baseline NOT updated.

There's also a corresponding `session_event` with `subtype=rigid_body_baseline`
on success (see above) that's easier to query.

#### `walk_event`

Trial lifecycle markers emitted by `SessionLoggerTrialSubscriber`. Each
walk produces 2–4 rows depending on whether the obstacle moved / reset:

- `walk_phase=start` — fired when `TrialSequencer.OnTrialLoaded` fires; populates trial parameters.
- `walk_phase=moved` — fired when the obstacle perturbed (only for `trial_active=1` trials).
- `walk_phase=reset` — fired when the obstacle reset to origin.
- `walk_phase=end` — fired on `OnTrialCompleted`. Populates `walk_duration_s`. The three "cumulative correction" columns are reserved for Phase 2 and currently empty.

`walk_index` is the trial number from `TrialCondition.TrialNumber`.

#### `*battery_sample*` is a `sleep_event` subtype, not its own event_type — listed under `sleep_event` above. Mentioned here because it's easy to miss.

### Reserved (defined in code, not yet emitted)

`correction_event`, `source_state_change`, and `snap_event` were originally
listed here as Phase 2 reserved — they are **now actively emitted** (see the
sections above for what populates each column). `correction_source` values
`controller` and `apriltag` are likewise active.

What remains reserved for future code:

- `validation_walk` — Phase 3 periodic re-validation markers.
- `correction_source = "optitrack"` — future OptiTrack integration.

---

## Correction sources

The `correction_source` column says **which subsystem this row came from**.

| Value                | Emitted by                                  | Status in current data |
|----------------------|----------------------------------------------|------------------------|
| `system`             | Logger lifecycle, sleep mitigation, trial subscriber | Active |
| `anchor_baseline`    | `AnchorBaselineLogger` (samples AprilTag anchor) | Active (always on after AprilTag calibration) |
| `controller_placer`  | `ControllerObstaclePlacer` (samples its dedicated anchor) | Active **only while a placer-anchor lock is held** |
| `controller`         | `ControllerDriftCorrector`                   | **Active** — emits `correction_event`, `snap_event`, `state_snapshot`, `source_state_change`, and several `session_event` subtypes |
| `apriltag`           | `AprilTagCorrectionLogger` (observer on `ConstellationDriftCorrector`) | **Active** — emits `correction_event` on accept/reject |
| `optitrack`          | Future OptiTrack integration                 | **Reserved** |

A correction source going silent (no rows for a stretch) usually means
**it's out of its working range or temporarily unavailable**, not that
something failed. Phase 2 will explicitly emit `source_state_change` rows on
those transitions, but the current build just goes silent.

---

## Session lifecycle (what a "normal" session looks like)

A typical session, in row order:

1. **`session_event` `subtype=session_start`** — first row. `detail` carries the build/scene/participant/wall-clock-ms.
2. Maybe immediately: **`sleep_event battery_sample`** (battery sampled on first frame of the provider).
3. **`session_event subtype=rigid_body_baseline`** — emitted once the experimenter triggers `ControllerRigidBodyValidator.CaptureBaselineNow()` (or it's triggered programmatically). Preceded by 30 `calibration_event` sample rows + a `calibration_event` summary row.
4. **`state_snapshot anchor_baseline`** rows begin at 30Hz once the AprilTag constellation auto-calibrates (~5 consecutive frames with ≥3 tags visible). These continue for the rest of the session.
5. **`walk_event walk_phase=start`** when the first trial loads, then `moved` / `reset` / `end` per trial. Trials advance for the duration of the session.
6. Optional: **`session_event subtype=obstacle_placer_lock`** rows when the experimenter presses the left index trigger. Each toggle creates / destroys a `ControllerPlacerAnchor`. While the anchor exists, **`state_snapshot controller_placer`** rows interleave with the `anchor_baseline` rows at 30Hz.
7. Periodic **`sleep_event pulse`** every ~5s; battery_sample every ~60s; `disconnect/reconnect` only if a controller actually drops.
8. **`session_event subtype=session_end`** or `application_quit` on app close.

---

## Walks ARE trials

A **trial** and a **walk** are the same event in this project. A trial is
the experimental condition that applies during one walk across the gait
mat. The `TrialSequencer` loads the next trial when the previous one ends.

Phase fields populated on each `walk_event`:

| `walk_phase` | Fires on                       | Populated fields (besides `walk_index` + the trial params) |
|--------------|--------------------------------|------------------------------------------------------------|
| `start`      | `TrialSequencer.OnTrialLoaded`  | (none extra — trial params only)                          |
| `moved`      | `ObstacleController.OnObstacleMoved` | (none extra)                                          |
| `reset`      | `ObstacleController.OnObstacleReset` | (none extra)                                          |
| `end`        | `ObstacleController.OnTrialCompleted` | `walk_duration_s`                                    |

Trial parameters (`trial_active`, `move_towards_user`, `trigger_distance_m`,
`perturbation_distance_m`) are repeated on each `walk_event` of the same
walk to make filtering easier — you don't need to forward-fill from the
`start` row.

The "cumulative correction" columns on `walk_phase=end` are reserved
(empty) in current data — Phase 2 will populate them.

---

## Session header

The first row of every CSV is a `session_event` with `subtype=session_start`
and a `detail` field carrying session metadata as `;`-delimited
`key=value` pairs. Currently:

| key                | meaning |
|--------------------|---------|
| `build`            | `Application.version` of the running app |
| `scene`            | Active scene name (typically `Triangle Constellation Pairing Down (Corrected)`) |
| `participant`      | Participant ID configured on the `SessionLogger` |
| `unix_ms`          | Wall-clock millisecond of session start |
| `schema_version`   | The `schema_version` value used by this file |
| `flush_interval_s` | Writer flush cadence in seconds |
| `notes`            | Free-form notes the experimenter configured (often empty) |

Parse this once; it's how you map `timestamp_session` → wall-clock time.

---

## Common analyses (pandas snippets)

The recipes below assume you've loaded the CSV as a pandas DataFrame.

### Loading

```python
import pandas as pd

df = pd.read_csv("P000_1747234567890.csv", low_memory=False)
print(df.shape)             # rows × 58
print(df["event_type"].value_counts())
print(df["correction_source"].value_counts())
```

### Parsing Vector3 / Quaternion cells

```python
def parse_vec3(cell):
    if pd.isna(cell) or cell == "":
        return (None, None, None)
    x, y, z = cell.split("|")
    return float(x), float(y), float(z)

def parse_quat(cell):
    if pd.isna(cell) or cell == "":
        return (None, None, None, None)
    x, y, z, w = cell.split("|")
    return float(x), float(y), float(z), float(w)

df[["ax", "ay", "az"]] = df["anchor_pos_xyz"].apply(parse_vec3).apply(pd.Series)
df[["hx", "hy", "hz"]] = df["headset_pos_xyz"].apply(parse_vec3).apply(pd.Series)
```

### Parsing the session header

```python
header_row = df[df["subtype"] == "session_start"].iloc[0]
detail = dict(kv.split("=", 1) for kv in header_row["detail"].split(";") if "=" in kv)
print(detail)
# {'build': '1.0.0', 'scene': 'Triangle Constellation Pairing Down (Corrected)',
#  'participant': 'P000', 'unix_ms': '1747234567890', ...}
session_start_unix_ms = int(detail["unix_ms"])
```

### Plot the AprilTag anchor's pose over the session

```python
import matplotlib.pyplot as plt

baseline = df[(df["event_type"] == "state_snapshot")
              & (df["correction_source"] == "anchor_baseline")].copy()
baseline[["ax", "ay", "az"]] = baseline["anchor_pos_xyz"].apply(parse_vec3).apply(pd.Series)

fig, axes = plt.subplots(3, 1, sharex=True, figsize=(10, 6))
for col, ax in zip(["ax", "ay", "az"], axes):
    ax.plot(baseline["timestamp_session"], baseline[col])
    ax.set_ylabel(col)
axes[-1].set_xlabel("timestamp_session (s)")
plt.suptitle("AprilTag anchor pose over session")
```

### Compare placer-anchor drift to AprilTag-anchor drift

```python
ab = df[(df["event_type"] == "state_snapshot")
        & (df["correction_source"] == "anchor_baseline")].copy()
cp = df[(df["event_type"] == "state_snapshot")
        & (df["correction_source"] == "controller_placer")].copy()
ab[["ax_b", "ay_b", "az_b"]] = ab["anchor_pos_xyz"].apply(parse_vec3).apply(pd.Series)
cp[["ax_p", "ay_p", "az_p"]] = cp["anchor_pos_xyz"].apply(parse_vec3).apply(pd.Series)

# Align by nearest timestamp (placer is the rarer / on-demand stream)
ab = ab[["timestamp_session", "ax_b", "ay_b", "az_b"]].sort_values("timestamp_session")
cp = cp[["timestamp_session", "ax_p", "ay_p", "az_p"]].sort_values("timestamp_session")
merged = pd.merge_asof(cp, ab, on="timestamp_session", direction="nearest")

merged["d_xyz_m"] = ((merged["ax_p"] - merged["ax_b"]) ** 2
                   + (merged["ay_p"] - merged["ay_b"]) ** 2
                   + (merged["az_p"] - merged["az_b"]) ** 2) ** 0.5
print(merged["d_xyz_m"].describe())
merged.plot(x="timestamp_session", y="d_xyz_m", title="placer - baseline anchor distance (m)")
```

### Walk durations + per-walk row stitch

```python
walks = df[df["event_type"] == "walk_event"]
ends = walks[walks["walk_phase"] == "end"]
print(ends[["walk_index", "trial_active", "move_towards_user",
            "trigger_distance_m", "perturbation_distance_m", "walk_duration_s"]])
print("Mean walk duration:", ends["walk_duration_s"].mean())
```

### Sleep mitigation health

```python
sleeps = df[df["event_type"] == "sleep_event"]
pulses = sleeps[sleeps["sleep_event_type"] == "pulse"]
print("Pulse count:", len(pulses))
print("Mean inter-pulse seconds:", pulses["time_since_last_pulse_s"].mean())

disconnects = sleeps[sleeps["sleep_event_type"].isin(["disconnect", "reconnect"])]
print("Disconnect/reconnect events:", len(disconnects))
print(disconnects[["timestamp_session", "sleep_event_type", "connected_L", "connected_R"]])
```

### Rigid body baseline + per-frame deviation distribution

```python
header = df[df["subtype"] == "rigid_body_baseline"]
if len(header):
    print("Baseline detail:", header.iloc[0]["detail"])

snap = df[(df["event_type"] == "state_snapshot")
          & df["deviation_from_baseline_m"].notna()].copy()
print(snap["deviation_from_baseline_m"].describe())
print(snap["deviation_from_baseline_deg"].describe())
snap.plot.scatter(x="timestamp_session", y="deviation_from_baseline_m",
                  title="Rigid-body deviation over session")
```

### Controller validity dropouts

```python
snap = df[df["event_type"] == "state_snapshot"].copy()
dropouts = snap[(snap["position_valid_L"] == 0) | (snap["position_valid_R"] == 0)]
print(f"{len(dropouts)} of {len(snap)} snapshots had a validity drop "
      f"({100 * len(dropouts) / max(1, len(snap)):.1f}%)")
```

---

## Dev session log (`session.log` + `session.json`)

Each session folder also contains a developer-side capture of the Unity
Console output and a metadata sidecar. These exist for triaging crashes
and tying CSV anomalies back to a specific build — they are not part of
the experiment analysis itself.

### `session.log`

A timestamped text capture of every Unity `Debug.Log*` call from the
launch — info, warning, error, exception with stack trace. Written by
`Assets/Scripts/QuestBuild/SessionLogger.cs`.

- Header lines (prefixed with `#`) describe the session: ID, build name +
  SHA, device, Unity version, UTC start.
- Body lines are `HH:mm:ss.fff [LogType] message` (UTC). Exception and
  Error entries also dump the stack trace on the next line(s).

### `session.json`

JSON sidecar with the build identity baked into the APK at build time +
lifecycle counters maintained by the logger:

| Field | Meaning |
|---|---|
| `sessionId` | Same as the folder name. |
| `apkBaseName` | The APK filename minus `.apk` — the build this session ran on. |
| `packageName` | Android application id. |
| `gitSha`, `gitBranch`, `dirty` | Repo state at the moment the APK was built. |
| `bundleVersion` | `Application.version` of the APK. |
| `buildTimestampUtc` | When the APK was built. |
| `sessionStartUtc` | UTC moment the logger initialised. |
| `sessionLastSeenUtc` | Heartbeat (~30s); approximates "still alive at" if the session ended without a clean quit. |
| `sessionEndUtc` | UTC of `Application.quitting` if it fired; empty string otherwise. |
| `cleanExit` | `true` only if `Application.quitting` fired (rare on Quest — usually false). |
| `durationSec` | End - start (or last-seen - start if no clean exit). |
| `unityVersion`, `deviceModel`, `osVersion` | Runtime environment. |
| `lineCount`, `warningCount`, `errorCount`, `exceptionCount` | Tallies over the whole session. |

### Triaging crashes

A session that died abruptly has `cleanExit:false` and `sessionEndUtc:""`. In that case:

1. Check `exceptionCount` and `errorCount` — non-zero means look at the
   tail of `session.log` for stack traces near `sessionLastSeenUtc`.
2. Cross-reference with the CSV's last `timestamp_session` row — if it's
   close to `sessionLastSeenUtc` (mapped via `session_start.detail.unix_ms`),
   the CSV stopped at roughly the moment the app died.

A clean quit leaves `cleanExit:true` (rare on Quest because the OS
suspends rather than quits) **or** the session simply ends when the user
puts the headset down. The latter looks the same as a crash in the
sidecar — the absence of exceptions in `session.log` is the actual
"clean-ish" signal.

---

## Joining build metadata to CSV analyses

Because `session.json` lives next to the experiment CSV, you can group
sessions by build identity to ask questions like "did this regression
appear after commit X?" or "do dirty builds drift more?".

```python
import json
from pathlib import Path
import pandas as pd

def load_session(folder: Path):
    """Return (csv DataFrame, sidecar dict) for one session folder."""
    sidecar = json.loads((folder / "session.json").read_text())
    csv_files = list(folder.glob("P*_*.csv"))   # participant CSV
    if not csv_files:
        return None, sidecar
    df = pd.read_csv(csv_files[0], low_memory=False)
    return df, sidecar

# Walk every session inside one APK's pulled folder
sessions_root = Path("C:/_G/Builds/QuestCameraKit/April Tag/Managed_Builds/"
                     "QVK-ControllerPair_0.1.0_2026-05-18_2117_59ca7a6-dirty_dev.sessions")
rows = []
for folder in sorted(sessions_root.iterdir()):
    if not folder.is_dir() or folder.name.startswith("_"):
        continue
    df, meta = load_session(folder)
    if df is None:
        continue
    rows.append({
        "session": folder.name,
        "gitSha": meta["gitSha"],
        "dirty": meta["dirty"],
        "bundleVersion": meta["bundleVersion"],
        "rows": len(df),
        "exceptions": meta["exceptionCount"],
        "errors": meta["errorCount"],
        "duration_s": meta["durationSec"],
    })

summary = pd.DataFrame(rows)
print(summary)
print("by SHA:\n",
      summary.groupby("gitSha")[["rows", "exceptions", "errors"]].sum())
```

### Mapping `session.log` timestamps to CSV `timestamp_session`

The CSV uses session-relative seconds (`timestamp_session`); `session.log`
uses UTC `HH:mm:ss.fff`. To align them:

1. From the CSV: `session_start_unix_ms = int(detail["unix_ms"])` (see
   "Parsing the session header" above).
2. From `session.json`: `session_start_utc = sidecar["sessionStartUtc"]`.
3. These should agree within a few ms — both record the same launch
   moment. Pick either as your t=0 reference.
4. For any log line at `HH:mm:ss.fff` (UTC), compute
   `(log_utc - session_start_utc).total_seconds()` to get the matching
   `timestamp_session`. Filter CSV rows by
   `(df["timestamp_session"] - X).abs() < window` to find what was
   happening in the experiment when that log line fired.

---

## Reference anchors (`reference_anchors.csv`) — dev only

**Why it's there.** Beyond the AprilTag-calibrated anchor and the controller-placer
anchor, the dev build can spawn an array of `OVRSpatialAnchor`s at configured
offsets from the AprilTag root (default: 1 center + 4 corners at ±2 m). Each is
tracked independently by Meta's runtime, so per-anchor bundle-adjustment events
show up as divergence between their pose traces — if all anchors jump together,
that's a uniform space re-localisation; if one anchor moves while the others
don't, that's a per-anchor adjustment.

Written by `Assets/_Scripts/Logging/ReferenceAnchorLogger.cs`.

**Schema.** `unix_ms, timestamp_session, frame, anchor_id, anchor_label,
pos_x, pos_y, pos_z, rot_x, rot_y, rot_z, rot_w, is_localized, tracking_state`.
One row per anchor per ~5 Hz sample (configurable).

**Typical analysis** (drift-uniformity probe):

```python
import pandas as pd
import matplotlib.pyplot as plt

df = pd.read_csv("reference_anchors.csv")
fig, axes = plt.subplots(3, 1, sharex=True, figsize=(10, 6))
for label, g in df.groupby("anchor_label"):
    g = g.sort_values("timestamp_session")
    for ax, col in zip(axes, ["pos_x", "pos_y", "pos_z"]):
        ax.plot(g["timestamp_session"], g[col], label=label)
for ax, col in zip(axes, ["pos_x", "pos_y", "pos_z"]):
    ax.set_ylabel(col); ax.legend(fontsize="x-small")
axes[-1].set_xlabel("timestamp_session (s)")
plt.suptitle("Reference anchor poses over session")
```

Synchronised jumps across all labels → uniform re-localisation. Independent
jumps → per-anchor bundle adjustment.

---

## Tracking events (`tracking_events.csv`) — dev only

**Why it's there.** Drift spikes and CSV anomalies often line up with SLAM
tracking transitions (boundary cross, headset lift, controller drop). This
file emits a row only when state changes, so it's tiny and easy to overlay
on the main CSV's time axis.

Written by `Assets/_Scripts/Logging/TrackingEventsLogger.cs`.

**Schema.** `unix_ms, timestamp_session, frame, event, state_from, state_to,
detail`.

Events emitted:

| `event`                          | Meaning |
|----------------------------------|---------|
| `tracking_baseline`              | First-frame snapshot — `detail` carries the initial state of every tracked flag. |
| `head_tracking_lost` / `_recovered` | `OVRPlugin.GetNodePositionTracked(Head)` flipped (stricter than `*Valid` - excludes dead-reckoned poses). |
| `controller_L_pose_invalid` / `_valid` | `OVRInput.GetControllerPositionValid(LTouch)` flipped. |
| `controller_R_pose_invalid` / `_valid` | Same, right hand. |
| `controller_L_connected` / `_disconnected` | `OVRInput.IsControllerConnected(LTouch)` flipped. |
| `controller_R_connected` / `_disconnected` | Same, right hand. |
| `user_present` / `user_absent`   | `OVRPlugin.userPresent` flipped (headset on/off head). |

**Typical use.** Merge into the experiment CSV by nearest `timestamp_session`
and annotate drift spikes that fall within a few hundred ms of a
`*_tracking_lost` event as "probably real, not an algorithmic failure":

```python
events = pd.read_csv("tracking_events.csv").sort_values("timestamp_session")
df = df.sort_values("timestamp_session")
df["nearest_tracking_event"] = pd.merge_asof(
    df[["timestamp_session"]], events[["timestamp_session", "event"]],
    on="timestamp_session", direction="nearest", tolerance=0.5)["event"]
```

---

## High-frequency pose + jitter stats — dev only

Four files, all gated to development builds:

- `headset_poses.csv` — per-frame headset world pose + velocity.
- `headset_pose_stats.csv` — 1 Hz rolling RMS jitter (window default 1 s).
- `controller_poses.csv` — per-frame L+R poses + validity.
- `controller_pose_stats.csv` — 1 Hz rolling RMS jitter per controller.

Written by `Assets/_Scripts/Logging/HeadsetPoseLogger.cs` and
`ControllerPoseLogger.cs`. Both share the `unix_ms, timestamp_session, frame`
prefix and use invariant-culture floats in "R" format.

### Schemas

```
headset_poses.csv
  unix_ms, timestamp_session, frame,
  pos_x, pos_y, pos_z, rot_x, rot_y, rot_z, rot_w,
  linvel_mps, angvel_dps

headset_pose_stats.csv
  unix_ms, timestamp_session, window_sec, sample_count,
  pos_jitter_rms_mm, rot_jitter_rms_deg

controller_poses.csv
  unix_ms, timestamp_session, frame,
  side,                          (L | R)
  pos_x, pos_y, pos_z, rot_x, rot_y, rot_z, rot_w,
  position_valid, orientation_valid,
  linvel_mps, angvel_dps

controller_pose_stats.csv
  unix_ms, timestamp_session, side, window_sec, sample_count,
  pos_jitter_rms_mm, rot_jitter_rms_deg
```

`linvel_mps` and `angvel_dps` are frame-to-frame deltas (position distance /
`Time.unscaledDeltaTime`; `Quaternion.Angle` / `dt`). The `*_jitter_rms_*`
columns are RMS of consecutive deltas over the window — high-frequency jitter,
not drift.

### Typical analysis

Quick at-a-glance controller jitter timeline (uses the cheap stats stream):

```python
import pandas as pd
import matplotlib.pyplot as plt

stats = pd.read_csv("controller_pose_stats.csv")
for side, g in stats.groupby("side"):
    plt.plot(g["timestamp_session"], g["pos_jitter_rms_mm"],
             label=f"controller {side} pos RMS (mm)")
plt.legend(); plt.xlabel("timestamp_session (s)"); plt.ylabel("RMS mm")
plt.title("Controller positional jitter (1 s window)")
```

Full offline analysis straight off the raw stream (any window, any metric):

```python
poses = pd.read_csv("controller_poses.csv")
L = poses[poses["side"] == "L"].sort_values("timestamp_session").reset_index(drop=True)
L["d_pos_mm"] = (L[["pos_x","pos_y","pos_z"]].diff().pow(2)
                 .sum(axis=1).pow(0.5) * 1000)
print(L["d_pos_mm"].describe())
```

### Production gating

All six dev-only files (`reference_anchors.csv`, `tracking_events.csv`, the
four high-freq pose / stats files) are **absent** from `Sessions/<sessionId>/`
in non-development builds — their loggers self-gate on `Debug.isDebugBuild`.
Their presence is the indicator of whether high-freq capture was on for that
session.

---

## Known data caveats

- **`position_valid_L` / `position_valid_R` are unreliable as "is the controller actually being optically seen"**. `OVRInput.GetControllerPositionValid` returns `true` even when the runtime is extrapolating from a stale fix (IMU dead-reckoning). For drift-correction decisions, cross-check against `velocity_*_mps` from the same row (sudden large velocities while a controller should be static is the dead-reckoning signature).
- **Sources go silent when out of their working range**. `controller_placer` is the obvious one (only emits while a lock is held), but in Phase 2 other sources will also gate themselves. Absence of rows is not failure — it's "nothing meaningful to report." Phase 2 will add `source_state_change` rows on the transitions; current data has no such transitions.
- **Battery is sampled once per minute**, not per snapshot. `battery_L/R_percent` on a `state_snapshot` is the most recent sample, not necessarily this-frame.
- **The session header is in a `detail` blob**, not as separate columns. Parse it once.
- **`schema_version=1` is current**. If you see `schema_version >= 2` in a future file, the column layout may differ; refer to the schema changelog in `SessionLoggerSchema.md` next to this file.
- **All times are session-relative** (`timestamp_session`), not wall-clock. The session_start row's `unix_ms` field is the only wall-clock anchor — use it once to map to wall-clock time.
- **Two anchor systems can be live at once** and they don't share state. Don't merge `anchor_baseline` and `controller_placer` rows by averaging — they're two independent estimators.

---

## Reserved (defined but not yet emitted in current data)

If you grep the CSV and don't see these, that's expected — they're stubbed
to keep the schema stable for future code:

- Event types: `correction_event`, `source_state_change`, `snap_event`, `validation_walk`.
- Correction sources: `controller`, `apriltag`, `optitrack`.
- Columns: `accepted`, `rejection_reason`, `delta_position_m`, `delta_rotation_deg`, `ema_alpha_applied`, `correction_applied_m`, `controller_distance_m`, `controller_velocity_mps`, `context_for`. These will populate when the Phase 2 `ControllerDriftCorrector` lands.
- Walk-event "cumulative correction" columns (`corrections_applied_count`, `max_correction_magnitude_m`, `rejection_reason_histogram`) — also Phase 2.

If a future log fills these in, the same analyses above will Just Work for
them.

---

## Glossary

- **SLAM** — Simultaneous Localization And Mapping. The Quest's algorithm for tracking the headset (and controllers) against a map of the surrounding environment. SLAM drift is the cumulative position error as the runtime updates its map.
- **`OVRSpatialAnchor`** — Meta's Unity component that asks the OS to track a specific world-pose against drift. Two of them in this project — one for the AprilTag constellation, one transient for the placer lock.
- **Anchor baseline** — In this project's vocabulary, the uncorrected AprilTag-anchor's world pose; the "ground truth without correction." Logged as `correction_source=anchor_baseline`.
- **`CorrectionRoot`** — A child Transform under the AprilTag `OVRSpatialAnchor`. The AprilTag pipeline writes its rigid corrections to this Transform's localPose; the obstacle is a further child so it inherits the corrected pose.
- **Constellation** — The set of AprilTags on the calibration board. RANSAC + Kabsch on the constellation produces the AprilTag system's correction.
- **AprilTag** — A 2D fiducial marker system (think QR-code-but-simpler). The Quest's stereo cameras detect them; their known geometry lets the app solve for pose.
- **Gait mat** — A physical mat the participant walks across. The virtual obstacle is anchored to a location on the mat.
- **Walk / Trial** — One traversal of the gait mat. Each walk is one trial with a specific `TrialCondition` (active, perturbation direction, trigger distance, perturbation distance). 50–100 walks per session.
- **Perceptual offset** — A small per-participant tweak of the obstacle's position to align it visually with the gait mat. Captured once at session start. (Logged in the session header `notes` field if the experimenter wrote it down.)
- **Finesse** — The `ObstacleFinesseController` flow that lets the experimenter nudge the obstacle's local pose via the controllers. Not directly visible in the log (it edits scene transforms; what shows up is the resulting obstacle pose).
- **Phase 1 / Phase 2 / Phase 3** — Project milestones. Phase 1 (logger + controller pose sensing) is shipped — that's what's in the data. Phase 2 (gates, EMA, snap detection, calibration UX) is in progress. Phase 3 (walk-boundary refinement, periodic re-validation, threshold tuning) is later.

---

## When in doubt

- Filter by `event_type` and `correction_source` first — those two columns plus `subtype` give you the structure of the data.
- Use `timestamp_session` as the x-axis; map to wall clock via the session header once.
- Vector3 and Quaternion fields are pipe-separated in a single cell — write a small parser and reuse it everywhere.
- If a column you expect is empty, it's almost certainly intentional (sparse schema) — check the relevant section above before assuming missing data.
- Schema is version **1**. If `schema_version` differs in your file, check the `SessionLoggerSchema.md` changelog next to this document for what changed.
