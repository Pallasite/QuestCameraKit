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
///   HOLD R index trigger              Redo current trial          [Running/Paused]
///   HOLD R grip + Start (menu)        Cycle condition preset      [Setup/Ready/Paused]
///   PRESS Start (menu)                Pause / Resume              [Running/Paused]
///   PRESS R thumbstick click          Toggle diagnostics zone     [any]
///
/// Hold mechanics: index-trigger presses group for a short window (so pressing
/// L then R lands on the both-index action instead of firing Place), then the
/// resolved action must be held ~0.9 s with escalating haptics and a HUD
/// progress bar; releasing early cancels. Actions attempted in the wrong phase
/// show a HUD hint and never start a hold. Destructive/committing actions are
/// hold-only by design.
/// </summary>
[DisallowMultipleComponent]
public sealed class ExperimenterSessionControls : MonoBehaviour
{
    private enum HoldAction { None, Place, Recapture, StartTrials, Redo, CyclePreset }

    [Header("Wiring (auto-resolved if empty)")]
    [SerializeField] private QuestControllerInput input;
    [SerializeField] private SessionFlowController flow;
    [SerializeField] private ObstaclePlacementController placement;
    [SerializeField] private SessionHUD hud;

    [Header("Bindings")]
    [SerializeField] private OVRInput.Button modifier = OVRInput.Button.SecondaryHandTrigger;   // R grip
    [SerializeField] private OVRInput.Button leftIndex = OVRInput.Button.PrimaryIndexTrigger;
    [SerializeField] private OVRInput.Button rightIndex = OVRInput.Button.SecondaryIndexTrigger;
    [SerializeField] private OVRInput.Button menuButton = OVRInput.Button.Start;
    [SerializeField] private OVRInput.Button diagnosticsButton = OVRInput.Button.SecondaryThumbstick;

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

    private void Awake()
    {
        if (!input) input = FindAnyObjectByType<QuestControllerInput>();
        if (!flow) flow = FindAnyObjectByType<SessionFlowController>();
        if (!placement) placement = FindAnyObjectByType<ObstaclePlacementController>();
        if (!hud) hud = FindAnyObjectByType<SessionHUD>();
    }

    private void Update()
    {
        if (input == null) return;

        // ---- simple press actions (no hold) ----
        bool mod = input.IsHeld(modifier);

        if (input.WasPressedThisFrame(diagnosticsButton) && !mod)
        {
            hud?.ToggleDiagnostics();
            Pulse(0.4f, 0.04f);
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
                flow.TogglePauseResume();
                Pulse(0.5f, 0.06f);
            }
            else
            {
                Hint("Pause is available once trials are running");
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
        else if (r && mod) { /* reserved */ }
        else if (r) TryBeginHold(HoldAction.Redo);
        // Released within the grouping window: treat as an aborted tap, no action.
    }

    private void TryBeginHold(HoldAction action)
    {
        if (_active != HoldAction.None) return;

        if (!IsActionAllowed(action, out string denyHint))
        {
            Hint(denyHint);
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
            float amp = t < 0.33f ? 0.15f : t < 0.66f ? 0.35f : 0.6f;
            OVRInput.SetControllerVibration(1f, amp, OVRInput.Controller.LTouch);
            OVRInput.SetControllerVibration(1f, amp, OVRInput.Controller.RTouch);
            _vibrating = true;
        }

        if (t >= 1f) CommitHold();
    }

    private void CancelHold(string reason)
    {
        if (_active == HoldAction.Place) placement?.EndPlacementPreview();
        StopVibration();
        Hint($"{Label(_active)} cancelled ({reason})");
        _active = HoldAction.None;
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
            return;
        }

        DoublePulse();

        switch (action)
        {
            case HoldAction.Place:
                // Ghost stays visible until the stable capture lands (PlaceInitial ends it).
                placement?.CapturePlacement();
                break;
            case HoldAction.Recapture:
                placement?.Recapture();
                break;
            case HoldAction.StartTrials:
                flow?.StartTrials();
                break;
            case HoldAction.Redo:
                flow?.RedoTrial();
                break;
            case HoldAction.CyclePreset:
                placement?.CyclePreset();
                break;
        }
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
                if (flow.CanStartTrials) return true;
                denyHint = flow.Phase == SessionPhase.Setup
                    ? "Place the obstacle first"
                    : "Trials already started";
                return false;
            case HoldAction.Redo:
                if (flow.CanRedo) return true;
                denyHint = "Redo is available once trials are running";
                return false;
            case HoldAction.CyclePreset:
                if (flow.CanChangeConfig) return true;
                denyHint = "Pause first to change the condition";
                return false;
            default:
                return false;
        }
    }

    private static string Label(HoldAction a) => a switch
    {
        HoldAction.Place => "Place obstacle",
        HoldAction.Recapture => "Re-place",
        HoldAction.StartTrials => "Start trials",
        HoldAction.Redo => "Redo trial",
        HoldAction.CyclePreset => "Change condition",
        _ => "",
    };

    private void Hint(string msg)
    {
        if (!string.IsNullOrEmpty(msg)) hud?.ShowTransient(msg, 2.5f);
    }

    // ---- haptics ----

    private void Pulse(float amplitude, float seconds)
    {
        if (!haptics) return;
        OVRInput.SetControllerVibration(1f, amplitude, OVRInput.Controller.LTouch);
        OVRInput.SetControllerVibration(1f, amplitude, OVRInput.Controller.RTouch);
        _vibrating = true;
        CancelInvoke(nameof(StopVibration));
        Invoke(nameof(StopVibration), seconds);
    }

    private void DoublePulse()
    {
        Pulse(0.7f, 0.05f);
        Invoke(nameof(SecondPulse), 0.12f);
    }

    private void SecondPulse() => Pulse(0.7f, 0.05f);

    private void StopVibration()
    {
        if (!_vibrating) return;
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.LTouch);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
        _vibrating = false;
    }
}
