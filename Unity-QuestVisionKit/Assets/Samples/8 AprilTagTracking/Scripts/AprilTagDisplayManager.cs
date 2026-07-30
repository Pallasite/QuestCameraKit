using System;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR;

/// <summary>
/// Manages the 3D visualization of detected AprilTags by converting
/// camera-space poses to world-space using the camera's world pose at
/// time of capture.
///
/// Mirrors QrCodeDisplayManager's architecture but uses the AprilTag
/// library's direct pose estimation (position + rotation in camera space)
/// rather than UV-corner raycasting, providing sub-centimeter accuracy.
///
/// Supports two placement modes:
/// - Direct: Uses the AprilTag library's pose directly (faster, works without MRUK)
/// - EnvironmentRaycast: Refines placement by raycasting onto the scene mesh (more stable)
/// </summary>
public class AprilTagDisplayManager : MonoBehaviour
{
    public enum PlacementMode
    {
        Direct,
        EnvironmentRaycast
    }

    /// <summary>
    /// Holds a detected tag's world-space pose after camera transform.
    /// SizeMeters is the RAW stereo-triangulated mean edge length (captured
    /// before any solver rescales or rebuilds the corners — the stereo
    /// scale-error diagnostic); 0 when only an inspector-set size is
    /// available (monocular).
    /// </summary>
    public struct TagWorldPose
    {
        public int TagId;
        public Vector3 Position;
        public Quaternion Rotation;
        public float SizeMeters;

        // Diagnostics for the solver-comparison experiment. SolverUsed records
        // which RotationSolver mode ACTUALLY produced this pose (size-aware
        // modes degrade to Kabsch when the tag size is unset and are stamped
        // as such); CornerResidualMeters is the RMS distance between the
        // corners the solver consumed and the rigid template at the fitted
        // pose (post-rescale for KabschRescaledRadial). Both default to
        // (NaiveCross, 0) for monocular scanners that don't populate the
        // AprilTagResult diagnostics.
        public StereoAprilTagScanner.RotationSolver SolverUsed;
        public float CornerResidualMeters;
    }

    public enum MarkerDisplayMode
    {
        /// <summary>Legacy / debug — a marker cube tracks every detection batch.</summary>
        Always,
        /// <summary>
        /// Gate on the single/double-tag placement flow: markers show while the
        /// obstacle is NOT yet placed (Setup), then hide so raw unsmoothed cubes
        /// don't jitter in the participant's view during walks. Mirrors
        /// AprilTagWireframeVisualizer's mode of the same name.
        /// </summary>
        DuringPlacementSetup,
        /// <summary>Never show marker cubes.</summary>
        Never,
    }

    [SerializeField] private PlacementMode placementMode = PlacementMode.Direct;

    [Tooltip("Visual scale multiplier for marker display (does not affect pose accuracy).")]
    [SerializeField] private float markerDisplayScale = 1f;

    [Tooltip("Explicit marker pool reference. Falls back to MarkerPool.Instance if not set.")]
    [SerializeField] private MarkerPool markerPool;

    [Tooltip("When marker cubes are shown. Default Always keeps sample scenes unchanged; the " +
             "experiment scenes use DuringPlacementSetup — markers update at the raw scan rate " +
             "with no smoothing, so they visibly jitter and don't belong in a participant's view.")]
    [SerializeField] private MarkerDisplayMode markerDisplayMode = MarkerDisplayMode.Always;

    [Tooltip("Used by MarkerDisplayMode.DuringPlacementSetup: markers show while this placement " +
             "controller has no placed obstacle. Auto-resolved (scene-wide) if left null; when " +
             "none exists the mode falls back to always-on (fail-visible, like the wireframe).")]
    [SerializeField] private ObstaclePlacementController placementGate;

    [Header("Proximity gate (skip per-frame scan when far from any reference tag)")]
    [Tooltip("Optional ConstellationDriftCorrector reference used to gate per-frame scans by distance " +
             "to the calibrated reference tags. Auto-resolved from the same GameObject if left null. " +
             "When unset and no fallback found, the gate is disabled (always scan, legacy behavior).")]
    [SerializeField] private ConstellationDriftCorrector proximityGate;

