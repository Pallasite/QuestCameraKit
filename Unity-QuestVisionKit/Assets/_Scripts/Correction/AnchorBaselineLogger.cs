using UnityEngine;

/// <summary>
/// 5Hz <c>state_snapshot</c> logger for the always-on "anchor_baseline" source.
/// Records the uncorrected <see cref="OVRSpatialAnchor"/> world pose, the headset
/// pose, and (when controllers are within working range) the controller poses
/// plus rigid-body deviation. This is the implicit comparison source against
/// which Phase 2's controller-based corrections will be measured.
///
/// "Anchor baseline" = the bare OVRSpatialAnchor's world pose (not
/// <c>CorrectionRoot</c>'s — that's downstream of correction). The anchor itself
/// drifts naturally; CorrectionRoot is offset on top of it. We log the upstream
/// one here.
///
/// The 2m working-range gate for controllers is applied per-sample: if either
/// controller exceeds 2m from the headset, that side's pose / velocity / validity
/// cells are left empty for that row. This honors the spec's "sparse schema"
/// contract — empty cell = the source had nothing useful to say.
/// </summary>
[DisallowMultipleComponent]
public sealed class AnchorBaselineLogger : MonoBehaviour
{
    [Header("Wiring")]
    [Tooltip("Source of the OVRSpatialAnchor. Reads ConstellationAnchor.transform for the bare anchor pose.")]
    [SerializeField] private ConstellationDriftCorrector corrector;

    [SerializeField] private ControllerPoseProvider poseProvider;

    [Tooltip("Optional. Reads CurrentDeviationFromBaselineM/Deg to attach to each snapshot. Phase 1: logs only.")]
    [SerializeField] private ControllerRigidBodyValidator rigidBodyValidator;

    [Header("Cadence")]
    [Tooltip("Snapshot rate in Hz. 5Hz spec default (200ms interval).")]
    [SerializeField, Range(1f, 30f)] private float snapshotRateHz = 5f;

    [Header("Gates")]
    [Tooltip("Controllers beyond this distance from the headset have their pose cells left empty.")]
    [SerializeField] private float controllerWorkingRangeMeters = 2f;

    private Transform _headsetTransform;
    private float _nextSampleTime;
    private bool _haveSubscribed;

    private void Awake()
    {
        if (!corrector) corrector = FindObjectOfType<ConstellationDriftCorrector>();
        if (!poseProvider) poseProvider = FindObjectOfType<ControllerPoseProvider>();
        if (!rigidBodyValidator) rigidBodyValidator = FindObjectOfType<ControllerRigidBodyValidator>();
    }

    private void OnEnable()
    {
        TryResolveHeadset();
        // No need to subscribe to OnConstellationCalibrated — we read the anchor
        // transform each frame and gate snapshots on its existence, so we'll
        // naturally start emitting once calibration produces one.
    }

    private void Update()
    {
        if (Time.unscaledTime < _nextSampleTime) return;
        _nextSampleTime = Time.unscaledTime + 1f / Mathf.Max(0.1f, snapshotRateHz);

        if (SessionLogger.Instance == null) return;

        // Anchor: only emit a row once calibration has produced one.
        if (corrector == null || !corrector.IsCalibrated || corrector.ConstellationAnchor == null) return;

        if (_headsetTransform == null) TryResolveHeadset();

        var e = LogEvent.StateSnapshot("anchor_baseline");

        var anchorT = corrector.ConstellationAnchor.transform;
        e.AnchorPos = anchorT.position;
        e.AnchorRot = anchorT.rotation;

        if (_headsetTransform != null)
        {
            e.HeadsetPos = _headsetTransform.position;
            e.HeadsetRot = _headsetTransform.rotation;
        }

        if (poseProvider != null && _headsetTransform != null)
        {
            var headPos = _headsetTransform.position;
            float lDist = Vector3.Distance(poseProvider.LeftPose.position, headPos);
            float rDist = Vector3.Distance(poseProvider.RightPose.position, headPos);

            if (poseProvider.LeftConnected && lDist <= controllerWorkingRangeMeters)
            {
                e.ControllerLPos = poseProvider.LeftPose.position;
                e.ControllerLRot = poseProvider.LeftPose.rotation;
                e.PositionValidL = poseProvider.LeftPositionValid;
                e.OrientationValidL = poseProvider.LeftOrientationValid;
                e.ConnectedL = poseProvider.LeftConnected;
                e.VelocityLMps = poseProvider.LeftVelocity.magnitude;
            }
            if (poseProvider.RightConnected && rDist <= controllerWorkingRangeMeters)
            {
                e.ControllerRPos = poseProvider.RightPose.position;
                e.ControllerRRot = poseProvider.RightPose.rotation;
                e.PositionValidR = poseProvider.RightPositionValid;
                e.OrientationValidR = poseProvider.RightOrientationValid;
                e.ConnectedR = poseProvider.RightConnected;
                e.VelocityRMps = poseProvider.RightVelocity.magnitude;
            }

            // Rigid body fields — only meaningful when both sides are populated.
            if (rigidBodyValidator != null && poseProvider.LeftPositionValid && poseProvider.RightPositionValid)
            {
                e.InterControllerDistanceM = rigidBodyValidator.CurrentDistanceM;
                e.InterControllerRotationDeg = Quaternion.Angle(Quaternion.identity, rigidBodyValidator.CurrentRelativeRotation);
                if (rigidBodyValidator.HasBaseline)
                {
                    e.DeviationFromBaselineM = rigidBodyValidator.CurrentDeviationFromBaselineM;
                    e.DeviationFromBaselineDeg = rigidBodyValidator.CurrentDeviationFromBaselineDeg;
                    e.ValidationEnforced = rigidBodyValidator.ValidationEnforced;
                }
            }
        }

        SessionLogger.Instance.Enqueue(e);
    }

    private void TryResolveHeadset()
    {
        // Established pattern in this codebase: Camera.main is the headset eye anchor.
        // See ObstacleController:119, AprilTagAnchorManager:271.
        if (Camera.main != null) _headsetTransform = Camera.main.transform;
    }
}
