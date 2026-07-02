# Single/Double-Tag Obstacle — Analysis Handoff

Scene-specific companion to `LogAnalysisHandoff.md` + `SessionLoggerSchema.md` for
the simplified single/double-AprilTag obstacle flow (branch `SingleTagObstacle`,
component `ObstaclePlacementController`).

## The question this scene answers

Does a single (or double) AprilTag, used to place a **world/anchor-locked** obstacle,
hold the obstacle's apparent position with **stable, bounded error** across a session?
(Not zero drift — bounded drift is the realistic target.)

## What's logged (all `schema_version=1`, no new columns)

`correction_source` = the active solver's label: `apriltag_single` or `apriltag_pair`.

Per measurement tick (default 30 Hz), two `state_snapshot` rows, split by `mode`:

| `mode` | `anchor_pos_xyz` / `anchor_rot_xyzw` means |
|---|---|
| `observe` | the **tag-proposed** obstacle base pose this frame (live tag) |
| `applied` | the obstacle base's **actual** anchor/world-locked pose (what the participant sees) |

`headset_pos_xyz`/`headset_rot_xyzw` populated on both.

Event markers:
- `session_event subtype=obstacle_placed` (once): `detail` =
  `solver=...;variant=Anchored|WorldRoot;policy=Deferred|SmoothedLive|RawLive;pos=x|y|z`.
- `correction_event` (`mode=applied`, `accepted=1`): in `Deferred` policy, one per
  between-trial correction (on obstacle reset, after the participant passes).
  `delta_position_m` / `delta_rotation_deg` / `correction_applied_m` = magnitude moved.

Trial markers are unchanged (`walk_event` phases `start`/`moved`/`reset`/`end` from
`SessionLoggerTrialSubscriber`).

## Headline analysis: bounded error

The `applied` stream is what the participant experiences; the `observe` stream is the
live tag's opinion. Their divergence over time is the drift the world-lock is absorbing.

```python
import pandas as pd
df = pd.read_csv(path, low_memory=False, encoding="utf-8-sig")  # utf-8-sig strips the BOM

def vec3(c):
    if pd.isna(c) or c == "": return (None, None, None)
    x, y, z = c.split("|"); return float(x), float(y), float(z)

snap = df[df.event_type == "state_snapshot"]
src  = snap[snap.correction_source.isin(["apriltag_single", "apriltag_pair"])].copy()

applied = src[src["mode"] == "applied"].copy()
observe = src[src["mode"] == "observe"].copy()
for d in (applied, observe):
    d[["x","y","z"]] = d.anchor_pos_xyz.apply(vec3).apply(pd.Series)

# 1) Does the *applied* (what they see) obstacle hold bounded error vs its placed origin?
o = applied.iloc[0][["x","y","z"]].astype(float)
applied["err_mm"] = ((applied[["x","y","z"]].astype(float) - o)**2).sum(axis=1)**0.5 * 1000
print(applied["err_mm"].describe())   # max/95th percentile = the bounded-error number

# 2) How big is the live tag vs applied divergence (the correction the lock absorbs)?
m = pd.merge_asof(observe.sort_values("timestamp_session"),
                  applied.sort_values("timestamp_session"),
                  on="timestamp_session", suffixes=("_obs","_app"), direction="nearest")
m["div_mm"] = (((m.x_obs-m.x_app)**2 + (m.y_obs-m.y_app)**2 + (m.z_obs-m.z_app)**2)**0.5)*1000
print(m["div_mm"].describe())
```

## Per-trial corrections (Deferred policy)

```python
corr = df[(df.event_type == "correction_event") &
          df.correction_source.isin(["apriltag_single","apriltag_pair"])]
print((corr["delta_position_m"].astype(float)*1000).describe())  # mm applied between trials
```

A correction magnitude that **shrinks then plateaus** over the session is the
bounded-error signature. Growing magnitudes = the lock isn't keeping up.

## Compare variants / policies across sessions

`variant` and `policy` are in the `obstacle_placed` row's `detail`. Group sessions by
those to compare `Anchored` vs `WorldRoot` and `Deferred` vs `SmoothedLive`/`RawLive`
on the bounded-error metric above.
