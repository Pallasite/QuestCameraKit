using System.Globalization;
using UnityEngine;

/// <summary>
/// In-headset experimenter control surface for the single/double-tag flow.
/// Phase-gated hold-to-confirm bindings that call the public APIs of
/// <see cref="SessionFlowController"/> and <see cref="ObstaclePlacementController"/>
/// (the same methods a future web console will call).
///
/// Bindings (finesse owns thumbsticks / L grip / A / B; R grip is the modifier):
///
///   HOLD L index trigger              Place the obstacle          [Setup]
///   HOLD R grip + L index trigger     Recapture (clear placement) [Ready]
///   HOLD both index triggers          Start trials                [Ready]
///   HOLD both index triggers          Reopen session (to Paused)  [Complete]
///   HOLD R index trigger              Redo current trial          [Running/Paused]
///   HOLD R grip + R index trigger     Skip to next trial          [Running/Paused]
///   HOLD R grip + Start (menu)        Cycle condition preset      [Setup/Ready/Paused]
///   PRESS Start (menu)                Pause / Resume              [Running/Paused]
///   PRESS R thumbstick click          Toggle diagnostics zone     [any]
///   PRESS R grip + Y (left)           Cycle AprilTag rot solver   [any]
///   PRESS R grip + X (left)           Open / close the trial picker [Ready/Paused]
///   HOLD X (left, picker open)        Commit the seek               [Ready/Paused]
///
/// The rot-solver cycle is a read-side diagnostic (mirrors the web console's
/// cycleRotationSolver action — built for labs where the console is
/// unreachable).
///
/// Previous-trial has no chord (rarely needed mid-walk; misfire risk next to
/// Redo) — it lives on the web console (RemoteConsoleServer) only.
///
/// TRIAL PICKER — an exclusive mode for seeking to an arbitrary trial before
/// trials start or while paused (single-step Skip covers ±1; this covers "go
/// to trial 17"). While open, this surface CLAIMS the left thumbstick from
/// ObstacleFinesseController (which otherwise nudges the obstacle with it in
/// exactly these phases) via its InputSuppressed flag, and releases it only
/// once the stick has re-centred — the fire/rearm latch in QuestControllerInput
/// is shared, so handing back a deflected stick would auto-repeat nudges into
/// the obstacle. L stick X steps ±1 trial, L stick Y ±5, both gap-correct and
/// clamped (never wrapping). Commit is HOLD X, deliberately NOT the R index
/// trigger: that is Redo while Paused, and the picker's likeliest exit
/// (Resume) blanks the HUD, so a stale-mode hold there would fire a real redo
/// unnoticed. Any phase change closes the picker with the refusal buzz.
///
/// HAPTIC VOCABULARY — during Running the HUD is hidden, so for most of a
/// session these patterns are the operator's ONLY feedback channel. Rules:
/// the hold ramp and the confirmation buzz on the hand whose finger acts
/// (left-hand actions buzz LEFT, right-hand actions buzz RIGHT, session-level
/// actions buzz BOTH); pulse count separates same-hand actions (Redo = 2R,
/// Skip = 3R; Recapture = 3L; seek commit = 1 long + 2 short L); a REFUSED or
/// FAILED action never gets a success buzz — it gets one long low-frequency
/// buzz ("nope"), and success buzzes fire only AFTER the underlying call
/// reports success. Placement commit ("the obstacle actually appeared") is
/// its own two long left pulses, distinct from the light request-accepted
/// tick. Pause = one long strong
/// buzz on both; Resume = two short on both. The clearance re-arm alert
/// (SessionFlowController) is three strong pulses on both. Full table in
/// Docs/OperatorQuickstart.md.
///
/// Hold mechanics: index-trigger presses group for a short window (so pressing
/// L then R lands on the both-index action instead of firing Place), then the
/// resolved action must be held ~0.9 s with escalating haptics on the acting
/// hand and a HUD progress bar; releasing early cancels with a tiny tick.
/// Actions attempted in the wrong phase get the refusal buzz + a HUD hint and
/// never start a hold. Destructive/committing actions are hold-only by design.
/// </summary>
[DisallowMultipleComponent]
public sealed class ExperimenterSessionControls : MonoBehaviour
{
    private enum HoldAction { None, Place, Recapture, StartTrials, Redo, CyclePreset, NextTrial, SeekTrial }

