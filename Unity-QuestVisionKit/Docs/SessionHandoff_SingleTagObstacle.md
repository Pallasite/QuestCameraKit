# Session Handoff — SingleTagObstacle branch

Self-contained handoff for any machine / fresh Claude session. Written 2026-07-02.
This is the portable copy of the session handoff (the original lived in a
machine-local plan file on the primary dev machine).

Project: Unity 6 (`6000.3.11f1`) Quest 3 MR gait experiment (UW-Madison
kinesiology). Goal metric: the virtual obstacle holds **stable, bounded error**
over a 45-90 min session (not a hard <1 cm guarantee).

---

## UPDATE 2026-07-14 — Field-test fix pass (user feedback, 10 items)

First real field test (grad-student experimenter) surfaced 10 issues; all
diagnosed and fixed this pass. Full diagnosis + decisions live in the commits;
headlines:

- **Perturbation axis (the big one):** obstacle perturbed vertically — up
  toward the eyes or DOWN through the floor, where env-depth occlusion
  swallowed it whole (that was the "obstacle disappears when stepping over").
  Cause: perturb axis = local +Z inheriting the raw tag rotation + a
  world-Z-vs-local-forward sign bug. Fixed: `SingleTagSolver` yaw-flattens
  (convention: **tag flat on floor, printed top = walking direction**; wall
  tag = face normal), `DefaultObstacleBehavior.Move` projects horizontal and
  signs toward/away on the same axis.
- **Two-color obstacle:** commit b4410c2 had repointed `No Occlusion Lit
  Green.mat` at the occluding shadergraph (different `_Base_Color`) — so the
  1 m OcclusionSwapper flip changed color AND occlusion was permanently on.
  Fixed: far mat is a same-color twin with `_EnvironmentDepthBias=1`
  (bias≥1 fully defeats the depth test per Meta's cginc — no shadergraph
  edit needed); near mat bias 0.06 so the floor stops eating the bottom
  edge. New `OcclusionPhasePolicy`: non-occluding during Setup/Ready
  (placement legibility), distance swap during walks.
- **Frame judder (54-59 fps @40 Hz scanning, worse with extra tags in
  view):** new `ScanProfilePolicy` — Setup/Ready = quality (full-res
  sampleFactor 1, 20 Hz); trials = minimal (8 Hz, sampleFactor 2, plus a
  **last-seen-tag distance gate**: scanner idles beyond tagSize×15 (~2.6 m),
  DYNAMIC with tag size). `targetTagIds` whitelist on both scanners drops
  non-experiment tags before triangulation (empty by default — set it once
  the lab tag ID is known). Marker cubes now hide after placement
  (`MarkerDisplayMode.DuringPlacementSetup`). Off-main-thread detection is
  the next lever if a locked 72 fps still isn't reached.
- **Trial navigation:** `SessionFlowController.NextTrial()/PreviousTrial()`
  (clearance-guarded like redo, `trial_skip` logged); web console Prev/Next
  buttons; new chord R-grip+R-index = next trial. Redo was never
  once-limited — the clearance guard just read as dead; it now holds a
  longer HUD message and pulses on re-arm.
- **Web console ON by default** in both scenes (port 8787; `adb forward
  tcp:8787 tcp:8787`); serves trial #/phase live + occlusion & scan-profile
  A/B toggles for the device pass.
- **Presets now A/B/C** (Deferred / SmoothedLive "glides back" / RawLive
  diagnostic) so R-grip+Start actually cycles.