    [Tooltip("Skip the per-frame ScanFrameAsync call when the camera is farther than this from every " +
             "calibrated reference tag. AprilTag pose error ramps quickly past ~2 m at the configured " +
             "size; cheap to lower without losing usable detections.")]
    [SerializeField] private float maxScanDistanceMeters = 2.5f;

    [Tooltip("Hysteresis: once out of range, must come within (max - hysteresis) to re-enable scanning. " +
             "Prevents the gate from rapidly flickering on/off at the boundary.")]
    [SerializeField] private float scanDistanceHysteresisMeters = 0.5f;

    [Tooltip("Always scan during a streaming-calibration sweep, regardless of distance. The sweep " +
             "explicitly wants to see every tag the headset can find, not just nearby ones.")]
    [SerializeField] private bool gateBypassDuringStreaming = true;

    [Header("Scan throttle (perf — stereo capture + AprilTag detect runs per scan, not free)")]
    [Tooltip("Master toggle. When false, the per-frame scan is skipped entirely; existing markers " +
             "stay frozen at their last detected positions. ConstellationDriftCorrector's anchor " +
             "stops receiving updates but doesn't break. Useful for A/B perf testing or freezing " +
             "the world for participant runs.")]
    [SerializeField] private bool enableScanning = true;

    [Tooltip("Maximum scan rate in Hz. Default 5Hz matches the downstream consistency-window " +
             "cadence (ConstellationDriftCorrector needs 5 consistent frames before applying a " +
             "correction, so faster scanning gives no extra correction quality). Lower for less " +
             "compute; raise toward 30+Hz to verify per-frame cost. Bypassed during a " +
             "streaming-calibration sweep, which always runs full-rate.")]
    [SerializeField, Range(0.5f, 90f)] private float scanRateHz = 5f;

    [Header("Distance gate to the last-seen tag (single/double-tag flow)")]
    [Tooltip("Skip scans when the camera is farther than (tag size x scanCutoffTagMultiples) from " +
             "the last detected tag position. OFF by default (sample scenes unchanged); the " +
             "experiment's ScanProfilePolicy turns it on during trials. Inactive until the first " +
             "detection — the tag has to be found before there is a position to gate against.")]
    [SerializeField] private bool distanceGateToLastTag = false;

    [Tooltip("Cutoff distance in tag-size multiples — DYNAMIC: editing the scanner's tag size " +
             "rescales the cutoff automatically. Pose quality is a function of pixels-on-tag: " +
             "translation error ~1% of distance out to ~5x the tag side, orientation degrades " +
             "steeply past ~10x, detection dies around ~15x at passthrough resolution. Default 15 " +
             "(~2.6 m for the 0.171 m tag) = beyond detection usefulness, so gated scans lose " +
             "nothing.")]
    [SerializeField, Range(3f, 30f)] private float scanCutoffTagMultiples = 15f;

    /// <summary>
    /// Fired each frame that tags are detected, after world poses are computed.
    /// Subscribe from additional visualizers (e.g. AprilTagWireframeVisualizer)
    /// to receive results without running a second scan.
    /// </summary>
    public event Action<TagWorldPose[]> OnTagsDetected;

    private const int MaxConsecutiveErrors = 10;

    private IAprilTagScanner _scanner;
    private EnvironmentRaycastManager _envRaycastManager;
    private Transform _cameraTransform;
    private readonly Dictionary<int, MarkerController> _activeMarkers = new();
    private readonly List<int> _keysToRemove = new();
    private bool _scanInProgress;
    private int _consecutiveErrors;
    private float _backoffUntil;

    // Proximity gate state. _inScanRange is the latched in/out value; we flip
    // it lazily based on hysteresis so the gate doesn't flicker at the boundary.
    private bool _inScanRange = true;