    /// <summary>Which controller(s) a pattern plays on — the hand whose finger acts.</summary>
    private enum Hand { Left, Right, Both }

    [Header("Wiring (auto-resolved if empty)")]
    [SerializeField] private QuestControllerInput input;
    [SerializeField] private SessionFlowController flow;
    [SerializeField] private ObstaclePlacementController placement;
    [SerializeField] private SessionHUD hud;
    [SerializeField] private StereoAprilTagScanner stereoScanner;
    [SerializeField] private ObstacleFinesseController finesse;

    [Header("Bindings")]
    [SerializeField] private OVRInput.Button modifier = OVRInput.Button.SecondaryHandTrigger;   // R grip
    [SerializeField] private OVRInput.Button leftIndex = OVRInput.Button.PrimaryIndexTrigger;
    [SerializeField] private OVRInput.Button rightIndex = OVRInput.Button.SecondaryIndexTrigger;
    [SerializeField] private OVRInput.Button menuButton = OVRInput.Button.Start;
    [SerializeField] private OVRInput.Button diagnosticsButton = OVRInput.Button.SecondaryThumbstick;

    [Tooltip("R grip + this cycles the AprilTag rotation solver. Y (Button.Four, left controller) " +
             "is bound to nothing else project-wide, so the chord can't collide — even if a " +
             "ConstellationDriftCorrector (which claims R-grip+A/B) returns to the scene.")]
    [SerializeField] private OVRInput.Button cycleSolverButton = OVRInput.Button.Four;

    [Tooltip("R grip + this opens/closes the trial picker; the SAME button held alone commits " +
             "the seek. X (Button.Three, left controller) is bound to nothing else project-wide. " +
             "Commit is deliberately NOT the R index trigger: that means Redo while Paused, and " +
             "the picker's most likely exit (Resume) blanks the HUD, so a stale-mode hold there " +
             "would fire a real redo with no way to notice.")]
    [SerializeField] private OVRInput.Button pickerButton = OVRInput.Button.Three;

    [Header("Hold tuning")]
    [Tooltip("Seconds a hold must be sustained to commit.")]
    [SerializeField, Range(0.3f, 3f)] private float holdSeconds = 0.9f;

    [Tooltip("Grouping window after the first index-trigger press, so L-then-R lands on the both-index action.")]
    [SerializeField, Range(0.05f, 0.4f)] private float chordGroupingSeconds = 0.15f;

    [Header("Feedback")]
    [SerializeField] private bool haptics = true;

    // ---- hold state ----
    private bool _pendingChord;          // inside the grouping window
    private float _pendingSince;
    private HoldAction _active = HoldAction.None;
    private float _holdStart;
    private bool _vibrating;
    private Coroutine _pattern;

    // ---- trial picker state ----
    private bool _pickerActive;
    private int _seekCandidate;
    private string _pickerNote;                 // composed INTO the picker row, never a transient
    private string _pickerRow;                  // rebuilt on candidate change, not per frame
    private bool _releasingStickClaim;          // suppression held until the L stick re-centres
    private QuestControllerInput.StickAxis _lockedAxis;
    private bool _axisLocked;
    private float _lastAxisFireTime;

    private void Awake()
    {
        if (!input) input = FindAnyObjectByType<QuestControllerInput>();
        if (!flow) flow = FindAnyObjectByType<SessionFlowController>();
        if (!placement) placement = FindAnyObjectByType<ObstaclePlacementController>();
        if (!hud) hud = FindAnyObjectByType<SessionHUD>();
        if (!stereoScanner) stereoScanner = FindAnyObjectByType<StereoAprilTagScanner>();
        if (!finesse) finesse = FindAnyObjectByType<ObstacleFinesseController>();
    }

    private void OnEnable()
    {
        // The real "placed" confirmation: CapturePlacement only ACCEPTS the
        // request (the stable capture lands async), so the commit tick must
        // not read as "obstacle placed" — this pattern does.
        if (placement != null) placement.OnPlaced += HandlePlaced;
        if (flow != null) flow.OnPhaseChanged += HandlePhaseChanged;
    }

    private void OnDisable()
    {
        if (placement != null) placement.OnPlaced -= HandlePlaced;
        if (flow != null) flow.OnPhaseChanged -= HandlePhaseChanged;
        if (input != null) input.OnStickFire -= HandlePickerStick;
        
        // Never strand the finesse controller if this surface is disabled mid-pick.
        _pickerActive = false;
        _releasingStickClaim = false;
        if (finesse != null) finesse.InputSuppressed = false;
    }

