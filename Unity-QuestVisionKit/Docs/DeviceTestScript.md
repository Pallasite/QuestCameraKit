# Device Test Script — SingleTagObstacle-Tier0

Ordered on-device checklist for the next lab/desk pass. One build validates
both the outstanding fix-pass items (occlusion, 92 mm tag size, marker
mis-spawn — from `SessionHandoff_SingleTagObstacle.md`) and the Tier 0
hardening branch. Scene under test: **Single Tag Obstacle - Scratch** (the
only enabled build scene).

Prep: `QuestBuildWindow` Build + Deploy (or `Tools\Deploy-Latest.ps1`), tag
mounted flat on the floor (interior square = 92 mm), headset on USB for
phases 0–2 and 7–8 (untethered for the walk phases).

## 0. Boot + heartbeat (USB, before wearing)

- [ ] Run `Tools\Watch-Session.ps1` — a `[SessionHeartbeat]` status line
      appears within ~2 s of app launch and updates every second
      (`phase=Setup trial=… batt=…`).
- [ ] `rows=` climbs slowly (logger writing) and does NOT show `LOG-FAIL`.
- [ ] If the lab Wi-Fi permits at your desk: web console at
      `http://localhost:8787` after `adb forward tcp:8787 tcp:8787` still
      works and now shows Battery. (Skip in the lab.)

## 1. Setup phase / placement (headset on)

- [ ] Session HUD is readable against bare passthrough — nothing behind it
      (HUD is now an Overlay compositor layer). No text-shaped dark
      silhouettes over 3D geometry; logcat has no `Failed to find shader`.
- [ ] Guardian boundary grid never appears while the app runs (suppression
      needs passthrough up); logcat has no `Cannot suppress boundary
      visibility`.
- [ ] Setup panel shows **participant ID + source** (`P… (from
      file/inspector)`) — verify it matches the pushed `participant.txt`.
- [ ] Panel shows Trial 1/N for your CSV (works for 0- or 1-based files).
- [ ] Deliberately push a broken `trial_conditions.csv` once (duplicate + gap):
      Setup panel flags `duplicate(s)/gap(s)` — then restore the good file.
- [ ] Look at tag from ~0.5 m → "Tag visible ✓". HOLD L trigger: buzz ramps
      on the **left hand only**; ghost appears; on commit **1 short left**
      tick, then **2 long left** pulses when the obstacle actually appears
      **on** the tag (92 mm size: obstacle base at the tag, not ±1 m).
- [ ] Wireframe hugs the physical tag edges; NO extra obstacle at boot.
- [ ] No TAG SIZE MISMATCH warning (optionally mis-set tagSizeMeters once to
      see it fire, then restore).
- [ ] Ready panel: fine-tune with sticks; **A once** → "Press A again…"
      hint; **A twice** → position zeroed. L-stick click → "Finesse target
      is fixed in this scene" (no fake switch confirmation).

## 2. Occlusion + recenter (fix-pass items)

- [ ] Obstacle occludes behind a real object within ~0.75 m, soft edges; no
      `[OcclusionSwapper] ... INERT` in logcat.
- [ ] Watch for a one-frame hitch at the FIRST occlusion swap (shader
      variant compile) — note if present.
- [ ] Meta-button recenter: obstacle HOLDS its place; no clone at your feet;
      CSV later shows a `recenter` row.
- [ ] Per-eye check (2 min): close one eye at a time on the wireframe at
      ~1 m — with the 92 mm size the earlier per-eye offset should collapse.

## 3. Trials — haptic vocabulary (headset handed over or worn loosely)

- [ ] HOLD both triggers → **2 strong both** = Running; panel hides.
- [ ] Walk the trigger distance: obstacle perturbs **horizontally** along
      the tag's walking direction (both mountings if time allows).
- [ ] Step past → auto-reset; deferred correction applies between trials
      (no visible mid-walk motion).
- [ ] HOLD R trigger (redo): **2 short right**; walk clear → unprompted
      **3 strong both** = re-armed.
- [ ] HOLD R-grip + R trigger (skip): **3 short right** — confirm you can
      tell it from redo without looking.
- [ ] On the LAST trial, try skip again: **one long dull buzz** (refusal),
      NOT a success pattern.
- [ ] Press Start: **1 long strong both** = paused. Sticks during Running →
      locked (hint only when panel visible); during Paused → nudges work and
      buzz per step.
- [ ] Press Start again: **2 short both** = resumed.
- [ ] R-grip + Y: **2 left** ticks; later CSV shows `reason=set_rot_solver`
      config_change rows (cycle all 5 modes once if doing the solver pass).

## 4. Perf (the async-detection fix-pass item)

- [ ] fps ≥ ~90 inside the ~1.4 m scan gate during trials (was 60–70);
      no `ProcessImage` on the main thread if profiling.
- [ ] Pose sampler on: no measurable fps change. (If chasing frames, it is
      `Obstacle Placement System → Applied Sample Rate Hz`, default 2.)

## 5. Interruption (Tier 0 watchdogs)

- [ ] Mid-session: doff the headset ~1 min (app suspends), don it again,
      resume — next reset does NOT apply a stale pre-break correction
      (logcat: "Deferred correction skipped: proposal … old" if one was
      held).
- [ ] Cover both cameras with a hand for 10 s during Setup — detection
      resumes when uncovered; if a "GPU readback … pending" warning ever
      appears it must stop within a few seconds (else note it).

## 6. Complete + reopen

- [ ] Finish (or skip to) the last trial → Complete screen shows
      **N trials · R redos · S skips**.
- [ ] HOLD both triggers on Complete → reopens **paused** at the last trial
      ("Reopen session" label on the hold bar); navigate, resume, complete
      again.

## 7. Pull + validate (USB)

- [ ] `Tools\Pull-Sessions.ps1` → every real session bundle prints
      **PASS** (idle-launch bundles may FAIL "no experiment CSV" — expected;
      cleanup stays blocked in that case, which is correct).
- [ ] Open the CSV: `session_start` detail has `git_sha=` (non-empty);
      `conventions` and `trial_csv` rows present.
- [ ] `state_snapshot mode=applied` rows with `sampler=timer` continue
      through each walk (timestamps inside walk start→end spans) at ~2 Hz.
- [ ] Walk rows: skipped trial shows `walk_phase=abandoned` (not `end`);
      the final completed walk HAS its `end` row.
- [ ] Every finesse nudge/reset from phase 1/3 appears as
      `finesse_nudge`/`finesse_reset` rows.
- [ ] Corrections shrink-then-plateau per `SingleTagObstacleHandoff.md`.

## 8. Optional A/B + battery

- [ ] Cycle presets A/B/C (R-grip + Start hold, **3 soft both**) — config
      rows attribute correctly.
- [ ] Note battery % at start/end (heartbeat carries it) — baseline for the
      90-min budget question.

**Recording results:** annotate this file in a copy or the lab notebook;
anything unexpected → `Tools\Get-DeviceLog.ps1` immediately after (logcat
buffer is finite).
