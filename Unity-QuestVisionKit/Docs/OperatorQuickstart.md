# Operator Quickstart — Obstacle Walking Session

One page for running a session. No Unity knowledge needed. The headset guides
you: a floating panel always says what to do next.

## The 5 phases (the panel shows which one you're in)

**SETUP → READY → RUNNING → PAUSED → COMPLETE**

## Before the participant arrives

0. **Mount the tag flat on the floor with its printed TOP pointing along the
   walking direction.** The obstacle appears over the tag and will move
   forward/backward along that direction during trials. (A wall-mounted tag
   also works — then the tag's face direction is the walking direction.)
   Remove any OTHER AprilTags from the room: extra tags cost tracking
   performance.
1. Put on the headset, pick up both controllers.
2. Stand within **1 meter** of the AprilTag (the printed marker) and look at it.
   The panel says "Tag visible" when the cameras see it.
3. **HOLD the LEFT trigger** (about 1 second — you'll feel buzzing build up).
   A see-through blue "ghost" shows where the obstacle will appear. When the
   capture is stable, the real obstacle appears and the panel switches to READY.
   The panel tells you live why a capture isn't landing: **"Too far"** = step
   closer to the tag; **"Moving/turning too fast"** = hold your head still;
   **"Capturing 6/10"** = keep holding, it's collecting samples.
4. Fine-tune the obstacle position with the **thumbsticks** (left stick =
   slide, right stick = height + rotate). Hold the **left grip** for
   millimeter-precision. A/B buttons reset position/rotation if you overshoot.
5. Made a mess? **HOLD R-grip + LEFT trigger** to clear and place again.

## Starting the walks

6. Hand the headset to the participant (keep both controllers).
7. **HOLD BOTH triggers** for ~1 second → RUNNING. From here everything is
   automatic: the obstacle reacts as the participant walks, resets after each
   pass, and moves to the next trial. The panel hides during walks so the
   participant isn't distracted.

## During the walks (you hold the controllers)

- **Participant stumbled / walk fouled?** → **HOLD the RIGHT trigger** to redo
  that trial. It re-arms only after the participant walks clear of the
  obstacle — the panel says "waiting for walker to clear", and the controllers
  buzz + the panel says "re-armed" the moment they've backed off far enough
  (about half a meter beyond the trigger distance). **Redo works as many
  times as you need** — it only looks one-shot if the participant is still
  standing next to the obstacle.
- **Skip a trial?** → **HOLD R-grip + RIGHT trigger** to jump to the next
  trial without completing the current walk. (Going *backward* is web-console
  only — see below.)
- **Break needed?** → **PRESS the menu button (Start)** to pause. Press again
  to resume. While paused you can also redo, or change the experimental
  condition (**HOLD R-grip + Start**).
- Every action buzzes the controllers so you know it registered.

## Watching from the laptop (web console)

You don't need to wear the headset to see where the session is. With the
headset on USB:

```
adb forward tcp:8787 tcp:8787
```

then open **http://localhost:8787/** in a browser. The page shows the live
phase, **trial number**, condition, tag/occlusion/scan status, and has buttons
for Start/Pause/Redo/**Prev-Next trial** and the diagnostic toggles
(occlusion on/off, scan profile) used for on-device testing. It also works
over Wi-Fi via the headset's IP, port 8787.

## Ending

- When all trials finish, the panel says **"All trials complete — please
  remove the headset."** Take the headset back; the data saved automatically
  (data is saved continuously — even a dead battery only loses the last second).

## Per-participant setup (optional, from the computer)

With the headset plugged into the computer, put the participant's ID and trial
list on the device (ask the lab tech for the two files):

```
adb push participant.txt /sdcard/Android/data/<package>/files/participant.txt
adb push trial_conditions.csv /sdcard/Android/data/<package>/files/trial_conditions.csv
```

`participant.txt` = one line with the ID (e.g. `P014`). If you skip this, data
still saves under `P000` with a unique timestamp — write the real ID in the lab
notebook. **Trial numbers in the CSV must start at 0, not 1** (the headset will
warn "Trial CSV has no trial 0" if the file is 1-based).

### The trial CSV columns (no header row; `#` lines are comments)

| Col | Meaning |
|---|---|
| 1 | Trial number (0-based) |
| 2 | **Does the obstacle perturb at all** this trial? `true`/`false` |
| 3 | Direction: `true` = toward the participant, `false` = away |
| 4 | Trigger distance (m) — how close the participant gets before it moves |
| 5 | Perturbation distance (m) — how far it moves |

There is **no up/down or axis column**: the perturbation is always horizontal,
along the walking direction set by the tag (see step 0).

### The files the headset saves

Every app launch creates a folder under
`Android/data/<package>/files/Sessions/` with three files: `session.log` and
`session.json` (debug info) and **`<participant>_<timestamp>.csv` — the
experiment data; this is the file the analyst needs.** Folders from idle
launches are harmless to delete; the headset also auto-deletes all but the
newest 50 on its own.

## If something looks wrong

| Problem | Fix |
|---|---|
| "Look at the tag" won't go away | Move closer (<1 m), clean the headset cameras, add light, flatten the tag |
| Placement keeps failing | Hold your head steadier during the 1-second capture; step slightly back |
| Obstacle is in the wrong spot | R-grip + LEFT trigger (hold) to re-place, or nudge with thumbsticks |
| Nothing responds | Check controller batteries; the panel's diagnostics view (press right-stick) shows system status |
| Started trials too early | Press Start to pause, fix things, press Start to resume |

## Changing experimental conditions (A/B testing)

**HOLD R-grip + Start** cycles between the configured conditions (works in
SETUP, READY, or PAUSED — never mid-walk). The active condition shows on the
panel's top line and is recorded in the data automatically.

The build ships three conditions:

| Preset | Behavior |
|---|---|
| **A** | Obstacle stays locked during walks; any tracking correction is applied as a snap *between* trials (default). |
| **B** | Obstacle **gradually glides** toward the corrected position, including during walks ("rolls back into place"). |
| **C** | Obstacle snaps to every raw tag detection — jittery, **diagnostic only**, not for participants. |

Ask the lab tech to add more: Unity Inspector → "Obstacle Placement System" →
Obstacle Placement Controller → Presets list.
