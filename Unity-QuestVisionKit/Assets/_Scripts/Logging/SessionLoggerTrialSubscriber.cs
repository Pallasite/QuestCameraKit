using UnityEngine;

/// <summary>
/// Bridges the existing trial pipeline (<see cref="TrialSequencer"/>,
/// <see cref="ObstacleController"/>) to <see cref="SessionLogger"/>.
///
/// A trial IS a walk: 1 trial = 1 walk. <see cref="TrialCondition"/> describes
/// what happens during that particular walk. This subscriber emits
/// <c>walk_event</c> rows at the natural trial lifecycle boundaries:
///   - <c>walk_phase=start</c> on <see cref="TrialSequencer.OnTrialLoaded"/>
///   - <c>walk_phase=moved</c> on <see cref="ObstacleController.OnObstacleMoved"/>
///   - <c>walk_phase=reset</c> on <see cref="ObstacleController.OnObstacleReset"/>
///   - <c>walk_phase=end</c> on <see cref="ObstacleController.OnTrialCompleted"/> with duration
///
/// The cumulative-correction columns (`corrections_applied_count`,
/// `max_correction_magnitude_m`, `rejection_reason_histogram`) are left empty in
/// Phase 1 because no correction source is acting yet. Phase 2's
/// `ControllerDriftCorrector` will populate them via a parallel subscriber.
///
/// Keeps <see cref="ObstacleController"/> and <see cref="TrialSequencer"/>
/// themselves untouched.
/// </summary>
[DisallowMultipleComponent]
public sealed class SessionLoggerTrialSubscriber : MonoBehaviour
{
    [Tooltip("Auto-resolved via FindObjectOfType if left empty.")]
    [SerializeField] private TrialSequencer trialSequencer;

    [Tooltip("Auto-resolved via FindObjectOfType if left empty.")]
    [SerializeField] private ObstacleController obstacleController;

    private TrialCondition _currentCondition;
    private int _currentWalkIndex = -1;
    private double _walkStartSessionTime;

    // True while a started walk has not yet had its "end" row emitted.
    //
    // Why: OnTrialCompleted has two independent subscribers — this component
    // (emits the end row) and TrialSequencer (advances, which synchronously
    // fires OnTrialLoaded). Their invocation order depends on cross-GameObject
    // OnEnable order, which Unity does not define. If the sequencer's handler
    // runs first, OnTrialLoaded would overwrite _currentCondition /
    // _walkStartSessionTime BEFORE the end row is emitted, stamping it with the
    // NEXT trial's index and ~0 duration. The pending flag makes the end row
    // order-robust: whoever observes the boundary first emits it with the
    // previous walk's data.
    private bool _endPending;

    // Set when HandleTrialLoaded consumed a completion (sequencer's handler ran
    // first and advanced before our HandleTrialCompleted was invoked). Our own
    // completion handler then skips exactly one callback, instead of emitting a
    // bogus ~0-duration end row for the freshly-started walk.
    private bool _completionConsumed;

    private void Awake()
    {
        if (!trialSequencer) trialSequencer = FindObjectOfType<TrialSequencer>();
        if (!obstacleController) obstacleController = FindObjectOfType<ObstacleController>();
    }

    private void OnEnable()
    {
        if (trialSequencer != null) trialSequencer.OnTrialLoaded += HandleTrialLoaded;
        if (obstacleController != null)
        {
            obstacleController.OnObstacleMoved += HandleObstacleMoved;
            obstacleController.OnObstacleReset += HandleObstacleReset;
            obstacleController.OnTrialCompleted += HandleTrialCompleted;
        }
    }

    private void OnDisable()
    {
        if (trialSequencer != null) trialSequencer.OnTrialLoaded -= HandleTrialLoaded;
        if (obstacleController != null)
        {
            obstacleController.OnObstacleMoved -= HandleObstacleMoved;
            obstacleController.OnObstacleReset -= HandleObstacleReset;
            obstacleController.OnTrialCompleted -= HandleTrialCompleted;
        }
    }

    private void HandleTrialLoaded(TrialCondition condition)
    {
        int newIndex = condition != null ? condition.TrialNumber : (_currentWalkIndex + 1);

        // If the previous walk's end row is still pending AND this load is the
        // natural next trial, the sequencer's completion handler ran before
        // ours: emit the end row now, with the PREVIOUS walk's condition/index/
        // timing, before adopting the new trial. A same-index reload (redo of a
        // fouled walk) or a manual jump does NOT get an end row — "end" keeps
        // meaning "walk completed". (A repeated start row for the same index is
        // the redo signature in the data.)
        if (_endPending && newIndex == _currentWalkIndex + 1)
        {
            EmitEnd();
            _completionConsumed = true;
        }
        _endPending = false;

        _currentCondition = condition;
        _currentWalkIndex = newIndex;
        _walkStartSessionTime = SessionLogger.Instance != null ? SessionLogger.Instance.NowSession : 0.0;
        Emit("start", duration: null);
        _endPending = true;
    }

    private void HandleObstacleMoved() => Emit("moved", duration: null);
    private void HandleObstacleReset() => Emit("reset", duration: null);

    private void HandleTrialCompleted()
    {
        // This completion was already consumed by HandleTrialLoaded (the
        // sequencer's handler ran first and advanced before we were invoked):
        // the end row is out; _endPending now belongs to the NEW walk.
        if (_completionConsumed)
        {
            _completionConsumed = false;
            return;
        }
        if (!_endPending) return;
        EmitEnd();
        _endPending = false;
    }

    private void EmitEnd()
    {
        float duration = 0f;
        if (SessionLogger.Instance != null)
            duration = (float)(SessionLogger.Instance.NowSession - _walkStartSessionTime);
        Emit("end", duration);
    }

    private void Emit(string phase, float? duration)
    {
        if (SessionLogger.Instance == null) return;
        var e = LogEvent.WalkEvent(_currentWalkIndex, phase);
        if (_currentCondition != null)
        {
            e.TrialActive = _currentCondition.IsActive;
            e.MoveTowardsUser = _currentCondition.MoveTowardsUser;
            e.TriggerDistanceM = _currentCondition.TriggerDistance;
            e.PerturbationDistanceM = _currentCondition.PerturbationDistance;
        }
        if (duration.HasValue) e.WalkDurationS = duration.Value;
        // Phase 1: corrections_applied_count, max_correction_magnitude_m,
        // rejection_reason_histogram left null. Phase 2 ControllerDriftCorrector
        // will populate them via its own end-of-walk hook.
        SessionLogger.Instance.Enqueue(e);
    }
}