    private void HandlePlaced() => PlayPattern(Hand.Left, pulses: 2, amplitude: 0.8f, onSeconds: 0.12f, gapSeconds: 0.1f);

    private void Update()
    {
        if (input == null) return;

        // ---- simple press actions (no hold) ----
        bool mod = input.IsHeld(modifier);

        if (input.WasPressedThisFrame(diagnosticsButton) && !mod)
        {
            hud?.ToggleDiagnostics();
            PlayPattern(Hand.Right, pulses: 1, amplitude: 0.4f, onSeconds: 0.04f, gapSeconds: 0f);
        }

        // R grip + Y: cycle the AprilTag rotation solver. Ungated by phase —
        // it's a read-side diagnostic (matches the web console action) and the
        // lab network can make the console unreachable.
        if (input.WasPressedThisFrame(cycleSolverButton) && mod)
        {
            CycleRotationSolver();
        }

        if (input.WasPressedThisFrame(menuButton))
        {
            if (mod)
            {
                // R grip + Start = hold-to-cycle-preset; starts the hold below.
                TryBeginHold(HoldAction.CyclePreset);
            }
            else if (flow != null && flow.CanPauseResume)
            {
                // Pause halts a live trial loop with a participant mid-walkway —
                // the most safety-relevant action here, so its signature is the
                // loudest: one long strong buzz. Resume is two short ones.
                bool pausing = flow.Phase == SessionPhase.Running;
                if (flow.TogglePauseResume())
                {
                    if (pausing) PlayPattern(Hand.Both, pulses: 1, amplitude: 0.9f, onSeconds: 0.35f, gapSeconds: 0f, frequency: 0.5f);
                    else PlayPattern(Hand.Both, pulses: 2, amplitude: 0.6f, onSeconds: 0.06f, gapSeconds: 0.08f);
                }
                else
                {
                    RefusalBuzz(Hand.Both);
                }
            }
            else
            {
                Hint("Pause is available once trials are running");
                RefusalBuzz(Hand.Both);
            }
        }

        // ---- trial picker ----
        // Dispatched outside the index-trigger grouping (like CyclePreset), so
        // both index triggers stay free while the picker is open.
        if (input.WasPressedThisFrame(pickerButton))
        {
            if (mod) TogglePicker();
            else if (_pickerActive) TryBeginHold(HoldAction.SeekTrial);
            // X alone outside the picker is inert by design - no refusal, no hold.
        }

        TickPicker();

        // ---- index-trigger chord grouping ----
        bool lPressedNow = input.WasPressedThisFrame(leftIndex);
        bool rPressedNow = input.WasPressedThisFrame(rightIndex);

        if (_active == HoldAction.None && !_pendingChord && (lPressedNow || rPressedNow))
        {
            _pendingChord = true;
            _pendingSince = Time.time;
        }

        if (_pendingChord && (Time.time - _pendingSince >= chordGroupingSeconds))
        {
            _pendingChord = false;
            ResolveChord();
        }

        // ---- active hold ----
        if (_active != HoldAction.None) TickHold();
    }

    private void ResolveChord()
    {
        bool l = input.IsHeld(leftIndex);
        bool r = input.IsHeld(rightIndex);
        bool mod = input.IsHeld(modifier);

        if (l && r) TryBeginHold(HoldAction.StartTrials);
        else if (l && mod) TryBeginHold(HoldAction.Recapture);
        else if (l) TryBeginHold(HoldAction.Place);
        else if (r && mod) TryBeginHold(HoldAction.NextTrial);
        else if (r) TryBeginHold(HoldAction.Redo);
        // Released within the grouping window: treat as an aborted tap, no action.
    }

    private void TryBeginHold(HoldAction action)
    {
        if (_active != HoldAction.None) return;

        if (!IsActionAllowed(action, out string denyHint))
        {
            Hint(denyHint);
            RefusalBuzz(HandFor(action));
            return;
        }

        _active = action;
        _holdStart = Time.time;

        if (action == HoldAction.Place) placement?.BeginPlacementPreview();
    }