- **Setup gate tooltips** (`ITagPlacementSolver.GateStatus` → HUD: "Too far —
  step within 1.0 m (now 1.6 m)", "Moving too fast", "Capturing 6/10") and
  **bigger HUD text** (diagnostics 13→20pt).
- CSV template gained a `#` comment header documenting the 5 columns (the
  tester had columns 2/3 swapped in their mental model); OperatorQuickstart
  covers tag mounting, CSV, web console, redo semantics, session files.

**Editor-verified:** compile clean; Move()/FlattenToYaw unit-driven via MCP
Roslyn (flat/tilted/yawed tag, both player sides). **Device pass still
needed**, checklist in the plan: perturb horizontal both mountings, single
green + no step-over vanish, locked-72 fps A/B via cycleScanProfile, web
next/prev under clearance guard, gate tooltips, HUD legibility, A/B/C cycle.

---

## UPDATE 2026-07-06 — UX pass complete (autonomous run)

The experimenter-experience pass shipped on top of everything below. Headlines:

- **`SessionFlowController`** (`Assets/_Scripts/Experiment/`) — phase machine
  Setup→Ready→Running→Paused→Complete; owns trial-loop arming exclusively
  (**`TrialLoopActivator` is [Obsolete] and removed from both scenes** — its
  arm-on-placement behavior was a hazard). Explicit Start-trials hold; redo is
  pause-safe and clearance-guarded; sequence-complete gated to Running/Paused
  (1-based trial CSVs can no longer brick boot).
- **`ExperimenterSessionControls` rewritten** — phase-gated **hold-to-confirm**
  (0.9 s, chord grouping, escalating haptics, HUD progress). Bindings in
  `Docs/OperatorQuickstart.md` (written for the grad-student operator).
- **`SessionHUD`** (multi-zone, in the reworked
  `PipelineStatusHUD (SingleTag).prefab`, both scenes) — status bar / per-phase
  guidance with live capture progress / transients / toggleable diagnostics;
  lazy-follow; hides during Running (participant wears the headset). Replaces
  the old HUD that showed dead constellation instructions.
- **Ghost preview** — translucent palette-cyan obstacle at the tag-proposed
  pose during the place-hold (`Assets/Materials/GhostPreview.mat`).
- **Palette** — `ExperimentPalette`: cyan=good / magenta=bad everywhere
  (HUD, wireframe gradients). Wireframe now hides during walks
  (`DuringPlacementSetup` mode) unless diagnostics are on.
- **Logging** — new session-flow events + fixes, see `SessionLoggerSchema.md`
  changelog (still schema v1, additive): `phase_change`, `config_change`
  (A/B attribution), `trial_redo`, `application_pause/resume` (with forced
  flush — the missing-session_end fix), `participant_source`,
  `stale_proposal` rejections. **Fixed a latent walk-end row race** that could
  stamp end rows with the next trial's index and ~0 duration.
- **Presets** — `ObstaclePlacementController.presets` ships `[A:
  Single/Deferred/Anchored]`; add more in the Inspector for A/B sessions.
- **participant.txt** — adb-push next to trial_conditions.csv to set the
  participant ID per session (falls back to the Inspector value).

**Editor-verified end-to-end in playmode** (synthetic tag injection + rig-root
walking): placement → Ready → StartTrials → trigger → perturb → auto-reset →
deferred correction (exact magnitude) → advance; pause/redo stay disarmed; CSV
row ordering asserted. **Still needs the device pass**: hold ergonomics +
haptic feel, real-tag placement + ghost quality, Scratch passthrough, anchor
behavior under real SLAM, HUD legibility/lazy-follow comfort. Known-benign:
`PassthroughCameraAccess` NREs in editor playmode (no camera hardware).

---

## Where things stand

**Branch `SingleTagObstacle`** (off `ControllerCorrections`) — a deliberate
scale-back from the constellation/controller-correction "secret sauce" to a
simple single/double-AprilTag placement flow. All work below is committed in
`437868f` and pushed to origin.

### What was built (all compile clean; verified over the live Unity MCP)

1. **Placement system** — `Assets/_Scripts/Placement/`:
   - `PlacementEnums.cs` — `TrackingVariant` (Anchored/WorldRoot),
     `VisualUpdatePolicy` (Deferred/SmoothedLive/RawLive),
     `TagSolverMode` (SingleTag/TwoTagLine/Constellation),
     `PlacementTrigger` (Manual/AutoOnFirstStable).
   - `ITagPlacementSolver` + `SingleTagSolver` (distance+stability gated),
     `TwoTagLineSolver` (obstacle at the midpoint of two tags, perpendicular
     yaw), `ConstellationSolver` (stub for later).
   - `ObstaclePlacementController` — builds the runtime chain
     `ObstacleAnchorRoot (OVRSpatialAnchor if Anchored) -> TagOffset ->
     FinesseOffset -> Obstacle`; Manual "place now" default; runtime
     cycle/set methods + events for every config axis (web-bridge-ready).
2. **Control layer** — `Assets/_Scripts/Experiment/`:
   - `ExperimenterSessionControls.cs` — in-headset chords that only call public
     APIs. **Current bindings (2026-07-14; the original list here was stale):**
     HOLD L index = Place, HOLD R-grip + L index = Recapture, HOLD both index =
     Start trials, HOLD R index = Redo, HOLD R-grip + R index = Next trial,
     HOLD R-grip + Start = Cycle preset, PRESS Start = Pause/Resume, PRESS
     R-stick = diagnostics. Finesse owns sticks/grips/A/B. Previous-trial is
     web-console only.
   - `TrialLoopActivator.cs` — **arms the trial loop. Nothing else in the
     project ever set IsArmed/AutoReset/TrialSequenceActive — that is why walks
     never completed in past field sessions.** Also Pause()/Resume().
   - `TrialSequencer.RedoCurrentTrial()`, `ObstacleController.ResetForRedo()`
     (reset + re-arm WITHOUT firing OnObstacleReset — no correction from a
     fouled walk, no advance), `SetManualTarget()` hooks on ObstacleController
     and ObstacleFinesseController.
3. **Two scenes** (both wired identically: `Root Obstacle.prefab`,
   placementMode=Manual, policy=Deferred, solver=SingleTag id=-1, variant=Anchored):
   - `Assets/_Scenes/Single Tag Obstacle - Lifted.unity` — clone of the
     constellation scene; `April Tags Logic and Managers` **enabled** (it was
     disabled — the root cause of zero tag detections in the field);
     `ConstellationDriftCorrector` + the 6 controller-correction objects
     stripped (kept `SessionLogger`); dangling refs cleared; placement +
     control layer added and wired.
   - `Assets/_Scenes/Single Tag Obstacle - Scratch.unity` — from scratch:
     canonical `OVRCameraRig.prefab`, fresh `OVRPassthroughLayer`, OVRManager
     passthrough=true / FloorLevel; detection + HUD instantiated from two new
     prefabs (`Assets/_Prefabs/AprilTagDetection (SingleTag).prefab`,
     `PipelineStatusHUD (SingleTag).prefab` — extracted from the working lifted
     scene); experiment/logging/placement stack built fresh.
4. **Docs kept in sync (repo rule #1):**
   `Assets/_Scripts/Logging/SessionLoggerSchema.md` (added `apriltag_single` /
   `apriltag_pair` sources + the observe/applied convention; still schema v1),
   `Assets/_Scripts/Logging/SingleTagObstacleHandoff.md` (CSV analysis guide),
   `Docs/SingleTagObstacleScene_Setup.md` (scene wiring guide).

### Correction model (the key design)

The obstacle stays visually world/anchor-locked during a walk. Tag detections
during the approach are **measurement only**. In the default **Deferred**
policy the held correction is applied on `ObstacleController.OnObstacleReset`
(= participant has passed). SmoothedLive/RawLive move it live (for A/B).
Logging: `state_snapshot` mode=observe (tag-proposed pose) vs mode=applied
(actual locked pose); `correction_event` per Deferred apply;
`session_event subtype=obstacle_placed` on placement.

## Immediate next step

**Device-test BOTH scenes** (QuestBuildWindow "Build + Deploy" or
`Tools/Deploy-Latest.ps1`). Watch for:
- Scene B passthrough rendering (fresh OVRPassthroughLayer vs A's building block).
- Scene B has **no occlusion** (`EnvironmentDepthManager` omitted — open
  question whether to add for parity).
- The chord map (untested on hardware; bindings are serialized — retune freely).
Then: fixes land in the shared code so both scenes benefit; analyze pulled CSVs
per `SingleTagObstacleHandoff.md`.

## New-machine setup (things that do NOT travel with git)

1. **Unity first import** — Library/ rebuilds; expect a long first open.
2. **Per-machine build config** — `UserSettings/QuestBuildSettings.json` is
   gitignored. Open the Quest Build window once (menu: Quest Build) to seed
   defaults, then set `outputFolder`.
3. **Unity MCP connection** — one manual step. A tracked
   `Unity-QuestVisionKit/.mcp.json` holds the server entry, but Claude Code only
   reads `.mcp.json` from the **repo root**, so it is invisible there. On the
   new machine either (a) copy `Unity-QuestVisionKit/.mcp.json` to the repo
   root, or (b) run the `claude mcp add --transport http ai-game-developer
   http://localhost:20788 --header "Authorization: Bearer <token>"` command
   shown in Unity's AI Game Developer window. The bearer token may differ per
   machine — always take it from that machine's Unity window. Unity must be
   open (the server runs inside the Editor), and a Claude Code session restart
   is required after MCP changes.
4. **Trial conditions** — per-participant `trial_conditions.csv` is adb-pushed
   to the device; the StreamingAssets template is the fallback.
5. **Claude memory/plans are machine-local** — this document is the handoff;
   CLAUDE.md points here.

## Unity MCP operational gotchas (hard-won; don't rediscover)

- Use the `ai-game-developer` HTTP MCP tools (`scene-*`, `gameobject-*`,
  `assets-*`, `script-execute` (Roslyn), `console-get-logs`). The CLI-backed
  `.claude/skills` do NOT work (no node/npx on PATH on the primary machine).
- `script-execute` can fire the same script **multiple times** (observed 3x) —
  make every scene-mutating script **idempotent** (a scene build got
  triplicated and had to be deduped by root name).
- `gameobject-modify` cannot set `activeSelf` (read-only) — use
  `script-execute` with `go.SetActive()` + `EditorSceneManager.MarkSceneDirty`.
- Playmode driving (`editor-application-set-state`), `console-clear-logs`, and
  `screenshot-*` are not exposed by the server config.
- Compile check recipe: `assets-refresh` -> `console-get-logs` (Error filter)
  -> optional Roslyn type-probe via `script-execute`.
- Tag detection needs the device passthrough cameras — it can never run in
  Editor playmode. Real placement is device-only.

## Facts not to re-derive / false trails

- The 2026-05-21 session analysis (`analysis_2026-05-21/`) is LOW-CONFIDENCE —
  an incomplete setup/debug run. Do not design from it.
- `ConstellationDriftCorrector` IS on this branch (under
  `Assets/Samples/9 AprilTagSpatialAnchor/`) — a prior exploration pass falsely
  reported it missing (folder-scope error). It is intentionally not wired into
  the new scenes; its solver returns later as the constellation rung.
- Planned later steps (do not build uninvited): web/remote trial-control UI
  (all control actions are already public methods for a future bridge),
  constellation solver rung.

## Working agreements observed

- Full Unity-MCP access is granted — do not ask permission per operation.
- Honest risk flags (what is untested and why) are valued over polish.
- Specific `git add` paths only, never `-A` (repo rule #5).
- Keep `LogAnalysisHandoff.md` / `SessionLoggerSchema.md` in sync with any
  logging change, same commit (repo rule #1).
- PowerShell under `Tools/` must be ASCII-only (repo rule #2).
