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
///
/// The rot-solver cycle is a read-side diagnostic (mirrors the web console's
/// cycleRotationSolver action — built for labs where the console is
/// unreachable).
///
/// Previous-trial has no chord (rarely needed mid-walk; misfire risk next to
/// Redo) — it lives on the web console (RemoteConsoleServer) only.
///
/// HAPTIC VOCABULARY — during Running the HUD is hidden, so for most of a
/// session these patterns are the operator's ONLY feedback channel. Rules:
/// the hold ramp and the confirmation buzz on the hand whose finger acts
/// (left-hand actions buzz LEFT, right-hand actions buzz RIGHT, session-level
/// actions buzz BOTH); pulse count separates same-hand actions (Redo = 2R,
/// Skip = 3R; Recapture = 3L); a REFUSED or FAILED action never gets a
/// success buzz — it gets one long low-frequency buzz ("nope"), and success
/// buzzes fire only AFTER the underlying call reports success. Placement
/// commit ("the obstacle actually appeared") is its own two long left pulses,
/// distinct from the light request-accepted tick. Pause = one long strong
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
    private enum HoldAction { None, Place, Recapture, StartTrials, Redo, CyclePreset, NextTrial }

    /// <summary>Which controller(s) a pattern plays on — the hand whose finger acts.</summary>
    private enum Hand { Left, Right, Both }

    [Header("Wiring (auto-resolved if empty)")]
    [SerializeField] private QuestControllerInput input;
    [SerializeField] private SessionFlowController flow;
    [SerializeField] private ObstaclePlacementController placement;
    [SerializeField] private SessionHUD hud;
    [SerializeField] private StereoAprilTagScanner stereoScanner;

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

    private void Awake()
    {
        if (!input) input = FindAnyObjectByType<QuestControllerInput>();
        if (!flow) flow = FindAnyObjectByType<SessionFlowController>();
        if (!placement) placement = FindAnyObjectByType<ObstaclePlacementController>();
        if (!hud) hud = FindAnyObjectByType<SessionHUD>();
        if (!stereoScanner) stereoScanner = FindAnyObjectByType<StereoAprilTagScanner>();
    }

    private void OnEnable()
    {
        // The real "placed" confirmation: CapturePlacement only ACCEPTS the
        // request (the stable capture lands async), so the commit tick must
        // not read as "obstacle placed" — this pattern does.
        if (placement != null) placement.OnPlaced += HandlePlaced;
    }

    private void OnDisable()
    {
        if (placement != null) placement.OnPlaced -= HandlePlaced;
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
            _ => false,
        };

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
