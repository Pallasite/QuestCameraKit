# Repo notes for Claude

QuestCameraKit is a Unity 6 (`6000.3.11f1`) Quest/Android project for a
kinesiology drift-correction experiment at UW-Madison. Multi-phase build
pipeline + session-logging infrastructure has been built up on the
`ControllerCorrections` branch (see plan file pointer below).

## Files Claude is likely to touch

- **Build pipeline.** `Unity-QuestVisionKit/Assets/Editor/QuestBuild/`
  (`QuestBuilder`, `QuestBuildSettings`, `QuestBuildWindow`) plus PowerShell
  helpers under `Tools/`. Per-machine settings live in
  `Unity-QuestVisionKit/UserSettings/QuestBuildSettings.json` (gitignored).
- **Dev session log.** `Unity-QuestVisionKit/Assets/Scripts/QuestBuild/`
  (`SessionLogger`, `BuildInfo`). Output bundle on device is
  `Application.persistentDataPath/Sessions/<sessionId>/session.{log,json}`.
- **Per-launch path helper.**
  `Unity-QuestVisionKit/Assets/_Scripts/Logging/SessionPaths.cs` owns the
  single SessionId for an app launch; every writer routes through
  `SessionPaths.Combine(filename)` so all per-launch outputs land in one folder.
- **Experiment + dev loggers (data).** `Unity-QuestVisionKit/Assets/_Scripts/Logging/`
  - `SessionLogger.cs` (wide experiment CSV, schema_version=1)
  - `SessionLoggerTrialSubscriber.cs` (walk/trial rows)
  - `ReferenceAnchorLogger.cs` (dev-only: N reference OVRSpatialAnchors over time)
  - `TrackingEventsLogger.cs` (dev-only: tracking-quality transitions)
  - `HeadsetPoseLogger.cs` (dev-only: per-frame headset pose + 1Hz jitter stats)
  - `ControllerPoseLogger.cs` (dev-only: per-frame controller poses + 1Hz jitter stats)
- **Correction subsystems.** `Unity-QuestVisionKit/Assets/_Scripts/Correction/`
  (drift correctors + observer loggers).
- **Analyst handoff doc.**
  `Unity-QuestVisionKit/Assets/_Scripts/Logging/LogAnalysisHandoff.md` is the
  contract with downstream analysis.

## Workflow rules

1. **Keep the analyst handoff doc in sync with the loggers.** Whenever you change
   what is logged, how it is logged, or where it is written, update
   `Unity-QuestVisionKit/Assets/_Scripts/Logging/LogAnalysisHandoff.md` in the
   same commit. The doc is the contract with downstream analysis; drift hurts.
2. **PowerShell scripts under `Tools/` must be ASCII-only.** Windows PowerShell 5.1
   reads no-BOM files as Windows-1252, so non-ASCII characters (em-dashes, smart
   quotes) break tokenisation with confusing brace-mismatch errors. C# and
   Markdown files are fine - Unity and the doc viewer read them as UTF-8.
3. **High-frequency dev loggers must self-gate on `Debug.isDebugBuild`.** The
   Phase 4 pose/jitter/reference-anchor loggers must not run (and therefore not
   write their CSVs) in non-development production builds, so analysts can tell
   by file presence whether high-freq capture was on for that session.
4. **Build outputs and per-machine settings live outside the repo.** Builds go
   to `outputFolder` from `QuestBuildSettings.json`; per-machine config lives
   in `UserSettings/` (already gitignored). Do not commit either.
5. **Specific `git add` paths, never `git add -A`.** The repo regularly has
   user WIP (`.unity` scene edits, local analysis dumps, Word lock files) that
   should not be swept into commits.

## Current work + session handoff

Active work is the **`SingleTagObstacle` branch** (simplified single/double-
AprilTag obstacle placement). On any machine, read
`Unity-QuestVisionKit/Docs/SessionHandoff_SingleTagObstacle.md` **first** — it
is the portable session handoff: what's built, what's next, per-machine setup
(build config, Unity MCP registration), and hard-won Unity-MCP gotchas.

(Historical note: earlier machine-local plan files under `~/.claude/plans/` do
not travel with the repo; the handoff doc above supersedes them.)
