# SessionLogger CSV Schema

Single wide-and-sparse CSV per session, written by `SessionLogger.cs` to
`Application.persistentDataPath/<participantId>_<unixMs>.csv`.

Schema version: **1** (see `LogEvent.CurrentSchemaVersion`). Bump on every change.
Keep the changelog at the bottom of this file in sync.

## Column contract

Required on every row:

| Column | Type | Notes |
|---|---|---|
| `schema_version` | int | Matches `LogEvent.CurrentSchemaVersion` at write time. |
| `timestamp_session` | double, seconds | `Time.realtimeSinceStartupAsDouble` minus session start. |
| `frame_number` | int | `Time.frameCount` at construction. |
| `event_type` | string | See *Event types* below. |
| `correction_source` | string | `anchor_baseline`, `controller`, `apriltag`, `optitrack`, or `system`. |
| `mode` | string | `applied`, `observe`, or `n/a`. |

Sparse columns (only populated for relevant event types):

| Column | Type | Populated by |
|---|---|---|
| `subtype` | string | `session_event`, `source_state_change` |
| `detail` | string | `session_event`, `source_state_change` |
| `anchor_pos_xyz` | `x\|y\|z` | `state_snapshot`, `snap_event`, `validation_walk` |
| `anchor_rot_xyzw` | `x\|y\|z\|w` | same |
| `headset_pos_xyz` | `x\|y\|z` | same |
| `headset_rot_xyzw` | `x\|y\|z\|w` | same |
| `controller_L_pos_xyz` | `x\|y\|z` | same — empty when controller out of working range |
| `controller_L_rot_xyzw` | `x\|y\|z\|w` | same |
| `controller_R_pos_xyz` | `x\|y\|z` | same |
| `controller_R_rot_xyzw` | `x\|y\|z\|w` | same |
| `position_valid_L` | 0/1 | `state_snapshot` |
| `position_valid_R` | 0/1 | `state_snapshot` |
| `orientation_valid_L` | 0/1 | `state_snapshot` |
| `orientation_valid_R` | 0/1 | `state_snapshot` |
| `connected_L` | 0/1 | `state_snapshot`, `sleep_event` |
| `connected_R` | 0/1 | `state_snapshot`, `sleep_event` |
| `velocity_L_mps` | float | `state_snapshot` — self-computed from pose-delta |
| `velocity_R_mps` | float | `state_snapshot` — self-computed from pose-delta |
| `battery_L_percent` | float (0-100) | `state_snapshot`, `sleep_event` |
| `battery_R_percent` | float (0-100) | same |
| `inter_controller_distance_m` | float | `state_snapshot`, `calibration_event` |
| `inter_controller_rotation_deg` | float | same |
| `deviation_from_baseline_m` | float | `state_snapshot`, `validation_walk` |
| `deviation_from_baseline_deg` | float | same |
| `validation_enforced` | 0/1 | `state_snapshot`, `correction_event` |
| `sleep_event_type` | string | `sleep_event` — `pulse`, `disconnect`, `reconnect`, `battery_sample` |
| `time_since_last_pulse_s` | float | `sleep_event` |
| `walk_index` | int | `walk_event`, `validation_walk` |
| `walk_phase` | string | `walk_event` — `start`, `moved`, `reset`, `end` |
| `trial_active` | 0/1 | `walk_event` |
| `move_towards_user` | 0/1 | `walk_event` |
| `trigger_distance_m` | float | `walk_event` |
| `perturbation_distance_m` | float | `walk_event` |
| `walk_duration_s` | float | `walk_event` (phase=end) |
| `corrections_applied_count` | int | `walk_event` (phase=end) — Phase 1: always 0 |
| `max_correction_magnitude_m` | float | `walk_event` (phase=end) — Phase 1: empty |
| `rejection_reason_histogram` | JSON string | `walk_event` (phase=end) — Phase 1: empty |
| `calibration_step` | string | `calibration_event` |
| `calibration_sample_index` | int | `calibration_event` (per-sample rows) |
| `mean_distance_m` | float | `calibration_event` (summary row) |
| `stddev_distance_m` | float | same |
| `mean_rot_deg` | float | same |
| `stddev_rot_deg` | float | same |
| `accepted` | 0/1 | `correction_event` |
| `rejection_reason` | string | `correction_event` |
| `delta_position_m` | float | `correction_event`, `snap_event` |
| `delta_rotation_deg` | float | `correction_event`, `snap_event` |
| `ema_alpha_applied` | float | `correction_event` |
| `correction_applied_m` | float | `correction_event` |
| `controller_distance_m` | float | `correction_event` |
| `controller_velocity_mps` | float | `correction_event` |
| `context_for` | string | `correction_event` rows that belong to a ring-buffer dump — refs the snap_event id |

