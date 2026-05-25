# Stationary Controller Drift — Analysis Handoff

This is a focused analysis spec for **one specific question**: do tripod-mounted
Touch Plus controllers drift in their reported pose while physically
stationary, and if so, what conditions modulate it?

This doc builds on `LogAnalysisHandoff.md`. Read that first for schema,
file layout, and parsing helpers (`parse_vec3`, header parsing, etc.).
Nothing here re-explains column meanings the main doc already covers —
it just says which subsets to pull and what to do with them.

---

## Background — the observation under test

During development sessions, the project owner noticed that when both Touch
Plus controllers sit motionless on tripods, a virtual object computed from
their midpoint visibly drifts against passthrough landmarks. Deliberately
wiggling a controller produces a visible "snap" back to a more accurate
alignment.

The controllers are not stowed — they're tripod-mounted in the experimenter's
view, the headset is moving around the room, and `OVRInput` continues to
report `position_valid=true` throughout.

### Working hypothesis (treat as testable, not confirmed)

Controller pose tracking degrades while the controller is physically
stationary, because the optical-IMU fusion downweights the optical channel
when the IMU reports motionlessness with high confidence. Physical motion
of the controller (not just the HMD) forces the optical channel back in,
correcting accumulated drift.

### Sub-hypotheses testable from existing logs

- **H1**: Inter-controller deviation grows with time-since-last-motion of
  either controller.
- **H2**: HMD motion alone does **not** refresh controller tracking — the
  controller itself needs to move.
- **H3**: The current 5s keep-alive haptic (amplitude 0.02, 50ms) is
  insufficient to perturb the optical channel.
- **H4**: When `velocity_L_mps` or `velocity_R_mps` spikes (any cause),
  inter-controller deviation drops within ~1s after.

### What existing tripod sessions **cannot** test (without protocol or code changes)

- **Deliberate wiggle snap-back** is now testable, but only if the
  experimenter actively introduces wiggles into the session. The
  controllers don't move on their own. See Test 5's experimenter
  protocol — manual tipping of the tripod at known times is the
  intervention.
- **Optical vs IMU contribution separately.** The Quest runtime does the
  fusion internally and only the fused pose is exposed via `OVRInput`.
  There's no public hook for "current optical confidence" or "current
  IMU integration weight." Indirect inference via variance behavior is
  possible — see Test 7.
- **Whether the production velocity gate** (reject corrections when `v >
  2 cm/s while static`) is pointing the right direction. We can observe
  the velocity distribution but can't confirm what the *correct* response
  to those velocities would be without external ground truth.
- **Which controller (L vs R) is drifting.** A single controller's
  world-position stability is coupled with HMD SLAM error — "left moved
  3mm" and "the world frame shifted 3mm under a stable left controller"
  look identical from these logs alone. Resolvable with a small logging
  addition (continuous constellation pose as independent reference); see
  the "Coding asks" section at the end.

---

## A note on what's clean and what's confounded

The cleanest signal in the schema is **inter-controller distance and
rotation** (`inter_controller_distance_m`, `deviation_from_baseline_m`,
etc.). It's a relative measurement between two world poses, so HMD SLAM
drift largely cancels — the world frame can wobble all it wants and the
inter-controller transform doesn't care, to first order.

Single-controller world poses (`controller_L_pos_xyz`, etc.) include HMD
SLAM coupling. They're still informative but not cleanly attributable to
controller-side error.

The virtual obstacle's perceived alignment in passthrough (the
project-owner's visual test) is even *less* coupled to HMD SLAM than
inter-controller distance is — because the rendering and the controller
poses use the same world frame, world-frame drift cancels in the loop.
But that's a perceptual measurement, not a log-derivable one.

So: the log analysis below leans heavily on inter-controller transform
metrics and treats single-controller world coordinates as supporting
evidence only.

---

## Tests

### Test 1 — Pure controller-pair drift over the session

**Hypothesis addressed**: Existence of stationary drift at all.

