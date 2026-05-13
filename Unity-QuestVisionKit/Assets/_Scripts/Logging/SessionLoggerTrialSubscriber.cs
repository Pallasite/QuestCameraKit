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
        _currentCondition = condition;
        _currentWalkIndex = condition != null ? condition.TrialNumber : (_currentWalkIndex + 1);
        _walkStartSessionTime = SessionLogger.Instance != null ? SessionLogger.Instance.NowSession : 0.0;
        Emit("start", duration: null);
    }

    private void HandleObstacleMoved() => Emit("moved", duration: null);
    private void HandleObstacleReset() => Emit("reset", duration: null);

    private void HandleTrialCompleted()
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