    private void TickHold()
    {
        if (!ChordStillHeld(_active))
        {
            CancelHold("released");
            return;
        }

        float t = (Time.time - _holdStart) / Mathf.Max(0.1f, holdSeconds);
        hud?.ShowHoldProgress(Label(_active), t);

        if (haptics)
        {
            // Ramp only the acting hand — with the HUD hidden mid-session,
            // WHERE the buzz is is what tells Redo (R) from Recapture (L)
            // from Start (both) before the operator has committed anything.
            float amp = t < 0.33f ? 0.15f : t < 0.66f ? 0.35f : 0.6f;
            SetVibration(HandFor(_active), amp);
            _vibrating = true;
        }

        if (t >= 1f) CommitHold();
    }

    private void CancelHold(string reason)
    {
        var action = _active;
        _active = HoldAction.None;
        if (action == HoldAction.Place) placement?.EndPlacementPreview();
        StopVibration();
        Hint($"{Label(action)} cancelled ({reason})");
        // Tiny tick ≠ refusal buzz: "you let go" vs "the system said no".
        PlayPattern(HandFor(action), pulses: 1, amplitude: 0.2f, onSeconds: 0.03f, gapSeconds: 0f);
    }

    private void CommitHold()
    {
        var action = _active;
        _active = HoldAction.None;
        StopVibration();

        // Phase may have changed mid-hold; re-check before acting.
        if (!IsActionAllowed(action, out string denyHint))
        {
            if (action == HoldAction.Place) placement?.EndPlacementPreview();
            Hint(denyHint);
            RefusalBuzz(HandFor(action));
            return;
        }

        // Dispatch FIRST; confirm only what actually happened. Every callee
        // returns whether it acted (and HUDs its own reason when it refused).
        bool ok = action switch
        {
            // Ghost stays visible until the stable capture lands (PlaceInitial ends it).
            HoldAction.Place => placement != null && placement.CapturePlacement(),
            HoldAction.Recapture => placement != null && placement.Recapture(),
            HoldAction.StartTrials => flow != null &&
                (flow.Phase == SessionPhase.Complete ? flow.LeaveComplete() : flow.StartTrials()),
            HoldAction.Redo => flow != null && flow.RedoTrial(),
            HoldAction.CyclePreset => placement != null && placement.CyclePreset(),
            HoldAction.NextTrial => flow != null && flow.NextTrial(),
            HoldAction.SeekTrial => flow != null && flow.SeekToTrial(_seekCandidate),
            _ => false,
        };


        // The picker is a one-shot mode: whatever the seek did, it is over.
        // Closed AFTER the dispatch so Label() could still read the candidate.
        if (action == HoldAction.SeekTrial) ClosePicker(exitBuzz: false);
        if (ok) PlaySuccessPattern(action);
        else RefusalBuzz(HandFor(action));
    }

    private bool ChordStillHeld(HoldAction action)
    {
        bool l = input.IsHeld(leftIndex);
        bool r = input.IsHeld(rightIndex);
        bool mod = input.IsHeld(modifier);
        return action switch
        {
            HoldAction.Place => l && !r && !mod,
            HoldAction.Recapture => l && !r && mod,
            HoldAction.StartTrials => l && r,
            HoldAction.Redo => r && !l && !mod,
            HoldAction.NextTrial => r && !l && mod,
            HoldAction.CyclePreset => input.IsHeld(menuButton) && mod,
            HoldAction.SeekTrial => input.IsHeld(pickerButton) && !mod,
            _ => false,
        };
    }

    private bool IsActionAllowed(HoldAction action, out string denyHint)
    {
        denyHint = null;
        if (flow == null) { denyHint = "Session flow missing from scene"; return false; }

        switch (action)
        {
            case HoldAction.Place:
                if (flow.CanPlace) return true;
                denyHint = placement != null && placement.IsPlaced
                    ? "Already placed — R-grip + L trigger to re-place"
                    : "Placement not available now";
                return false;
            case HoldAction.Recapture:
                if (flow.CanRecapture) return true;
                denyHint = "Re-place is available before trials start";
                return false;
            case HoldAction.StartTrials:
                // The both-index hold doubles as the exit from Complete (the
                // one phase that used to be terminal): reopens Paused.
                if (flow.CanStartTrials || flow.CanLeaveComplete) return true;
                denyHint = flow.Phase == SessionPhase.Setup
                    ? "Place the obstacle first"
                    : "Trials already started";
                return false;
            case HoldAction.Redo:
                if (flow.CanRedo) return true;
                denyHint = "Redo is available once trials are running";
                return false;
            case HoldAction.NextTrial:
                if (flow.CanRedo) return true;    // same phase gate as Redo (Running/Paused)
                denyHint = "Trial navigation is available once trials are running";
                return false;
            case HoldAction.CyclePreset:
                if (flow.CanChangeConfig) return true;
                denyHint = "Pause first to change the condition";
                return false;
            case HoldAction.SeekTrial:
                if (flow.CanSeekTrial) return true;
                denyHint = "Trial picker works when Ready or Paused";
                return false;
            default:
                return false;
        }
    }

