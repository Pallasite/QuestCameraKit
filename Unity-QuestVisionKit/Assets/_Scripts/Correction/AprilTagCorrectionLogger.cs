using UnityEngine;

/// <summary>
/// Parallel observer that logs AprilTag (ConstellationDriftCorrector) correction
/// events into the same session CSV that ControllerDriftCorrector writes to. Gives
/// offline analysis a side-by-side view of what each correction system did, on
/// the same frames.
///
/// <para>Pure listener — no modifications to <see cref="ConstellationDriftCorrector"/>
/// required. Subscribes to its existing
/// <c>OnCorrectionTriggered(Pose)</c> + <c>OnCorrectionRejected(string)</c>
/// events and emits <c>correction_event</c> rows with
/// <c>correction_source = "apriltag", mode = "applied"</c>.</para>
///
/// <para>Independent of <see cref="ControllerDriftCorrector"/> — they don't
/// share state, they just write parallel streams into the same log.</para>
///
/// <para>Emit cadence: roughly the AprilTag pipeline's own correction-trigger
/// rate (≪ render frame rate, gated by ConstellationDriftCorrector's
/// consistencyFrameCount + magnitude check + cooldownSeconds). Far sparser than
/// the controller corrector's per-frame stream.</para>
/// </summary>
[DisallowMultipleComponent]
public sealed class AprilTagCorrectionLogger : MonoBehaviour
{
    [Tooltip("AprilTag drift corrector to observe. Auto-resolved via FindAnyObjectByType.")]
    [SerializeField] private ConstellationDriftCorrector corrector;

    [Tooltip("Disables logging without removing the component.")]
    [SerializeField] private bool enableLogging = true;

    private bool _subscribed;

    private void Awake()
    {
        if (!corrector) corrector = FindAnyObjectByType<ConstellationDriftCorrector>(FindObjectsInactive.Include);
    }

    private void OnEnable()
    {
        if (!enableLogging) return;
        if (corrector == null)
        {
            Debug.LogWarning("[AprilTagCorrectionLogger] No ConstellationDriftCorrector found; logger inactive.");
            return;
        }
        Subscribe();
    }

    private void OnDisable() => Unsubscribe();

    private void Subscribe()
    {
        if (_subscribed) return;
        corrector.OnCorrectionTriggered += HandleTriggered;
        corrector.OnCorrectionRejected += HandleRejected;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        if (corrector != null)
        {
            corrector.OnCorrectionTriggered -= HandleTriggered;
            corrector.OnCorrectionRejected -= HandleRejected;
        }
        _subscribed = false;
    }

    private void HandleTriggered(Pose proposed)
    {
        if (SessionLogger.Instance == null) return;

        // Delta from the currently-applied correction (the lerp's source) to the
        // proposed correction (the lerp's target). ConstellationDriftCorrector
        // fires OnCorrectionTriggered before the lerp starts, so AppliedCorrection
        // still reflects the previous state.
        Pose currentApplied = corrector != null ? corrector.AppliedCorrection : Pose.identity;
        float deltaPos = Vector3.Distance(proposed.position, currentApplied.position);
        float deltaRot = Quaternion.Angle(proposed.rotation, currentApplied.rotation);

        var row = LogEvent.CorrectionEvent(
            correctionSource: "apriltag",
            mode: "applied",
            accepted: true,
            rejectionReason: null,
            deltaPositionM: deltaPos,
            deltaRotationDeg: deltaRot);
        SessionLogger.Instance.Enqueue(row);
    }

    private void HandleRejected(string reason)
    {
        if (SessionLogger.Instance == null) return;
        var row = LogEvent.CorrectionEvent(
            correctionSource: "apriltag",
            mode: "applied",
            accepted: false,
            rejectionReason: reason);
        SessionLogger.Instance.Enqueue(row);
    }
}
