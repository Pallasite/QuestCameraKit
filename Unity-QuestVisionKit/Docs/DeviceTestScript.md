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
- [ ] **Trial picker (Ready).** R-grip + X → **2 soft left**, panel shows
      "Go to trial N (n/M)". L stick left/right steps 1, up/down steps 5;
      on a CSV with a gap the number **steps over** the gap and **clamps** at
      both ends (tiny tick, never wraps to the other end). HOLD X →
      **1 long + 2 short left**; panel shows the new trial.
- [ ] **The arming check (do not skip).** Stand INSIDE the trigger distance
      and seek in Ready: the obstacle must **not** move, then start trials and
      confirm the first walk perturbs normally. (A seek that left the obstacle
      armed would silently void the first walk.)
- [ ] **Stick handback.** Open the picker, hold the L stick fully deflected,
      press R-grip + X to close, and keep holding: the obstacle must **not**
      drift. Release, then confirm nudges work again.

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
- [ ] After stepping past: **~3 s later** (participant off the gait mat) HUD
      pops large **"Walk n"** (raw 0-based CSV index) alone for ~2 s, then
      hides. Also after skip; NOT after redo.
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
- [ ] **Trial picker (Paused).** Pause mid-session, seek from trial A to a
      distant trial B, resume: the participant walks B. Skip counter goes up
      by 1.
- [ ] **Picker auto-exit.** Open the picker while paused, then press Start to
      resume: **1 long dull buzz** (cancelled). Now HOLD the R trigger — it
      must perform a normal **redo** (2 short right), proving the modes are
      cleanly separated.
- [ ] R-grip + Y: **2 left** ticks; later CSV shows `reason=set_rot_solver`
      config_change rows (cycle all 5 modes once if doing the solver pass).

## 4. Perf + frame pacing (72 Hz pass)

The app now owns the display refresh rate (`Display Config` →
`XRDisplayConfigurator`, default **72 Hz**). The goal is no longer "more fps" —
it is *uniform* frame delivery. 72 Hz gives the renderer 13.9 ms per frame
instead of 11.1 ms, which is the point.

- [ ] **Reset the headset's own Settings → Display refresh to its default
      before this pass.** The whole point is that the app sets the rate; a
      hand-set 72 would mask a failed request.
- [ ] Boot logcat: `[XRDisplayConfigurator] requesting target=72Hz
      supported=True ... available=[72,80,90,...]`, then ~0.25 s later
      `confirmed applied=72Hz`. A `request NOT honored` warning means the
      headset is overriding the app — STOP, nothing below is meaningful.
- [ ] Frame pacing during trials inside the ~1.4 m scan gate is subjectively
      steady, with the 80–90 fps wobble gone. No `ProcessImage` on the main
      thread if profiling.
- [ ] Optional A/B: web console → **Display Hz** cycles 72→80→90→120. Walk two
      trials at 90, two at 72, and have the wearer say which felt smoother
      without being told which is active. Each leg writes a
      `display_frequency` row with `reason=console`. Leave it on 72.
- [ ] Pose sampler on: no measurable change. (If chasing frames, it is
      `Obstacle Placement System → Applied Sample Rate Hz`, default 2.)
- [ ] Battery % at start/end vs. the old 90 Hz baseline — 72 Hz should improve
      it. Note the result either way (see phase 8).

## 5. Interruption (Tier 0 watchdogs)

- [ ] Mid-session: doff the headset ~1 min (app suspends), don it again,
      resume — next reset does NOT apply a stale pre-break correction
      (logcat: "Deferred correction skipped: proposal … old" if one was
      held).
- [ ] Same doff/don: a `display_frequency` row appears with
      `reason=hmd_mounted` and `applied=72`. *** This is the regression the
      72 Hz change exists to prevent — before it, a doff/don could silently
      revert the headset to its default rate for the rest of the session. ***
- [ ] Cover both cameras with a hand for 10 s during Setup — detection
      resumes when uncovered; if a "GPU readback … pending" warning ever
      appears it must stop within a few seconds (else note it).

## 6. Complete + reopen

- [ ] Finish (or skip to) the last trial → Complete screen shows
      **N trials · R redos · S skips**.
- [ ] HOLD both triggers on Complete → reopens **paused** at the last trial
      ("Reopen session" label on the hold bar); navigate, resume, complete
      again.
- [ ] After reopening, use the picker to seek back several trials, then pull
      the CSV: there must be **no** `walk_phase=abandoned` row for the last
      trial and the skip count must be unchanged (`trial_seek ... abandoned=0`).

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
- [ ] `trial_seek` rows match what you did: `abandoned=1` for the paused
      mid-session seek (with a matching `walk_phase=abandoned`), `abandoned=0`
      for the Ready seek and the post-reopen seek (with NO abandoned row).
- [ ] No `walk_phase=moved` row carries a timestamp from before the first
      `phase_change ... to=Running`.
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