    private string Label(HoldAction a) => a switch
    {
        HoldAction.Place => "Place obstacle",
        HoldAction.Recapture => "Re-place",
        HoldAction.StartTrials => flow != null && flow.Phase == SessionPhase.Complete
            ? "Reopen session" : "Start trials",
        HoldAction.Redo => "Redo trial",
        HoldAction.CyclePreset => "Change condition",
        HoldAction.NextTrial => "Next trial",
        // Carries the candidate: the hold bar outranks the picker row, so without
        // this the commit would blank the one number being confirmed.
        HoldAction.SeekTrial => $"Go to trial {_seekCandidate}",
        _ => "",
    };

    // Mirrors RemoteConsoleServer's cycleRotationSolver action: cycle, HUD
    // transient, haptic confirm (double on the LEFT — Y is a left-controller
    // button), and the config_change join-key event so analysts can attribute
    // apriltag_solver_comparison.csv rows.
    private void CycleRotationSolver()
    {
        if (stereoScanner == null)
        {
            Hint("Stereo scanner missing from scene");
            RefusalBuzz(Hand.Left);
            return;
        }
        var next = stereoScanner.CycleSolver();
        hud?.ShowTransient($"Rot solver: {next}", 3f);
        PlayPattern(Hand.Left, pulses: 2, amplitude: 0.4f, onSeconds: 0.05f, gapSeconds: 0.07f);
        SessionLogger.Instance?.Enqueue(LogEvent.SessionEvent(
            "config_change",
            $"rot_solver={next};tag_size_m={stereoScanner.TagSizeMeters.ToString("F3", CultureInfo.InvariantCulture)};reason=set_rot_solver"));
    }


    // ---- trial picker ----------------------------------------------------
    //
    // A small exclusive mode: while it is open this surface owns the left
    // thumbstick (the finesse controller normally nudges the obstacle with it,
    // and is live in exactly these phases), and X commits. Everything else -
    // pause, start trials, place - keeps working.

    private void TogglePicker()
    {
        if (_pickerActive) { ClosePicker(exitBuzz: false); return; }

        if (flow == null || !flow.CanSeekTrial)
        {
            Hint("Trial picker works when Ready or Paused");
            RefusalBuzz(Hand.Left);
            return;
        }
        if (flow.TrialCount <= 0)
        {
            Hint("No trial CSV loaded");
            RefusalBuzz(Hand.Left);
            return;
        }

        _pickerActive = true;
        _releasingStickClaim = false;
        _axisLocked = false;
        _seekCandidate = flow.CurrentTrialNumber;
        _pickerNote = null;
        RebuildPickerRow();

        if (finesse != null) finesse.InputSuppressed = true;
        if (input != null) input.OnStickFire += HandlePickerStick;

        // Two soft left taps = "the sticks are mine now".
        PlayPattern(Hand.Left, pulses: 2, amplitude: 0.4f, onSeconds: 0.05f, gapSeconds: 0.07f);
    }

    private void ClosePicker(bool exitBuzz)
    {
        if (!_pickerActive) return;
        _pickerActive = false;
        _axisLocked = false;
        if (input != null) input.OnStickFire -= HandlePickerStick;

        // Hold the stick claim until the stick re-centres. The fire/rearm latch
        // lives in QuestControllerInput and is shared by all subscribers, so a
        // stick still deflected past the threshold is already auto-repeating at
        // 5 Hz - handing it straight back would walk the obstacle while the
        // operator is still looking at the trial number.
        _releasingStickClaim = finesse != null;
        if (_releasingStickClaim) TryReleaseStickClaim();

        // Killed rather than committed, and the HUD may already be dark (the
        // likeliest exit is Resume, which blanks the canvas): make it audible.
        if (exitBuzz) RefusalBuzz(Hand.Left);
    }

