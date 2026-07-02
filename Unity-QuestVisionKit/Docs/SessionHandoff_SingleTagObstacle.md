# Session Handoff — SingleTagObstacle branch

Self-contained handoff for any machine / fresh Claude session. Written 2026-07-02.
This is the portable copy of the session handoff (the original lived in a
machine-local plan file on the primary dev machine).

Project: Unity 6 (`6000.3.11f1`) Quest 3 MR gait experiment (UW-Madison
kinesiology). Goal metric: the virtual obstacle holds **stable, bounded error**
over a 45-90 min session (not a hard <1 cm guarantee).

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
     APIs. Bindings (serialized, untested on hardware): L index = Place now,
     R-grip + L index = Recapture, R index = Redo trial, R-grip + R index =
     Pause/Resume, Start = cycle policy, R-grip + Start = cycle variant, both
     index triggers = cycle solver. Finesse owns sticks/grips/A/B.
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