Filter `state_snapshot` rows from `correction_source == 'anchor_baseline'`
where `deviation_from_baseline_m` is populated. Plot the four time series:

- `inter_controller_distance_m` over `timestamp_session`
- `inter_controller_rotation_deg` over `timestamp_session`
- `deviation_from_baseline_m` over `timestamp_session`
- `deviation_from_baseline_deg` over `timestamp_session`

Also produce a distribution histogram of `deviation_from_baseline_m` and a
30-second rolling stddev over the session.

```python
ab = df[(df["event_type"] == "state_snapshot")
        & (df["correction_source"] == "anchor_baseline")
        & df["deviation_from_baseline_m"].notna()].copy()

fig, axes = plt.subplots(2, 1, sharex=True, figsize=(12, 6))
axes[0].plot(ab["timestamp_session"], ab["deviation_from_baseline_m"])
axes[0].set_ylabel("deviation_from_baseline_m")
axes[1].plot(ab["timestamp_session"], ab["deviation_from_baseline_deg"])
axes[1].set_ylabel("deviation_from_baseline_deg")
axes[1].set_xlabel("timestamp_session (s)")
plt.savefig("test1_drift_over_session.png")

ab["deviation_from_baseline_m"].hist(bins=80)
plt.title("deviation_from_baseline_m distribution")
plt.savefig("test1_deviation_histogram.png")
```

**Interpretation**

- Slow walk-away from baseline over minutes → systematic bias (IMU
  integration drift) accumulating.
- Sharp spikes back toward baseline → optical re-acquisition events.
  Note their timestamps for cross-referencing with Tests 4 and 5.
- High variance even at session start → physical rig isn't stable
  enough; the tracking might be fine. Rule out before blaming
  controllers.
- **Bimodal** distribution (tight cluster around baseline + outlier tail)
  → rigid-body validation gate likely to work well; pick its threshold
  near the gap between the modes.
- **Smooth, wide** distribution → rigid-body validation will reject too
  much real signal at any reasonable threshold.

**If this test shows essentially flat deviation, stop** — the hypothesis
isn't observable in this session's logs. Try a longer session, or one
where the controllers sat longer without disturbance.

**Experimenter protocol**

