using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Captures the rigid-body baseline of the two controllers seated in the rig
/// (inter-controller distance + relative rotation) and computes per-frame
/// deviation from that baseline. In Phase 1 the validation toggle is OFF
/// (log-only) so the deviation distribution can be characterized before any
/// gate enforces a tolerance.
///
/// Phase 2's <c>ControllerDriftCorrector</c> consumes <see cref="ValidationEnforced"/>
/// and <see cref="CurrentInTolerance"/> as one of its gate inputs.
///
/// Baseline capture: <see cref="baselineSampleCount"/> samples over
/// <see cref="baselineCaptureSeconds"/> via coroutine. Mean+stddev of distance,
/// and a reference orientation + deviation-angle stddev for rotation (a true
/// quaternion mean is overkill at the expected sub-degree spread).
///
/// Tolerances default to 5mm / 2deg per the spec — expect to relax to ~1cm /
/// ~5deg in practice once each controller's independent tracking noise compounds.
/// </summary>
[DisallowMultipleComponent]
public sealed class ControllerRigidBodyValidator : MonoBehaviour
{
    [Header("Wiring")]
    [SerializeField] private ControllerPoseProvider provider;

    [Header("Calibration")]
    [SerializeField, Range(5, 200)] private int baselineSampleCount = 30;
    [SerializeField, Range(0.1f, 5f)] private float baselineCaptureSeconds = 1f;

    [Header("Tolerances")]
    [Tooltip("Distance from baseline mean considered out-of-tolerance (meters). Spec starts at 5mm; expect to relax.")]
    [SerializeField] private float distanceToleranceMeters = 0.005f;

    [Tooltip("Rotation deviation from baseline considered out-of-tolerance (degrees). Spec starts at 2 deg.")]
    [SerializeField] private float rotationToleranceDegrees = 2f;

    [Header("Behavior")]
    [Tooltip("Phase 1 default = false (log-only). Phase 2 ControllerDriftCorrector will respect this.")]
    [SerializeField] private bool useRigidBodyValidation = false;

    // ---- baseline state ----
    public bool HasBaseline { get; private set; }
    public float BaselineMeanDistanceM { get; private set; }
    public float BaselineStddevDistanceM { get; private set; }
    public Quaternion BaselineRelativeRotation { get; private set; } = Quaternion.identity;
    public float BaselineMeanRotDeg { get; private set; }
    public float BaselineStddevRotDeg { get; private set; }

    // ---- per-frame state ----
    public float CurrentDistanceM { get; private set; }
    public Quaternion CurrentRelativeRotation { get; private set; } = Quaternion.identity;
    public float CurrentDeviationFromBaselineM { get; private set; }
    public float CurrentDeviationFromBaselineDeg { get; private set; }
    public bool CurrentInTolerance { get; private set; } = true;
    public bool ValidationEnforced => useRigidBodyValidation;

    /// <summary>
    /// Fired at the end of a successful <see cref="CaptureBaselineCoroutine"/> run,
    /// after <see cref="HasBaseline"/> flips true and the calibration_event /
    /// session_event rows are enqueued. <see cref="ControllerDriftCorrector"/>
    /// (and the future mode coordinator) subscribe to this to re-capture their
    /// own references — when the rig is recalibrated, the controllers physically
    /// moved and any previously-captured midpoint reference is stale.
    /// </summary>
    public event Action OnBaselineCaptured;

    private bool _capturing;

    private void Awake()
    {
        if (!provider) provider = FindObjectOfType<ControllerPoseProvider>();
    }

    private void Update()
    {
        if (provider == null) return;
        if (!provider.LeftPositionValid || !provider.RightPositionValid) return;

        CurrentDistanceM = Vector3.Distance(provider.LeftPose.position, provider.RightPose.position);
        CurrentRelativeRotation = Quaternion.Inverse(provider.LeftPose.rotation) * provider.RightPose.rotation;

        if (HasBaseline)
        {
            CurrentDeviationFromBaselineM = Mathf.Abs(CurrentDistanceM - BaselineMeanDistanceM);
            CurrentDeviationFromBaselineDeg = Quaternion.Angle(CurrentRelativeRotation, BaselineRelativeRotation);
            CurrentInTolerance = CurrentDeviationFromBaselineM <= distanceToleranceMeters
                              && CurrentDeviationFromBaselineDeg <= rotationToleranceDegrees;
        }
        else
        {
            CurrentDeviationFromBaselineM = 0f;
            CurrentDeviationFromBaselineDeg = 0f;
            CurrentInTolerance = true;
        }
    }

    [ContextMenu("Capture Baseline Now")]
    public void CaptureBaselineNow() => StartCoroutine(CaptureBaselineCoroutine());

