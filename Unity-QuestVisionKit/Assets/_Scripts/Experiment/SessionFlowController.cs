using System;
using UnityEngine;

/// <summary>Where the session currently is. Controls and HUD guidance gate on this.</summary>
public enum SessionPhase
{
    /// <summary>Calibrating: obstacle not yet placed (or placement cleared).</summary>
    Setup,
    /// <summary>Placed + trial data loaded; finesse-tune, then Start trials.</summary>
    Ready,
    /// <summary>Participant walks; trial loop armed and advancing.</summary>
    Running,
    /// <summary>Loop held (participant break); obstacle disarmed.</summary>
    Paused,
    /// <summary>All trials done.</summary>
    Complete,
}

/// <summary>
/// The session phase machine: Setup -> Ready -> Running -> Paused -> Complete.
/// Owns trial-loop arming EXCLUSIVELY (replaces <see cref="TrialLoopActivator"/>,
/// whose arm-on-placement behavior let proximity trials fire while the
/// experimenter was still finesse-tuning next to the obstacle).
///
/// Key rules:
///   - Ready requires placement AND trial data; Running requires an explicit
///     <see cref="StartTrials"/> (a deliberate hold on the controls surface).
///   - <see cref="RedoTrial"/> never leaves the obstacle armed while Paused, and
///     while Running it waits for the walker to clear the trigger radius before
///     re-arming (a redo with someone standing inside the radius would re-fire
///     the perturbation on the next frame).
///   - <c>OnSequenceComplete</c> is honored only in Running/Paused — a 1-based
///     participant CSV makes trial index 0 miss at boot, which fires
///     sequence-complete spuriously and must not brick the session.
///
/// Every transition is logged (`session_event subtype=phase_change`) so the CSV
/// carries the full session timeline. All actions are public methods — the
/// in-headset chords call them today; a web console can call them later.
/// </summary>
[DisallowMultipleComponent]
public sealed class SessionFlowController : MonoBehaviour
{
    [Header("Wiring (auto-resolved if empty)")]
    [SerializeField] private ObstaclePlacementController placement;
    [SerializeField] private TrialSequencer trialSequencer;
    [SerializeField] private ObstacleController obstacleController;
    [SerializeField] private TrialLoader trialLoader;

    [Header("Behavior")]
    [Tooltip("After a redo while Running, re-arm only once the walker is this far beyond the trigger radius (m).")]
    [SerializeField] private float redoClearanceMarginMeters = 0.5f;

    // ---- public surface (chords + future web console) ----

    public SessionPhase Phase { get; private set; } = SessionPhase.Setup;

    /// <summary>(previousPhase, newPhase) on every transition.</summary>
    public event Action<SessionPhase, SessionPhase> OnPhaseChanged;

    public bool CanPlace => Phase == SessionPhase.Setup;
    public bool CanRecapture => Phase == SessionPhase.Ready;
    public bool CanStartTrials => Phase == SessionPhase.Ready;
    public bool CanRedo => Phase == SessionPhase.Running || Phase == SessionPhase.Paused;
    public bool CanPauseResume => Phase == SessionPhase.Running || Phase == SessionPhase.Paused;
    public bool CanChangeConfig => Phase == SessionPhase.Setup || Phase == SessionPhase.Ready || Phase == SessionPhase.Paused;
    public bool IsPaused => Phase == SessionPhase.Paused;

    /// <summary>True while a Running-phase redo is waiting for the walker to clear the trigger radius.</summary>
    public bool WaitingForRedoClearance => _waitingForRedoClearance;

    private IHudTransientSink _hud;
    private bool _hudSearched;
    private bool _waitingForRedoClearance;
    private Transform _cameraRef;

    private void Awake()
    {
        if (!placement) placement = FindAnyObjectByType<ObstaclePlacementController>();
        if (!trialSequencer) trialSequencer = FindAnyObjectByType<TrialSequencer>();
        if (!obstacleController) obstacleController = FindAnyObjectByType<ObstacleController>();
        if (!trialLoader) trialLoader = FindAnyObjectByType<TrialLoader>();
    }

    private void OnEnable()
    {
        if (trialSequencer != null)
        {
            trialSequencer.OnTrialLoaded += HandleTrialLoaded;
            trialSequencer.OnSequenceComplete += HandleSequenceComplete;
        }
    }

    private void OnDisable()
    {
        if (trialSequencer != null)
        {
            trialSequencer.OnTrialLoaded -= HandleTrialLoaded;
            trialSequencer.OnSequenceComplete -= HandleSequenceComplete;
        }
    }

    private void Start()
    {
        Log("phase_change", $"from=boot;to={Phase};reason=scene_start");
    }

    private void Update()
    {
        switch (Phase)
        {
            case SessionPhase.Setup:
                if (ReadyConditionsMet()) TransitionTo(SessionPhase.Ready, "placed_and_data_loaded");
                break;

            case SessionPhase.Ready:
                // Recapture / config change cleared the placement -> back to Setup.
                if (placement != null && !placement.IsPlaced) TransitionTo(SessionPhase.Setup, "placement_cleared");
                break;

            case SessionPhase.Running:
                if (_waitingForRedoClearance && PlayerClearOfTrigger())
                {
                    _waitingForRedoClearance = false;
                    obstacleController?.ArmObstacle();
                    Hud("Walker clear — trial re-armed");
                }
                break;
        }
    }

    // ---- public actions ----