    private void HandlePhaseChanged(SessionPhase prev, SessionPhase next)
    {
        if (_pickerActive) ClosePicker(exitBuzz: true);
    }

    private void TickPicker()
    {
        if (_releasingStickClaim) TryReleaseStickClaim();
        if (!_pickerActive) return;

        // Phase gate can lapse without a transition event only if flow went
        // missing; cheap re-check keeps the mode honest.
        if (flow == null || !flow.CanSeekTrial) { ClosePicker(exitBuzz: true); return; }

        hud?.ShowTrialPicker(_pickerRow);
    }

    private void TryReleaseStickClaim()
    {
        if (input == null || finesse == null) { _releasingStickClaim = false; return; }
        if (input.LeftStick.magnitude >= input.StickRearmThreshold) return;
        finesse.InputSuppressed = false;
        _releasingStickClaim = false;
    }

    // L stick X = +/-1 trial, L stick Y = +/-5. Both step over CSV numbering
    // gaps via TryGetAdjacentTrial rather than arithmetic, and never wrap: one
    // extra auto-repeat past the last trial would otherwise land on the first,
    // and a committed seek there restarts the protocol.
    private void HandlePickerStick(QuestControllerInput.StickAxis axis, int sign)
    {
        if (!_pickerActive) return;
        if (axis != QuestControllerInput.StickAxis.LeftX && axis != QuestControllerInput.StickAxis.LeftY) return;

        // Dominant-axis lock. Every axis is thresholded independently at 0.7,
        // so a 45-degree push (0.707, 0.707) fires X and Y in the SAME frame -
        // silently stepping 4 or 6 instead of 1 or 5. First axis to fire wins
        // until it goes quiet for longer than the 200 ms auto-repeat interval.
        if (_axisLocked && axis != _lockedAxis)
        {
            if (Time.time - _lastAxisFireTime < 0.3f) return;
            _axisLocked = false;
        }
        _lockedAxis = axis;
        _axisLocked = true;
        _lastAxisFireTime = Time.time;

        int steps = axis == QuestControllerInput.StickAxis.LeftX ? 1 : 5;
        int candidate = _seekCandidate;
        int moved = 0;
        for (int i = 0; i < steps; i++)
        {
            if (!flow.TryGetAdjacentTrial(candidate, sign, out int next)) break;
            candidate = next;
            moved++;
        }

        if (moved == 0)
        {
            _pickerNote = sign > 0 ? "end of CSV" : "start of CSV";
            RebuildPickerRow();
            // Tiny tick, not the refusal buzz: nothing was refused, the list ended.
            PlayPattern(Hand.Left, pulses: 1, amplitude: 0.2f, onSeconds: 0.03f, gapSeconds: 0f);
            return;
        }

        _seekCandidate = candidate;
        _pickerNote = null;
        RebuildPickerRow();
        PlayPattern(Hand.Left, pulses: 1, amplitude: 0.25f, onSeconds: 0.03f, gapSeconds: 0f);
    }

    // Rebuilt on change, not per frame: PositionOf is an O(n) scan and the HUD
    // pushes at 6.7 Hz. Messages compose INTO this row - a ShowTransient call
    // would be masked by the picker's own per-frame push anyway.
    private void RebuildPickerRow()
    {
        int pos = Mathf.Clamp(flow.TrialPositionOf(_seekCandidate), 1, Mathf.Max(1, flow.TrialCount));
        string note = _pickerNote != null ? $" — <color={ExperimentPalette.MidHex}>{_pickerNote}</color>" : "";
        _pickerRow = $"<b>Go to trial {_seekCandidate}</b> ({pos}/{flow.TrialCount}) · stick to change · HOLD X to confirm{note}";
    }
    private void Hint(string msg)
    {
        if (!string.IsNullOrEmpty(msg)) hud?.ShowTransient(msg, 2.5f);
    }

    // ---- haptics ----

    private static Hand HandFor(HoldAction a) => a switch
    {
        HoldAction.Place => Hand.Left,        // L index
        HoldAction.Recapture => Hand.Left,    // R grip modifies, L index acts
        HoldAction.StartTrials => Hand.Both,
        HoldAction.Redo => Hand.Right,        // R index
        HoldAction.NextTrial => Hand.Right,   // R grip + R index
        HoldAction.CyclePreset => Hand.Both,
        HoldAction.SeekTrial => Hand.Left,    // X is a left-controller button
        _ => Hand.Both,
    };