    public IEnumerator CaptureBaselineCoroutine()
    {
        if (_capturing)
        {
            Debug.LogWarning("[ControllerRigidBodyValidator] Capture already in progress.");
            yield break;
        }
        if (provider == null)
        {
            Debug.LogError("[ControllerRigidBodyValidator] No ControllerPoseProvider; can't capture.");
            yield break;
        }

        _capturing = true;
        try
        {
            float interval = baselineCaptureSeconds / Mathf.Max(1, baselineSampleCount);
            var distances = new List<float>(baselineSampleCount);
            var rotations = new List<Quaternion>(baselineSampleCount);

            if (SessionLogger.Instance != null)
                SessionLogger.Instance.Enqueue(LogEvent.CalibrationEvent("rigid_body_baseline_start"));

            for (int i = 0; i < baselineSampleCount; i++)
            {
                yield return new WaitForSeconds(interval);
                if (!provider.LeftPositionValid || !provider.RightPositionValid)
                {
                    if (SessionLogger.Instance != null)
                    {
                        var skipRow = LogEvent.CalibrationEvent("rigid_body_sample_invalid", sampleIndex: i);
                        SessionLogger.Instance.Enqueue(skipRow);
                    }
                    continue;
                }
                float d = Vector3.Distance(provider.LeftPose.position, provider.RightPose.position);
                Quaternion q = Quaternion.Inverse(provider.LeftPose.rotation) * provider.RightPose.rotation;
                distances.Add(d);
                rotations.Add(q);

                if (SessionLogger.Instance != null)
                {
                    var row = LogEvent.CalibrationEvent("rigid_body_sample", sampleIndex: i);
                    row.InterControllerDistanceM = d;
                    row.InterControllerRotationDeg = Quaternion.Angle(Quaternion.identity, q);
                    SessionLogger.Instance.Enqueue(row);
                }
            }

            if (distances.Count < 3)
            {
                Debug.LogError($"[ControllerRigidBodyValidator] Only {distances.Count} valid samples; aborting baseline.");
                if (SessionLogger.Instance != null)
                    SessionLogger.Instance.Enqueue(LogEvent.CalibrationEvent("rigid_body_baseline_failed"));
                yield break;
            }

            // Distance: classic mean + stddev.
            float dSum = 0f;
            foreach (var d in distances) dSum += d;
            float dMean = dSum / distances.Count;
            float dVar = 0f;
            foreach (var d in distances) { float diff = d - dMean; dVar += diff * diff; }
            float dStddev = Mathf.Sqrt(dVar / distances.Count);

            // Rotation: pick the first as the baseline orientation and measure
            // angle deviation from it for the spread. A true quaternion mean
            // is overkill given the <2deg expected spread.
            Quaternion rotRef = rotations[0];
            float rSum = 0f, rSumSq = 0f;
            foreach (var q in rotations)
            {
                float ang = Quaternion.Angle(rotRef, q);
                rSum += ang;
                rSumSq += ang * ang;
            }
            float rMean = rSum / rotations.Count;
            float rStddev = Mathf.Sqrt(Mathf.Max(0f, rSumSq / rotations.Count - rMean * rMean));

            BaselineMeanDistanceM = dMean;
            BaselineStddevDistanceM = dStddev;
            BaselineRelativeRotation = rotRef;
            BaselineMeanRotDeg = rMean;
            BaselineStddevRotDeg = rStddev;
            HasBaseline = true;

            if (SessionLogger.Instance != null)
            {
                var summary = LogEvent.CalibrationEvent("rigid_body_baseline_captured",
                    meanDistanceM: dMean, stddevDistanceM: dStddev,
                    meanRotDeg: rMean, stddevRotDeg: rStddev);
                SessionLogger.Instance.Enqueue(summary);

                var cfg = LogEvent.SessionEvent("rigid_body_baseline",
                    $"mean_distance_m={dMean:F4};stddev_distance_m={dStddev:F4};" +
                    $"mean_rot_deg={rMean:F3};stddev_rot_deg={rStddev:F3};" +
                    $"distance_tolerance_m={distanceToleranceMeters:F4};rotation_tolerance_deg={rotationToleranceDegrees:F3};" +
                    $"validation_enforced={(useRigidBodyValidation ? 1 : 0)};samples={distances.Count}");
                SessionLogger.Instance.Enqueue(cfg);
            }

            Debug.Log($"[ControllerRigidBodyValidator] Baseline: distance {dMean * 100f:F2}cm " +
                      $"(sd {dStddev * 1000f:F2}mm), rotation spread {rStddev:F3}deg over {distances.Count} samples.");

            // Fire after HasBaseline is true and logging is enqueued, so subscribers
            // see a fully-published baseline. Wrapped in try/catch so a misbehaving
            // listener can't roll back the capture.
            try { OnBaselineCaptured?.Invoke(); }
            catch (Exception ex) { Debug.LogError($"[ControllerRigidBodyValidator] OnBaselineCaptured listener threw: {ex}"); }
        }
        finally
        {
            _capturing = false;
        }
    }

    public void SetValidationEnforced(bool v) => useRigidBodyValidation = v;
}