### Vector / quaternion encoding

To keep column count manageable, Vector3 and Quaternion fields are packed into a
single cell separated by `|`:
- `Vector3` → `"x|y|z"` (three floats, R-format)
- `Quaternion` → `"x|y|z|w"` (four floats, R-format)

This avoids the CSV column explosion and means readers can `split('|')` to parse.
Empty cell = field not populated for this row.

### Float / int / bool encoding

- Floats: invariant-culture, "R" round-trip format (full precision)
- Ints: invariant-culture
- Bools: `0` / `1`
- Missing values: empty cell

CSV-special characters (`,`, `"`, `\n`, `\r`) in string fields trigger quoted-cell
encoding with `"` doubled per RFC 4180.

## Event types

| `event_type` | Cadence | Purpose |
|---|---|---|
| `session_event` | sparse | `session_start`, `session_end`, `application_quit`, config dumps |
| `state_snapshot` | ~5 Hz | Periodic pose + state capture |
| `correction_event` | on state change or applied correction (Phase 2) | Gate decisions, applied corrections, snaps |
| `source_state_change` | on working range transition (Phase 2) | Source entering/leaving active |
| `sleep_event` | sparse | Pulse sent, disconnect, reconnect, battery sample |
| `calibration_event` | per sample during cal + 1 summary | Calibration samples + summary |
| `walk_event` | 2-4 per walk | `start`, optionally `moved` / `reset`, `end` (with cumulative stats) |
| `validation_walk` | every N walks (Phase 3) | Periodic re-validation markers |
| `snap_event` | rare (Phase 2) | High-resolution context dump trigger |

### Working-range gating

Sources gate their own emission. When a source is out of range it emits a single
`source_state_change` row marking the transition and then goes silent until the
next transition. This keeps the log honest about what was observed vs. silenced.

## Session header

The first `session_event` row (`subtype=session_start`) has a `detail` payload
of `key=value;key=value;...` pairs:

- `build` — `Application.version`
- `scene` — active scene name
- `participant` — participant ID
- `unix_ms` — wall-clock session start
- `schema_version` — schema version active for this file
- `flush_interval_s` — writer flush cadence
- `notes` — free-form (from `sessionHeaderNotes` field)

When subsystem-specific config dumps happen (e.g. rigid-body baseline values,
correction thresholds), they go in additional `session_event` rows with their
own subtypes (e.g. `subtype=rigid_body_baseline_captured`,
`subtype=correction_config`).

## ADB pull workflow (Quest)

```
adb shell pm list packages | grep -i quest    # find the package name
adb shell run-as <package> ls files/           # list files in scoped storage
adb pull /sdcard/Android/data/<package>/files/<filename>.csv ./
```

Or directly via the Application's persistent data path:
```
adb pull /sdcard/Android/data/com.YourCompany.QuestVisionKit/files/ ./session_logs/
```

On the Quest, sessions land in `Application.persistentDataPath` which maps to
`/sdcard/Android/data/<package>/files/` for the running app.

## Changelog

- **v1** (initial) — Schema established for Phase 1. Emits `session_event`,
  `state_snapshot`, `sleep_event`, `calibration_event`, `walk_event`. Phase 2
  factories defined but not yet used: `correction_event`,
  `source_state_change`, `validation_walk`, `snap_event`.