    // Scan throttle state. Updated each time a scan kicks off; checked at the
    // top of Update before any other gating, so we never even call ShouldScan
    // when we're throttling.
    private float _nextScanTime;

    // Last-seen-tag gate state (hysteresis-latched like the constellation gate).
    private Vector3? _lastKnownTagPos;
    private bool _inTagRange = true;

    // ---- runtime knobs (ScanProfilePolicy / web console) ----

    /// <summary>Per-frame scan rate in Hz (clamped to the Inspector range).</summary>
    public float ScanRateHz
    {
        get => scanRateHz;
        set => scanRateHz = Mathf.Clamp(value, 0.5f, 90f);
    }

    /// <summary>Enable/disable the last-seen-tag distance gate.</summary>
    public bool DistanceGateToLastTag
    {
        get => distanceGateToLastTag;
        set => distanceGateToLastTag = value;
    }

    /// <summary>Computed gate cutoff in meters: scanner tag size x multiplier.</summary>
    public float ScanCutoffMeters
        => (_scanner != null ? _scanner.TagSizeMeters : 0.171f) * scanCutoffTagMultiples;

    /// <summary>True while the distance gate is actively suppressing scans.</summary>
    public bool ScanGatedByDistance
        => distanceGateToLastTag && _lastKnownTagPos.HasValue && !_inTagRange;

    /// <summary>The active scanner (stereo preferred), for profile/config access.</summary>
    public IAprilTagScanner Scanner => _scanner;

    private void Awake()
    {
        // Prefer a stereo scanner if present (it triangulates and avoids the
        // single-camera depth bias that misaligns one eye); fall back to mono.
        // Use Unity's overloaded != null (not ??) so destroyed components are treated as null.
        var stereo = GetComponent<StereoAprilTagScanner>();
        _scanner = stereo != null ? (IAprilTagScanner)stereo : GetComponent<AprilTagScanner>();
        _envRaycastManager = GetComponent<EnvironmentRaycastManager>();

        if (!markerPool) markerPool = MarkerPool.Instance;
        if (!markerPool) Debug.LogWarning("[AprilTagDisplayManager] No MarkerPool assigned and no singleton found. Markers will not spawn.");

        // Auto-resolve the proximity gate from the same GameObject when not
        // wired explicitly. Matches the AprilTag stack's typical layout
        // (corrector + display manager on the same GameObject) so the gate
        // works zero-touch.
        if (!proximityGate) proximityGate = GetComponent<ConstellationDriftCorrector>();

        // Placement gate for MarkerDisplayMode.DuringPlacementSetup (scene-wide,
        // like the wireframe visualizer's equivalent field).
        if (!placementGate) placementGate = FindAnyObjectByType<ObstaclePlacementController>();
    }

    private void Update()
    {
        if (!enableScanning) return;
        if (_scanInProgress || Time.time < _backoffUntil) return;

        // Bypass the rate throttle during a streaming-calibration sweep — the
        // sweep wants every detection it can get, and gateBypassDuringStreaming
        // already covers the proximity gate during sweeps.
        bool inSweep = gateBypassDuringStreaming
                       && proximityGate != null
                       && proximityGate.IsStreamingCalibration;
        if (!inSweep && Time.time < _nextScanTime) return;

        if (!ShouldScanThisFrame()) return;
        if (!WithinLastTagRange()) return;
        _nextScanTime = Time.time + 1f / Mathf.Max(0.01f, scanRateHz);
        RefreshMarkers();
    }

    /// <summary>
    /// Last-seen-tag distance gate. True (scan) when the gate is off, no tag
    /// has ever been seen (must scan to find it), or the camera is within the
    /// cutoff. Hysteresis: drop out beyond cutoff + hysteresis, re-enter
    /// within cutoff — same latching as the constellation gate.
    /// </summary>
    private bool WithinLastTagRange()
    {
        if (!distanceGateToLastTag || !_lastKnownTagPos.HasValue) return true;

        if (!_cameraTransform)
        {
            var cam = Camera.main;
            if (!cam) return true;
            _cameraTransform = cam.transform;
        }

        float cutoff = ScanCutoffMeters;
        float dist = Vector3.Distance(_cameraTransform.position, _lastKnownTagPos.Value);
        if (_inTagRange)
        {
            if (dist > cutoff + scanDistanceHysteresisMeters) _inTagRange = false;
        }
        else
        {
            if (dist <= cutoff) _inTagRange = true;
        }
        return _inTagRange;
    }