    /// <summary>Success signatures: count + hand per action (see class docs).</summary>
    private void PlaySuccessPattern(HoldAction action)
    {
        switch (action)
        {
            case HoldAction.Place:
                // Light "request accepted" tick; the real placed confirmation
                // is HandlePlaced's two long left pulses when the capture lands.
                PlayPattern(Hand.Left, pulses: 1, amplitude: 0.6f, onSeconds: 0.06f, gapSeconds: 0f);
                break;
            case HoldAction.Recapture:
                PlayPattern(Hand.Left, pulses: 3, amplitude: 0.7f, onSeconds: 0.05f, gapSeconds: 0.07f);
                break;
            case HoldAction.StartTrials:
                PlayPattern(Hand.Both, pulses: 2, amplitude: 0.9f, onSeconds: 0.09f, gapSeconds: 0.09f);
                break;
            case HoldAction.Redo:
                PlayPattern(Hand.Right, pulses: 2, amplitude: 0.7f, onSeconds: 0.05f, gapSeconds: 0.07f);
                break;
            case HoldAction.NextTrial:
                PlayPattern(Hand.Right, pulses: 3, amplitude: 0.7f, onSeconds: 0.05f, gapSeconds: 0.07f);
                break;
            case HoldAction.CyclePreset:
                PlayPattern(Hand.Both, pulses: 3, amplitude: 0.5f, onSeconds: 0.05f, gapSeconds: 0.09f);
                break;
            case HoldAction.SeekTrial:
                // One long then two short, on the LEFT: must not be mistakable
                // for Redo (2 short right) or Next trial (3 short right), its
                // neighbours in the same phase.
                PlaySeekPattern();
                break;
        }
    }

    /// <summary>One long low-frequency buzz: refused or failed. Never confused
    /// with a success pattern (those are short, full-frequency, 1-3 pulses).</summary>
    private void RefusalBuzz(Hand hand)
        => PlayPattern(hand, pulses: 1, amplitude: 0.35f, onSeconds: 0.4f, gapSeconds: 0f, frequency: 0.3f);

    private void PlayPattern(Hand hand, int pulses, float amplitude, float onSeconds, float gapSeconds, float frequency = 1f)
    {
        if (!haptics) return;
        if (_pattern != null) StopCoroutine(_pattern);
        _pattern = StartCoroutine(PatternCo(hand, pulses, amplitude, onSeconds, gapSeconds, frequency));
    }


    /// <summary>Seek confirm: one long left pulse then two short ones. A single
    /// coroutine because PlayPattern cancels whatever is already running.</summary>
    private void PlaySeekPattern()
    {
        if (!haptics) return;
        if (_pattern != null) StopCoroutine(_pattern);
        _pattern = StartCoroutine(SeekPatternCo());
    }

    private System.Collections.IEnumerator SeekPatternCo()
    {
        SetVibration(Hand.Left, 0.85f);
        _vibrating = true;
        yield return new WaitForSecondsRealtime(0.22f);
        StopVibration();
        yield return new WaitForSecondsRealtime(0.12f);
        for (int i = 0; i < 2; i++)
        {
            SetVibration(Hand.Left, 0.7f);
            _vibrating = true;
            yield return new WaitForSecondsRealtime(0.05f);
            StopVibration();
            if (i == 0) yield return new WaitForSecondsRealtime(0.07f);
        }
        _pattern = null;
    }
    private System.Collections.IEnumerator PatternCo(Hand hand, int pulses, float amplitude, float onSeconds, float gapSeconds, float frequency)
    {
        for (int i = 0; i < pulses; i++)
        {
            SetVibration(hand, amplitude, frequency);
            _vibrating = true;
            yield return new WaitForSecondsRealtime(onSeconds);
            StopVibration();
            if (i < pulses - 1) yield return new WaitForSecondsRealtime(gapSeconds);
        }
        _pattern = null;
    }

    private static void SetVibration(Hand hand, float amplitude, float frequency = 1f)
    {
        if (hand != Hand.Right) OVRInput.SetControllerVibration(frequency, amplitude, OVRInput.Controller.LTouch);
        if (hand != Hand.Left) OVRInput.SetControllerVibration(frequency, amplitude, OVRInput.Controller.RTouch);
    }

    private void StopVibration()
    {
        if (!_vibrating) return;
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.LTouch);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
        _vibrating = false;
    }
}
