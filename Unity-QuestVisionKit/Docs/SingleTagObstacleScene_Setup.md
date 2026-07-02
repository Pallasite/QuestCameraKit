# Single/Double-Tag Obstacle Scene — Assembly & Wiring Guide

This is the stepped-back, simplified experimental flow (branch `SingleTagObstacle`).
The C# is written; this guide is for **assembling the new scene in the Unity Editor**
(the MCP tooling isn't reachable from the agent shell, so scene wiring is manual).

Create a **new scene of its own lineage** — do NOT fork the constellation scene:
`Assets/_Scenes/Single Tag Obstacle v1.unity`.

---

## Runtime transform chain (what the controller builds for you)

`ObstaclePlacementController` creates this at runtime — you do **not** author it:

```
ObstacleAnchorRoot   (OVRSpatialAnchor in the Anchored variant; plain root otherwise)
  └── TagOffset       (solver-proposed pose, written per policy)
      └── FinesseOffset  (experimenter nudge — baked-in offset)
          └── Obstacle    (your obstaclePrefab; PerturbationPivot + visual go below it)
```

You only supply the **obstacle prefab** (visual mesh + any `ObstacleVisualCycler`).

---

## Scene contents (GameObjects + components)

### 1. MR rig (from Meta Building Blocks)
- **`[BuildingBlock] Camera Rig`** — `OVRCameraRig` + `OVRManager`.
  - Confirm `OVRManager` → **Anchor Support = Enabled** (project already has
    `anchorSupport: 1` in `OculusProjectConfig`).
- **`[BuildingBlock] Passthrough`** — `OVRPassthroughLayer`.
- **`[BuildingBlock] Occlusion Dependencies`** — `EnvironmentDepthManager` (optional).
- **Camera Permissions** — `CameraPermissionRequester`.
- **`XRDisplayConfigurator`** — 90 Hz pinning + CPU/GPU levels (carry from old scene).

### 2. AprilTag detection pipeline (one GameObject, e.g. "April Tags")
- `StereoAprilTagScanner`
- `AprilTagDisplayManager`  ← **assign its `markerPool`** (see gotcha below)
- `AprilTagWireframeVisualizer` (optional, live detection viz)
- 2× `PassthroughCameraAccess` (Left + Right)
- `MarkerPool` (can be on this GO or its own; assign as a singleton)

> **GOTCHA (the May-21 root cause):** if `AprilTagDisplayManager.markerPool` is
> unassigned and no `MarkerPool` singleton exists, you get
> `[AprilTagDisplayManager] No MarkerPool assigned…` and **no detections flow**.
> Wire the MarkerPool explicitly.

> Leave `AprilTagDisplayManager.proximityGate` **empty** — that field gates scans
> by distance to a `ConstellationDriftCorrector`, which this scene doesn't use.
> Empty = always scan (correct here).

### 3. Placement
- **`ObstaclePlacementController`** — assign:
  - `displayManager` → the AprilTagDisplayManager above
  - `obstaclePrefab` → your obstacle prefab
  - `obstacleController` → the ObstacleController below
  - `finesseController` → the ObstacleFinesseController below
  - **Variant fields** (the experiment knobs):
    - `trackingVariant`: `Anchored` (best) or `WorldRoot` (SLAM-only backup)
    - `visualPolicy`: `Deferred` (default), `SmoothedLive`, or `RawLive`
    - `solverMode`: `SingleTag` (start here) or `TwoTagLine`
    - `singleTagId`: the tag ID at the obstacle (or `-1` = nearest)
    - two-tag: `twoTagIdA` / `twoTagIdB` / `twoTagVerticalOffsetMeters` / `twoTagRotationOffsetEuler`

### 4. Trial loop
- **`TrialLoader`** (loads `trial_conditions.csv`; runtime push or StreamingAssets template)
- **`TrialSequencer`** → assign `obstacleController`
- **`ObstacleController`** → leave `corrector` **empty**; its `manualTarget` is set
  at runtime by the placement controller. Tune `resetDistance` / `timeBuffer`.
- **`TrialLoopActivator`** → assign `obstacleController`, `trialSequencer`, and
  (recommended) `placement`. This is the piece that actually **arms** the loop —
  without it, trials load but never perturb/reset/advance (the field bug).
- **`SessionLoggerTrialSubscriber`** → assign `trialSequencer` + `obstacleController`
- **`TrialDiagnosticsHUD`** → assign `obstacleController` (+ TMP label)

### 5. Input + finesse
- **`QuestControllerInput`**
- **`ObstacleFinesseController`** → assign `input`; leave `corrector`/`placer`/
  `driftCorrector` empty. Its `manualTarget` is pointed at `FinesseOffset` at
  runtime by the placement controller. (Calibration chords that need a
  `ConstellationDriftCorrector` are inert here — harmless.)

### 6. Logging
- **`SessionLogger`** → set `participantId`; the per-session folder + CSV are automatic.

---

## How a session runs

1. App starts; AprilTag pipeline begins detecting.
2. Experimenter looks at the tag from < 1 m. Once the pose is stable, the obstacle
   is **placed** (anchor created in the Anchored variant) and becomes visible.
   `TrialLoopActivator` arms the loop on placement.
3. Finesse-nudge the obstacle to taste (the baked-in offset layer).
4. Participant walks; trials perturb / reset / advance automatically.
5. In `Deferred` policy the obstacle holds still during each walk; the tag-measured
   correction is applied only between trials. (`SmoothedLive` / `RawLive` move it live.)
6. Pull the session CSV; see `SingleTagObstacleHandoff.md` for analysis.

---

## World-lock finding (plan step 1)

`anchorSupport` is already enabled. The obstacle's high-performance world-lock **is**
the `OVRSpatialAnchor` created in the `Anchored` variant — no extra building block or
OVR setting is needed. The `WorldRoot` variant (no anchor; relies on the inherently
world-locked tracking space) is the SLAM-only **backup** to A/B against. There is no
other static content in this passthrough scene that needs locking; if you later add
fixed reference objects, parent them under a single shared `OVRSpatialAnchor` rather
than anchoring each one.