    /// <summary>
    /// Proximity-gate check. Returns true (scan) if no gate is configured,
    /// if the corrector isn't calibrated yet (no reference to gate against),
    /// or if a streaming sweep is active (we want all detections regardless
    /// of distance). Otherwise applies hysteresis: re-enter when the camera
    /// gets within maxScanDistance, exit when it goes beyond max + hysteresis.
    /// </summary>
    private bool ShouldScanThisFrame()
    {
        if (proximityGate == null) return true;
        if (gateBypassDuringStreaming && proximityGate.IsStreamingCalibration) return true;
        if (!proximityGate.IsCalibrated) return true;

        if (!_cameraTransform)
        {
            var cam = Camera.main;
            if (!cam) return true;     // no camera reference yet; don't accidentally permanently gate off
            _cameraTransform = cam.transform;
        }

        var camPos = _cameraTransform.position;
        if (_inScanRange)
        {
            // Drop out only when beyond the outer (max + hysteresis) radius.
            if (!proximityGate.IsCameraWithinScanRange(camPos, maxScanDistanceMeters + scanDistanceHysteresisMeters))
            {
                _inScanRange = false;
            }
        }
        else
        {
            // Re-enter when within the inner (max) radius.
            if (proximityGate.IsCameraWithinScanRange(camPos, maxScanDistanceMeters))
            {
                _inScanRange = true;
            }
        }
        return _inScanRange;
    }

    private async void RefreshMarkers()
    {
        if (_scanner == null) return;
        _scanInProgress = true;

        AprilTagResult[] results;
        try
        {
            results = await _scanner.ScanFrameAsync();
        }
        catch (System.Exception ex)
        {
            _consecutiveErrors++;
            if (_consecutiveErrors >= MaxConsecutiveErrors)
            {
                // Exponential backoff: 1s, 2s, 4s... capped at 16s.
                var delay = Mathf.Min(16f, Mathf.Pow(2f, _consecutiveErrors - MaxConsecutiveErrors));
                _backoffUntil = Time.time + delay;
                Debug.LogError($"[AprilTagDisplayManager] {_consecutiveErrors} consecutive scan errors. " +
                               $"Backing off for {delay:F1}s. Last error: {ex.Message}");
            }
            else
            {
                Debug.LogError($"[AprilTagDisplayManager] Scan error ({_consecutiveErrors}/{MaxConsecutiveErrors}): {ex.Message}");
            }
            _scanInProgress = false;
            return;
        }

        _consecutiveErrors = 0;
        _scanInProgress = false;

        if (results == null || results.Length == 0)
        {
            CleanupInactiveMarkers();
            return;
        }

        // Build world poses and collect them for the event
        var worldPoses = new List<TagWorldPose>(results.Length);
        bool showMarkers = ShouldShowMarkers();

        foreach (var result in results)
        {
            if (!TryBuildWorldPose(result, out var worldPos, out var worldRot))
            {
                continue;
            }

            worldPoses.Add(new TagWorldPose
            {
                TagId = result.tagId,
                Position = worldPos,
                Rotation = worldRot,
                // Prefer the scanner's raw pre-mutation measurement: after the
                // rebuild/rescale modes run, observedCorners equal the rigid
                // template and measuring them just returns the configured size.
                SizeMeters = result.measuredTagSizeMeters > 0f
                    ? result.measuredTagSizeMeters
                    : MeasureTagSize(result.observedCorners),
                SolverUsed = result.solverUsed,
                CornerResidualMeters = result.cornerResidualMeters,
            });

            // Feed the last-seen-tag distance gate regardless of marker display.
            _lastKnownTagPos = worldPos;

            if (!showMarkers) continue;   // markers self-hide ~2 s after their last update

            var marker = GetOrCreateMarker(result.tagId);
            if (!marker) continue;

            var scale = new Vector3(markerDisplayScale, markerDisplayScale, 1f);
            marker.UpdateMarker(worldPos, worldRot, scale, $"Tag {result.tagId}");
        }

        // Notify any additional visualizers
        if (worldPoses.Count > 0)
        {
            OnTagsDetected?.Invoke(worldPoses.ToArray());
        }

        CleanupInactiveMarkers();
    }