    /// <summary>Ready -> Running. The explicit gate that arms the trial loop.</summary>
    public void StartTrials()
    {
        if (Phase != SessionPhase.Ready)
        {
            Hud(Phase == SessionPhase.Setup ? "Place the obstacle first" : "Trials already started");
            return;
        }
        if (obstacleController == null) return;

        obstacleController.AutoReset = true;
        obstacleController.TrialSequenceActive = true;
        obstacleController.ArmObstacle();
        TransitionTo(SessionPhase.Running, "start_trials");
    }

    /// <summary>Running -> Paused: disarm so nothing triggers/resets/advances.</summary>
    public void Pause()
    {
        if (Phase != SessionPhase.Running) return;
        _waitingForRedoClearance = false;
        obstacleController?.DisarmObstacle();
        TransitionTo(SessionPhase.Paused, "pause");
    }

    /// <summary>Paused -> Running: re-arm.</summary>
    public void Resume()
    {
        if (Phase != SessionPhase.Paused) return;
        obstacleController?.ArmObstacle();
        TransitionTo(SessionPhase.Running, "resume");
    }

    public void TogglePauseResume()
    {
        if (Phase == SessionPhase.Running) Pause();
        else if (Phase == SessionPhase.Paused) Resume();
    }

    /// <summary>
    /// Re-run the current trial after a fouled walk. Safe under both phases:
    /// stays disarmed while Paused; while Running, waits for trigger-radius
    /// clearance before re-arming (see class docs).
    /// </summary>
    public void RedoTrial()
    {
        if (!CanRedo)
        {
            Hud("Redo is available once trials are running");
            return;
        }
        if (trialSequencer == null || obstacleController == null) return;

        int index = trialSequencer.CurrentTrialIndex;
        trialSequencer.RedoCurrentTrial();  // resets pivot + re-arms + reloads the same condition

        if (Phase == SessionPhase.Paused)
        {
            // ResetForRedo unconditionally re-armed; pause must stay disarmed.
            obstacleController.DisarmObstacle();
            Hud($"Trial {index} reset — still paused");
        }
        else if (!PlayerClearOfTrigger())
        {
            obstacleController.DisarmObstacle();
            _waitingForRedoClearance = true;
            Hud("Redo — waiting for walker to clear the obstacle");
        }
        else
        {
            Hud($"Trial {index} re-armed");
        }

        Log("trial_redo", $"index={index};phase={Phase}");
    }

    // ---- event handlers ----

    private void HandleTrialLoaded(TrialCondition condition)
    {
        // Per-trial re-arm, but only while actually Running and not holding for
        // redo clearance. (Setup/Ready/Paused loads must never arm.)
        if (Phase == SessionPhase.Running && !_waitingForRedoClearance)
        {
            obstacleController?.ArmObstacle();
        }
    }

    private void HandleSequenceComplete()
    {
        if (Phase == SessionPhase.Running || Phase == SessionPhase.Paused)
        {
            if (obstacleController != null)
            {
                obstacleController.DisarmObstacle();
                obstacleController.TrialSequenceActive = false;
            }
            _waitingForRedoClearance = false;
            TransitionTo(SessionPhase.Complete, "sequence_complete");
            Hud("<b>All trials complete</b> — session done");
        }
        else
        {
            // Boot-time fire = the loaded CSV has no trial at index 0 (likely
            // 1-based numbering). Warn loudly instead of silently completing.
            Debug.LogWarning("[SessionFlow] OnSequenceComplete outside Running/Paused — " +
                             "trial_conditions.csv probably has no trial number 0 (1-based file?). Ignored.");
            Hud("<color=#FF2FB9>Trial CSV has no trial 0 — check numbering</color>");
            Log("phase_change", $"from={Phase};to={Phase};reason=sequence_complete_ignored");
        }
    }

    // ---- internals ----

    private bool ReadyConditionsMet()
        => placement != null && placement.IsPlaced
           && trialLoader != null && !trialLoader.MissingData;

    private bool PlayerClearOfTrigger()
    {
        var pivot = obstacleController != null ? obstacleController.PerturbationPivot : null;
        var cam = CameraRef();
        if (pivot == null || cam == null) return true;   // nothing to guard against

        float trigger = trialSequencer != null && trialSequencer.CurrentTrial != null
            ? trialSequencer.CurrentTrial.TriggerDistance
            : 1f;

        // Same XZ-plane projection the ObstacleController's trigger check uses.
        Vector3 playerXZ = new Vector3(cam.position.x, pivot.position.y, cam.position.z);
        return Vector3.Distance(playerXZ, pivot.position) > trigger + redoClearanceMarginMeters;
    }

    private Transform CameraRef()
    {
        if (!_cameraRef && Camera.main) _cameraRef = Camera.main.transform;
        return _cameraRef;
    }

    private void TransitionTo(SessionPhase next, string reason)
    {
        if (next == Phase) return;
        var prev = Phase;
        Phase = next;
        Log("phase_change", $"from={prev};to={next};reason={reason}");
        Debug.Log($"[SessionFlow] {prev} -> {next} ({reason})");
        OnPhaseChanged?.Invoke(prev, next);
    }

    private static void Log(string subtype, string detail)
    {
        if (SessionLogger.Instance != null)
            SessionLogger.Instance.Enqueue(LogEvent.SessionEvent(subtype, detail));
    }

    private void Hud(string msg)
    {
        if (!_hudSearched) { _hud = HudSink.Find(); _hudSearched = true; }
        _hud?.ShowTransient(msg, 3f);
    }
}
