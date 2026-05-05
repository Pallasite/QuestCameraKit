using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Corrects spatial anchor drift over time by comparing observed AprilTag
/// positions against their calibrated ground-truth positions.
///
/// Architecture:
/// - All virtual content that needs drift correction is parented under a
///   "correction root" transform that this component controls.
/// - Each frame, detected AprilTag positions are compared to their known
///   ground-truth positions. The difference is the measured drift.
/// - An exponential moving average (EMA) filter smooths the drift correction
///   to prevent jitter while still tracking slow drift over time.
///
/// Usage:
/// 1. Place AprilTags at known, fixed positions in your physical space.
/// 2. Measure their real-world positions and register them via RegisterGroundTruth().
/// 3. Parent all drift-sensitive virtual objects under CorrectionRoot.
/// 4. Call ApplyObservation() each frame with detected tag poses.
/// </summary>
public class SpatialAnchorDriftCorrector : MonoBehaviour
{
    [Tooltip("Transform that parents all virtual objects needing drift correction. " +
             "If not assigned, this component's transform is used.")]
    [SerializeField] private Transform correctionRoot;

    [Tooltip("EMA smoothing factor (0-1). Lower = smoother but slower to respond. " +
             "0.05 is good for slow spatial anchor drift. 0.2 for faster correction.")]
    [Range(0.01f, 1f)]
    [SerializeField] private float smoothingFactor = 0.1f;

    [Tooltip("Maximum allowed correction magnitude in meters. " +
             "Observations exceeding this are treated as outliers and ignored.")]
    [SerializeField] private float maxCorrectionMeters = 0.5f;

    [Tooltip("Maximum allowed rotation correction in degrees.")]
    [SerializeField] private float maxCorrectionDegrees = 15f;

    [Tooltip("Minimum number of observations before corrections are applied.")]
    [SerializeField] private int minObservationsBeforeCorrection = 3;

    // Ground truth: tag ID -> known world position and rotation
    private readonly Dictionary<int, Pose> _groundTruth = new();

    // Running EMA state
    private Vector3 _emaPositionOffset = Vector3.zero;
    private Quaternion _emaRotationOffset = Quaternion.identity;
    private int _observationCount;
    private bool _initialized;

    /// <summary>
    /// The transform being adjusted to correct drift. Parent your virtual
    /// content under this transform.
    /// </summary>
    public Transform CorrectionRoot => correctionRoot ? correctionRoot : transform;

    /// <summary>
    /// Current drift correction offset being applied (for debugging/visualization).
    /// </summary>
    public Vector3 CurrentPositionCorrection => _emaPositionOffset;
    public Quaternion CurrentRotationCorrection => _emaRotationOffset;
    public int ObservationCount => _observationCount;

    private void Awake()
    {
        if (correctionRoot == null)
        {
            correctionRoot = transform;
        }
    }

    /// <summary>
    /// Registers the known real-world position and rotation of an AprilTag.
    /// Call this during calibration or load from a saved configuration.
    /// </summary>
    /// <param name="tagId">AprilTag ID (from tagStandard41h12 family).</param>
    /// <param name="worldPose">The tag's measured real-world pose.</param>
    public void RegisterGroundTruth(int tagId, Pose worldPose)
    {
        _groundTruth[tagId] = worldPose;
        Debug.Log($"[DriftCorrector] Registered ground truth for tag {tagId} at {worldPose.position}");
    }

    /// <summary>
    /// Removes a ground truth registration.
    /// </summary>
    public void UnregisterGroundTruth(int tagId)
    {
        _groundTruth.Remove(tagId);
    }

    /// <summary>
    /// Clears all ground truth data and resets the correction state.
    /// </summary>
    public void Reset()
    {
        _groundTruth.Clear();
        _emaPositionOffset = Vector3.zero;
        _emaRotationOffset = Quaternion.identity;
        _observationCount = 0;
        _initialized = false;
        ApplyCorrection();
    }

    /// <summary>
    /// Processes a set of detected AprilTag observations against ground truth.
    /// Call this every frame with the latest detection results.
    /// </summary>
    /// <param name="observations">Array of detected AprilTag results with world-space poses.</param>
    public void ApplyObservations(AprilTagResult[] observations)
    {
        if (observations == null || observations.Length == 0) return;

        var positionAccum = Vector3.zero;
        var validCount = 0;
        var rotationAccum = Quaternion.identity;

        foreach (var obs in observations)
        {
            if (!_groundTruth.TryGetValue(obs.tagId, out var truth)) continue;

            // Compute observed world position from the camera-space detection
            var camPose = obs.cameraPose;
            var observedWorldPos = camPose.position + camPose.rotation * obs.localPosition;
            var observedWorldRot = camPose.rotation * obs.localRotation;

            // Drift = where we expected the tag - where we see it
            var positionDrift = truth.position - observedWorldPos;
            var rotationDrift = truth.rotation * Quaternion.Inverse(observedWorldRot);

            // Outlier rejection
            if (positionDrift.magnitude > maxCorrectionMeters)
            {
                Debug.LogWarning($"[DriftCorrector] Tag {obs.tagId} position drift " +
                    $"{positionDrift.magnitude:F3}m exceeds threshold, skipping.");
                continue;
            }

            if (Quaternion.Angle(Quaternion.identity, rotationDrift) > maxCorrectionDegrees)
            {
                Debug.LogWarning($"[DriftCorrector] Tag {obs.tagId} rotation drift " +
                    $"exceeds threshold, skipping.");
                continue;
            }

            positionAccum += positionDrift;
            // Average rotations using Slerp accumulation
            if (validCount == 0)
            {
                rotationAccum = rotationDrift;
            }
            else
            {
                rotationAccum = Quaternion.Slerp(rotationAccum, rotationDrift, 1f / (validCount + 1));
            }
            validCount++;
        }

        if (validCount == 0) return;

        // Average the position drift across all valid observations
        var avgPositionDrift = positionAccum / validCount;

        _observationCount++;

        // Apply EMA filter
        if (!_initialized)
        {
            _emaPositionOffset = avgPositionDrift;
            _emaRotationOffset = rotationAccum;
            _initialized = true;
        }
        else
        {
            _emaPositionOffset = Vector3.Lerp(_emaPositionOffset, avgPositionDrift, smoothingFactor);
            _emaRotationOffset = Quaternion.Slerp(_emaRotationOffset, rotationAccum, smoothingFactor);
        }

        // Only apply after minimum observations to avoid initial noise
        if (_observationCount >= minObservationsBeforeCorrection)
        {
            ApplyCorrection();
        }
    }

    /// <summary>
    /// Applies the accumulated correction to the correction root transform.
    /// </summary>
    private void ApplyCorrection()
    {
        if (!CorrectionRoot) return;
        CorrectionRoot.localPosition = _emaPositionOffset;
        CorrectionRoot.localRotation = _emaRotationOffset;
    }

    /// <summary>
    /// Starts a calibration sequence: detects a tag and registers its current
    /// observed world position as ground truth.
    /// </summary>
    /// <param name="tagId">The tag ID to calibrate.</param>
    /// <param name="observedWorldPose">The tag's currently observed world pose.</param>
    public void CalibrateTag(int tagId, Pose observedWorldPose)
    {
        RegisterGroundTruth(tagId, observedWorldPose);
        Debug.Log($"[DriftCorrector] Calibrated tag {tagId} at position {observedWorldPose.position}");
    }
}