    /// <summary>
    /// Converts the AprilTag's camera-space pose to world-space using
    /// the camera's world pose captured at detection time.
    /// </summary>
    private bool TryBuildWorldPose(AprilTagResult result, out Vector3 worldPos, out Quaternion worldRot)
    {
        worldPos = default;
        worldRot = default;

        var camPose = result.cameraPose;

        if (result.worldPoseOverride.HasValue)
        {
            var wp = result.worldPoseOverride.Value;
            worldPos = wp.position;
            worldRot = wp.rotation;
        }
        else
        {
            worldPos = camPose.position + camPose.rotation * result.localPosition;
            worldRot = camPose.rotation * result.localRotation;
        }

        if (placementMode == PlacementMode.EnvironmentRaycast && _envRaycastManager)
        {
            // Refine placement by raycasting toward the detected position
            var ray = new Ray(camPose.position, (worldPos - camPose.position).normalized);
            if (_envRaycastManager.Raycast(ray, out var hit))
            {
                worldPos = hit.point;
                // Keep the rotation from AprilTag pose (more accurate than surface normal alone)
                // but align the forward axis with the hit normal for stability
                var up = worldRot * Vector3.up;
                var forward = -hit.normal;
                if (Vector3.Dot(forward, worldRot * Vector3.forward) < 0)
                {
                    forward = hit.normal;
                }
                worldRot = Quaternion.LookRotation(forward, up);
            }
        }

        return true;
    }

    // Mean of the 4 triangulated edge lengths. Returns 0 when the scanner
    // didn't populate world corners (monocular path), letting consumers fall
    // back to a configured size.
    private static float MeasureTagSize(Vector3[] corners)
    {
        if (corners == null || corners.Length != 4) return 0f;
        var sum = Vector3.Distance(corners[0], corners[1])
                + Vector3.Distance(corners[1], corners[2])
                + Vector3.Distance(corners[2], corners[3])
                + Vector3.Distance(corners[3], corners[0]);
        return sum * 0.25f;
    }

    private bool ShouldShowMarkers()
    {
        switch (markerDisplayMode)
        {
            case MarkerDisplayMode.Never:
                return false;
            case MarkerDisplayMode.DuringPlacementSetup:
                // No placement controller -> fall back to always-on so markers
                // never silently vanish in a misconfigured scene.
                if (placementGate == null) return true;
                return !placementGate.IsPlaced;
            default:
                return true;
        }
    }

    private MarkerController GetOrCreateMarker(int tagId)
    {
        if (_activeMarkers.TryGetValue(tagId, out var marker))
        {
            return marker;
        }

        var markerGo = markerPool ? markerPool.GetMarker() : null;
        if (!markerGo) return null;

        marker = markerGo.GetComponent<MarkerController>();
        if (!marker) return null;

        _activeMarkers[tagId] = marker;
        return marker;
    }

    private void CleanupInactiveMarkers()
    {
        _keysToRemove.Clear();
        foreach (var kvp in _activeMarkers)
        {
            if (!kvp.Value || !kvp.Value.gameObject.activeSelf)
            {
                _keysToRemove.Add(kvp.Key);
            }
        }

        foreach (var key in _keysToRemove)
        {
            _activeMarkers.Remove(key);
        }
    }
}