- Capture the rigid-body baseline at the start of the session (run
  `ControllerRigidBodyValidator.CaptureBaselineNow()` or whatever the
  scene's calibration button maps to). Without a baseline, the
  `deviation_from_baseline_*` columns are empty and this test is dead.
- Don't bump the tripods after baseline capture. If you do, restart the
  session.
- Include a 2+ minute still period at the start before any walks begin.
  This establishes the noise floor and gives you "drift while genuinely
  undisturbed" to compare against later activity.
- Note rig integrity at session end (was a tripod nudged? a cable
  pulled?) in the session notes field or a paper log.

---

### Test 2 — Drift vs time-since-last-motion (H1)

**Hypothesis addressed**: Stationary controllers drift *because* they're
stationary.

For each `state_snapshot`, compute "time since either controller last had
velocity above a threshold." Plot deviation against time-since-motion.

The threshold should be slightly above the noise floor of
`velocity_L_mps` / `velocity_R_mps` for a confirmed-still controller. A
reasonable starting point: 1 cm/s. Tune by looking at the velocity
distribution in a known-still segment.

```python
snap = df[(df["event_type"] == "state_snapshot")
          & (df["correction_source"] == "anchor_baseline")].copy()
snap = snap.sort_values("timestamp_session").reset_index(drop=True)

threshold_mps = 0.01  # tune from the data
moving = (snap["velocity_L_mps"].fillna(0) > threshold_mps) | \
         (snap["velocity_R_mps"].fillna(0) > threshold_mps)

# Time since last "moving" frame
last_motion_ts = snap["timestamp_session"].where(moving).ffill()
snap["time_since_motion_s"] = snap["timestamp_session"] - last_motion_ts

# Bucket
import numpy as np
bins = [0, 1, 5, 15, 60, 300, 1e9]
snap["bucket"] = pd.cut(snap["time_since_motion_s"], bins=bins)
print(snap.groupby("bucket")["deviation_from_baseline_m"].describe())

snap.boxplot(column="deviation_from_baseline_m", by="bucket")
plt.savefig("test2_drift_vs_time_since_motion.png")
```

**Interpretation**

- Monotonic rise across buckets → **H1 supported**. Drift accumulates
  while still.
- Flat across buckets → drift is happening but stationarity isn't the
  cause. Look at IMU temperature drift, controller-to-headset distance,
  room lighting changes, or HMD-side issues.
- Non-monotonic / noisy → small sample size, or other factors dominate
  the variance. Combine with Test 1's rolling stddev to see if a session
  is even long-stable enough to extract this signal.

**Experimenter protocol**

- For best results, structure the session to include a mix of
  short-still and long-still windows. E.g.: 2-min still → walks for 5
  min → 5-min still → walks → 10-min still at end. The variance in
  time-since-motion is what makes this test work; a session that's
  uniformly busy or uniformly still gives you only one bucket.
- If you introduce deliberate tip-events (per Test 5's protocol), those
  reset the time-since-motion clock and give you natural bucket
  boundaries. Plan tips to break up long still windows rather than
  clustering at the end.

---

### Test 3 — HMD motion correlation (H2)

**Hypothesis addressed**: Whether HMD motion alone refreshes controller
tracking via parallax (as I initially speculated), or whether the
controllers themselves need to move (as the project-owner's wiggle
observation suggests).

Compute headset velocity from successive `headset_pos_xyz` at the 5 Hz
snapshot rate. Bucket inter-controller deviation by HMD velocity band.

```python
snap = df[(df["event_type"] == "state_snapshot")
          & (df["correction_source"] == "anchor_baseline")].copy()
snap = snap.sort_values("timestamp_session").reset_index(drop=True)
snap[["hx", "hy", "hz"]] = snap["headset_pos_xyz"].apply(parse_vec3).apply(pd.Series)

dt = snap["timestamp_session"].diff()
dpos = np.sqrt(snap["hx"].diff()**2 + snap["hy"].diff()**2 + snap["hz"].diff()**2)
snap["hmd_speed_mps"] = dpos / dt

bins = [0, 0.05, 0.2, 0.6, 1.5, 10]
snap["hmd_bucket"] = pd.cut(snap["hmd_speed_mps"], bins=bins)
print(snap.groupby("hmd_bucket")["deviation_from_baseline_m"].describe())

snap.boxplot(column="deviation_from_baseline_m", by="hmd_bucket")
plt.savefig("test3_drift_vs_hmd_velocity.png")
```

**Interpretation**

- **Flat across HMD velocity bands** → H2 supported. HMD motion alone
  doesn't help. Controller motion is the actual mechanism.
- **Lower deviation at higher HMD velocity** → HMD-induced parallax IS
  doing something. It's still probably weaker than direct controller
  motion (Test 5 will speak to that), but the rig design might benefit
  from putting the controllers in the headset's natural sweep line during
  walks.

**Caveat**: 5 Hz HMD-velocity is coarse. The buckets mostly distinguish
"walking" from "stationary observer" rather than fine-grained motion
gradient. Don't over-interpret bucket-to-bucket monotonicity.

**Experimenter protocol**

- Include 30–60 second "stand still and look around" windows at known
  times (mid-session is good). Head turns produce HMD motion without
  walking, which gives you intermediate velocity bands the analysis
  needs.
- Also include "stand completely still, facing the rig" windows of
  similar length. These are the zero-velocity bucket and the cleanest
  test of "controllers drift while *nothing* moves."
- Vary your distance from the rig across these windows — controller
  tracking quality may depend on headset-controller distance, which is
  worth noting in a future analysis.

---

### Test 4 — Keep-alive pulse correlation (H3)

**Hypothesis addressed**: Whether the existing pulse profile is strong
enough to perturb the optical channel at all.

`sleep_event` rows with `sleep_event_type == 'pulse'` give exact pulse
timestamps. For each pulse, compute pre-pulse and post-pulse mean
deviation over a short window (e.g. ±1 s) and look at the distribution of
their differences.

```python
pulses = df[(df["event_type"] == "sleep_event")
            & (df["sleep_event_type"] == "pulse")]

snap = df[(df["event_type"] == "state_snapshot")
          & (df["correction_source"] == "anchor_baseline")
          & df["deviation_from_baseline_m"].notna()].copy()
snap = snap.sort_values("timestamp_session")

deltas = []
for ts in pulses["timestamp_session"]:
    pre  = snap[(snap["timestamp_session"] >= ts - 1.0) & (snap["timestamp_session"] < ts)]
    post = snap[(snap["timestamp_session"] >  ts) & (snap["timestamp_session"] <= ts + 1.0)]
    if len(pre) and len(post):
        deltas.append(post["deviation_from_baseline_m"].mean()
                      - pre["deviation_from_baseline_m"].mean())

pd.Series(deltas).hist(bins=40)
plt.axvline(0, color="k", linestyle="--")
plt.title(f"Pulse effect on deviation (n={len(deltas)}), mean={np.mean(deltas):.4f} m")
plt.savefig("test4_pulse_effect.png")
```

**Interpretation**

- Distribution centered on zero → **H3 supported**. Pulses don't perturb
  optics meaningfully. Future characterization session should test
  stronger profiles (amplitude 0.3–0.5, longer pulse duration, or
  staggered double-pulses creating brief actual motion).
- Skews negative (post < pre) → pulses help even at 0.02 amplitude.
  Worth tightening cadence rather than amplitude.
- Skews positive (post > pre) → pulses are *introducing* error. Likely
  IMU ringing immediately after the haptic. Unexpected; would suggest
  shorter pulse duration or a quiet-window gate on corrections.

**Experimenter protocol**

- No special action — pulses fire automatically at the configured
  cadence. Just make sure the keep-alive system is enabled on the
  `ControllerSleepMitigation` component before starting.
- For a stronger version of this test: run two back-to-back sessions
  with different pulse profiles (e.g., default vs amplitude=0.3 or
  cadence=2s) and compare the Test 4 distributions across them. This
  needs the coding ask "configurable pulse profile from inspector"
  below.

---

### Test 5 — Velocity spike → drift drop (H4)

**Hypothesis addressed**: The wiggle-induced snap-back observed visually,
detected in log data.

Look for moments where `velocity_L_mps` or `velocity_R_mps` exceeds a
"that's a real motion" threshold (e.g. 5 cm/s — well above the noise
floor seen in Test 2). With the tipping protocol below, these become
known scheduled events rather than incidental ones. Plot deviation in a
±5 s window around each spike, spike-relative time on the x-axis.

```python
snap = df[(df["event_type"] == "state_snapshot")
          & (df["correction_source"] == "anchor_baseline")].copy()
snap = snap.sort_values("timestamp_session").reset_index(drop=True)

spike_threshold = 0.05
spike_mask = (snap["velocity_L_mps"].fillna(0) > spike_threshold) | \
             (snap["velocity_R_mps"].fillna(0) > spike_threshold)

# Collapse runs of consecutive spike-frames into one spike per event
spike_events = snap[spike_mask & ~spike_mask.shift(1, fill_value=False)]
print(f"Found {len(spike_events)} spike events")

window = 5.0
traces = []
for ts in spike_events["timestamp_session"]:
    win = snap[(snap["timestamp_session"] >= ts - window)
               & (snap["timestamp_session"] <= ts + window)].copy()
    win["t_rel"] = win["timestamp_session"] - ts
    traces.append(win[["t_rel", "deviation_from_baseline_m"]])

if traces:
    combined = pd.concat(traces)
    combined["t_bin"] = (combined["t_rel"] / 0.2).round() * 0.2
    averaged = combined.groupby("t_bin")["deviation_from_baseline_m"].mean()
    averaged.plot()
    plt.axvline(0, color="r", linestyle="--", label="velocity spike")
    plt.title(f"Mean deviation around velocity spikes (n={len(traces)})")
    plt.savefig("test5_spike_response.png")
```

**Interpretation**

- Clear drop in deviation in the second after spike → snap-back confirmed
  in log data. Strong support for the wiggle hypothesis.
- Magnitude of the post-spike deviation tells you how good the
  "corrected" tracking actually is. The pre-spike value is how much
  drift had accumulated.
- No correlation → either spikes were too small (tipping was too
  gentle), or the inter-controller deviation signal doesn't capture
  what's happening visually. Cross-check by separating L-only spikes
  from R-only spikes and seeing if the deviation pattern matches.
- Drop *before* the spike → coincidence, or you tipped reactively
  because you saw misalignment. Plan to tip on a schedule (clock-based),
  not based on observed drift.

**Experimenter protocol**

This is the test that asks the most of you. The protocol turns
incidental spikes into a controlled experiment.

- Pick scheduled tip times in advance — e.g., at +3, +8, +15, +25
  minutes into the session. Use a watch or a phone timer.
- At each scheduled time, perform a known tip:
  - **Left only**: tip the left tripod ~5–10° off vertical, return to
    upright, hold still for ≥30s.
  - Wait at least 60s.
  - **Right only**: same on the right.
  - Wait at least 60s.
  - **Both simultaneously**: tip both, return both, hold still.
- Record the wall-clock time and which intervention each was (paper log,
  or speak it aloud if you have audio capture). Without this, you can
  still detect spikes in the velocity columns, but you can't tell L-tip
  from R-tip from both-tip after the fact at a glance.
- Try to make the tips deliberate and brief (≤1s of motion), not
  prolonged wobble. A clean impulse-then-still gives the cleanest
  signal — the analysis wants to see deviation *immediately after* the
  motion ends.
- If at all possible, log a marker event at the moment of each tip via
  controller button press (see "Coding asks" below — a "mark event"
  button would make this analysis dramatically cleaner).

---

### Test 6 — Placer-anchor vs live-controller-midpoint divergence

**Hypothesis addressed**: Direct visualization of "controllers drifted
during a known-still window."

When a `controller_placer` lock is active, you have:

- The placer's dedicated `OVRSpatialAnchor` pose, in `correction_source ==
  'controller_placer'` rows (`anchor_pos_xyz`).
- The live controller poses, in nearest `correction_source ==
  'anchor_baseline'` rows.

The placer anchor was captured at lock time and from that moment forward
represents the OS's best estimate of "the pose the controllers were at
when locked." The live controller midpoint is computed from current
controller world poses. If the controllers drift during the lock window,
those two diverge.

```python
ab = df[(df["event_type"] == "state_snapshot")
        & (df["correction_source"] == "anchor_baseline")
        & df["controller_L_pos_xyz"].notna()
        & df["controller_R_pos_xyz"].notna()].copy()
ab[["Lx", "Ly", "Lz"]] = ab["controller_L_pos_xyz"].apply(parse_vec3).apply(pd.Series)
ab[["Rx", "Ry", "Rz"]] = ab["controller_R_pos_xyz"].apply(parse_vec3).apply(pd.Series)
ab["mid_x"] = (ab["Lx"] + ab["Rx"]) / 2
ab["mid_y"] = (ab["Ly"] + ab["Ry"]) / 2
ab["mid_z"] = (ab["Lz"] + ab["Rz"]) / 2

cp = df[(df["event_type"] == "state_snapshot")
        & (df["correction_source"] == "controller_placer")].copy()
cp[["ax", "ay", "az"]] = cp["anchor_pos_xyz"].apply(parse_vec3).apply(pd.Series)

merged = pd.merge_asof(
    cp[["timestamp_session", "ax", "ay", "az"]].sort_values("timestamp_session"),
    ab[["timestamp_session", "mid_x", "mid_y", "mid_z",
        "velocity_L_mps", "velocity_R_mps"]].sort_values("timestamp_session"),
    on="timestamp_session", direction="nearest", tolerance=0.3,
)
merged["divergence_m"] = np.sqrt(
    (merged["ax"] - merged["mid_x"])**2
    + (merged["ay"] - merged["mid_y"])**2
    + (merged["az"] - merged["mid_z"])**2
)

# Restrict to genuinely still windows
still = merged[(merged["velocity_L_mps"].fillna(0) < 0.01)
               & (merged["velocity_R_mps"].fillna(0) < 0.01)]
still.plot(x="timestamp_session", y="divergence_m",
           title="placer anchor vs live controller midpoint, still-window only")
plt.savefig("test6_placer_vs_live.png")
```

**Interpretation**

- Divergence growing over a still window after lock → direct evidence
  the controllers drifted while motionless. Magnitude here is the
  practical scale of the problem (compare against the 1 cm experiment
  tolerance).
- No divergence in still windows → controllers were stable during this
  lock, OR both they and the placer anchor drifted together (less likely
  given they're different drift dynamics, but possible).
- Divergence only when velocity is non-zero → controllers were
  disturbed, not drifting. Filtering on velocity should already remove
  these, but check the velocity time series to confirm.

**Caveat**: Skip this test entirely if the session contains no
`session_event subtype=obstacle_placer_lock` rows (no placer activity
happened).

**Experimenter protocol**

- Engage the placer lock once at a known stable moment — after the
  initial baseline capture and before any walking starts works well.
- Leave the lock engaged for the longest still window you have. The
  analysis wants ≥5 minutes of locked-and-still data to see drift
  accumulate meaningfully.
- Don't toggle the lock repeatedly. Each lock-on re-anchors the placer
  to the current controller midpoint, resetting the divergence to zero.
- If you also want to test the response to a wiggle: tip a controller
  while the lock is engaged. The divergence will jump (the controllers
  moved) and then either stay at the new value (drift compensation
  didn't trigger) or return toward zero (something corrected). Combine
  this finding with Test 5's spike analysis.

---

### Test 7 — Variance signature: random-walk vs bounded (indirect optical-vs-IMU proxy)

**Hypothesis addressed**: Whether the inter-controller distance behaves
like a pure IMU integration (variance grows with time → random walk) or
like an optically-corrected measurement (variance plateaus → bounded
estimation error).

Pure IMU integration has a characteristic variance signature: the
standard deviation of position over a time window grows roughly as √t
(random walk). Optically-corrected tracking has bounded variance —
extending the window doesn't keep growing the stddev because the
underlying signal isn't actually drifting unboundedly. Pick the longest
contiguous still window in the session and compute rolling stddev of
inter-controller distance over varying window sizes.

```python
snap = df[(df["event_type"] == "state_snapshot")
          & (df["correction_source"] == "anchor_baseline")
          & df["inter_controller_distance_m"].notna()].copy()
snap = snap.sort_values("timestamp_session").reset_index(drop=True)

# Restrict to a known-still segment. Either filter by velocity
# threshold, or hardcode the timestamp range of a still period from
# the experimenter's notes.
still = snap[(snap["velocity_L_mps"].fillna(0) < 0.005)
             & (snap["velocity_R_mps"].fillna(0) < 0.005)]

window_sizes_s = [1, 5, 30, 120, 600]
print("Window (s) | Stddev of inter_controller_distance_m")
for w_s in window_sizes_s:
    rows_per_window = int(w_s * 5)  # 5 Hz snapshots
    if rows_per_window > len(still):
        continue
    rolling_std = still["inter_controller_distance_m"].rolling(
        window=rows_per_window, min_periods=rows_per_window
    ).std().dropna()
    print(f"{w_s:>10} | {rolling_std.mean():.6f} m  (median {rolling_std.median():.6f})")

# Plot: stddev as a function of window size (log-log makes the slope clear)
import numpy as np
sizes, stds = [], []
for w_s in window_sizes_s:
    rows_per_window = int(w_s * 5)
    if rows_per_window > len(still):
        continue
    rs = still["inter_controller_distance_m"].rolling(
        rows_per_window, min_periods=rows_per_window).std().dropna()
    sizes.append(w_s); stds.append(rs.median())

plt.loglog(sizes, stds, marker="o")
plt.xlabel("window size (s)"); plt.ylabel("median rolling stddev (m)")
plt.title("Variance vs window size — slope ≈ 0.5 means random walk")
plt.savefig("test7_variance_signature.png")
```

**Interpretation**

- **Stddev grows roughly as √t (slope ≈ 0.5 on log-log)** → random-walk
  behavior. The fusion filter is letting IMU integration drive the
  position estimate. This is the "optical is downweighted" signature.
- **Stddev plateaus past some window size** → bounded estimation error.
  Optical corrections are keeping things in check despite the
  stationary state. The drift the experimenter sees visually would
  then need a different explanation (e.g., calibration drift between
  optical and IMU frames, room lighting changes, etc.).
- **Stddev grows faster than √t (slope > 0.5)** → likely temperature
  drift in the IMU or some other slowly-evolving bias source on top of
  random walk.

**Experimenter protocol**

- This is the test that benefits most from a long, completely
  undisturbed still window. **At least 10 minutes** with no walking, no
  tipping, no rig contact. 20 minutes is better.
- Place it at the start of the session, before walks begin and before
  any deliberate tip events. Mark its start and end times.
- The longer this window, the more window sizes the analysis can fit
  against — and the more confident the variance-signature slope
  estimate becomes.

**Caveat**

This is a statistical signature, not a direct measurement. A slope near
0.5 is suggestive of random-walk behavior but doesn't prove the optical
channel is downweighted (could also be e.g. mounting compliance in the
tripod producing low-frequency wobble). Combine with Test 5 (does a
forced wiggle restore accuracy?) before drawing strong conclusions.

---

## What these tests can and can't separate

| Question | Tests | Cleanly answerable? |
|---|---|---|
| Does either controller drift while stationary? | 1, 2, 6 | Yes (relative measure) |
| Is time-since-motion the driver? | 2 | Yes |
| Does HMD motion alone help? | 3 | Partial (coarse buckets, walking confound) |
| Are keep-alive pulses effective? | 4 | Yes |
| Does deliberate motion correct drift? | 5 | Yes, with the tipping protocol |
| Is the underlying behavior random-walk (IMU-dominated)? | 7 | Yes, statistically — needs long still window |
| Which controller (L vs R) is drifting? | (none) | **No** — needs continuous constellation-pose logging (see Coding asks) |
| What's the absolute drift rate in physical-space units? | 1, 6 | Partial — relative drift directly, absolute only via Test 6 divergence |

The L-vs-R question and absolute drift rate are the strongest motivators
for a new characterization session (described in the Implementation
Handoff): vibration profile variations, known still/wiggle windows,
controllers in known fixed positions relative to a room feature for an
external reference.

---

## Suggested execution order

1. **Test 1 first.** If inter-controller deviation is essentially flat
   over the session, stop — the hypothesis isn't observable in these
   logs. Either the rig is more stable than expected or the session is
   too short. Pull a longer one before continuing.
2. **Test 2** if Test 1 shows drift. This is the most decision-shaping
   result: if H1 doesn't hold, the rest of the hypothesis collapses and
   the production design needs to look elsewhere for the drift source.
3. **Test 7** alongside Tests 1–2 if a long-enough still window exists.
   It's the cheapest "is the underlying process random-walk?"
   characterization and runs on the same data slice.
4. **Tests 3, 4, 5** are independent. Each addresses one specific
   decision in the production correction system:
   - Test 3 → does the rig design need to be in the headset's natural
     view during walks?
   - Test 4 → does the keep-alive pulse profile need to be stronger?
   - Test 5 → does deliberate motion restore tracking accuracy? (Needs
     the tipping protocol — without it, this test has no events to
     analyze.)
5. **Test 6** if any placer-lock events exist. This is the most direct
   visualization of the actual user-visible problem.

---

## Output format per test

For each test produce:

- The plot (PNG)
- A 2–3 sentence summary of what the plot shows
- An explicit verdict on the hypothesis: **supported / contradicted / inconclusive**
- Any follow-up the analysis suggests (parameter sweep, new session
  recipe, code change)

If H1 is supported and the deviation magnitudes are on the millimeter
scale or larger (not micrometers), prioritize getting that result back
quickly — it has consequences for the production correction design that
the implementation handoff currently assumes go the other way (the
velocity gate, the rig-stillness premise, the keep-alive pulse strength).

---

## Coding asks that would enable better testing

These are suggestions, not specifications — they're meant to be sized
and prioritized by whoever's doing the implementation. Listed roughly in
order of analysis value per unit of dev effort.

### 1. Continuous constellation-pose logging (unlocks L-vs-R discrimination)

The constellation drift corrector already computes a per-frame
constellation-derived pose internally; it's the input to the correction
logic. Surface that pose as a logged column on every `state_snapshot`,
not just when a correction is triggered. Once that's logged, each
controller's pose minus the constellation pose gives you a quasi-stable
per-controller drift signal — one that doesn't move with HMD SLAM error
(because the constellation pose moves with it identically) and one
that's independent for L and R.

This is the single biggest unlock. It promotes "which controller is
drifting" from unanswerable to answerable.

Schema impact: one or two new columns (`constellation_pos_xyz`,
`constellation_rot_xyzw`) populated on `anchor_baseline` snapshots. Bump
`schema_version`.

### 2. Manual marker event ("I just did a thing")

A controller button (one of the side buttons not already mapped) that
emits a `session_event` with `subtype=manual_marker` and a short
`detail` string when pressed. The experimenter taps it the instant they
tip a tripod, change a parameter, notice misalignment, etc.

This makes Test 5's tipping protocol dramatically cleaner — instead of
reconstructing tip timing from velocity spikes (which works but is
noisy), you have ground truth. It also helps any future test where the
experimenter is doing something the log can't see directly.

Schema impact: zero — uses existing `session_event` infrastructure.

### 3. Configurable pulse profile from inspector / runtime toggle

The `ControllerSleepMitigation` component currently has hardcoded
amplitude (0.02), duration (50ms), and cadence (5s). Exposing these in
the inspector — and ideally a runtime hotkey to cycle between a few
preset profiles mid-session — lets Test 4 become an A/B over pulse
strength without rebuilding the APK between conditions.

Bonus version: log the active profile parameters on each
`sleep_event pulse` row so the analysis can correlate pulse strength
with effect even within a single session.

### 4. Single-controller velocity logging at higher rate

Current logging samples controller velocity at the 5 Hz snapshot rate.
For Test 5's spike analysis, a higher-rate (e.g., 30+ Hz) sample of
controller velocity during the seconds around a marker event would
give a cleaner picture of the spike shape and the deviation response.

Could be implemented as a per-frame ring buffer that dumps to the log
on marker events (similar to the Phase 2 snap-event ring buffer pattern
already in the architecture).

### 5. Optional: structured test-mode scene flow

A "characterization mode" entry that prompts the experimenter through
the protocols in order — initial baseline capture, 2-min still window,
walking, then a tip sequence (L, wait, R, wait, both), then a long
still window for Test 7, then end. Each phase auto-emits a
`session_event` marking the phase boundary.

This is the most ambitious of these and probably not worth it unless
you find yourself running the same protocol repeatedly. Manual + the
marker event from (2) gets you most of the way there with much less
effort.

---

## Bumping schema / changing what's logged

If during analysis you find that an additional column would meaningfully
help (e.g., HMD angular velocity, raw IMU-frame controller motion,
optical-vs-IMU weight from the fusion filter if it's ever exposed), don't
hack a workaround — note it for the next schema version. Bump
`schema_version` in `SessionLoggerSchema.md`, add the column to the
schema doc, and update the main `LogAnalysisHandoff.md` accordingly.
Doc drift is silent breakage.
