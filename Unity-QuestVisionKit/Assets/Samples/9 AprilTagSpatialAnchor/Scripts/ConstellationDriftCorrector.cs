using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Constellation-based drift corrector. Owns a single OVRSpatialAnchor created
/// at calibration time, with a CorrectionRoot transform parented under it; the
/// experiment's obstacle prefab lives under CorrectionRoot. Per-frame AprilTag
/// detections are compared (in anchor-local space) against the calibrated
/// reference constellation via RANSAC + Kabsch, and the resulting rigid
/// transform is lerped onto CorrectionRoot's local pose. Obstacles inherit the
/// correction as a rigid group, preserving the experimenter's hand-authored
/// offset across drift.
///
/// Replaces the legacy SpatialAnchorDriftCorrector (single-tag EMA approach).
/// Runs alongside AprilTagAnchorManager: that component continues to provide
/// per-tag anchor diagnostics, and both subscribe independently to
/// AprilTagDisplayManager.OnTagsDetected.
///
/// Two calibration modes:
///   1. Batch (Calibrate): one-shot heavy capture via ScanCalibrationAsync.
///      Requires all tags in a single camera frustum. Good when tags are
///      clustered and easily seen together.
///   2. Streaming (BeginStreamingCalibration / CommitStreamingCalibration):
///      accumulates per-tag observations from the live OnTagsDetected feed
///      while the experimenter sweeps the headset across the space.
///      Each tag just needs to be visible at *some* point during the sweep;
///      it does NOT need to share a frustum with the others. Brisk sweeps
///      (under ~20s) keep SLAM-frame drift between viewpoints negligible.
///
/// Steady state (after either mode commits): each detection batch produces a
/// candidate correction; after N consistent candidates and a magnitude check,
/// the correction is lerped onto CorrectionRoot.
///
/// All tuning constants are Inspector-exposed and will need calibration on
/// device.
/// </summary>
public class ConstellationDriftCorrector : MonoBehaviour
{
    [Header("Wiring")]
    [SerializeField] private AprilTagDisplayManager displayManager;
    [SerializeField] private StereoAprilTagScanner stereoScanner;
    [SerializeField] private GameObject anchoredObstaclePrefab;

    [Header("Calibration")]
    [Tooltip("Restrict the constellation to these tag IDs. Empty = use any visible tag.")]
    [SerializeField] private int[] allowedTagIds = Array.Empty<int>();

    [Tooltip("Minimum number of distinct tags required to commit a calibration (either mode).")]
    [SerializeField] private int minTagsForCalibration = 3;

    [Header("Batch calibration (Calibrate / ScanCalibrationAsync)")]
    [Tooltip("Number of frame pairs to capture during batch calibration (passed to ScanCalibrationAsync, " +
             "which overrides the scanner's own default). Default 8 matches the scanner's tuned default and " +
             "halves capture time vs. the previous 16. Variance scales as 1/sqrt(N), so 8 vs. 16 is only ~1.4x " +
             "more noise.")]
    [SerializeField] private int calibrationFrameCount = 8;

    [Tooltip("Auto-trigger Calibrate() once enough tags have been visible for several consecutive batches. " +
             "Off in production; useful for in-Editor testing without the UI panel.")]
    [SerializeField] private bool autoCalibrate = false;

    [SerializeField] private int autoCalibrateConsistentFrames = 5;

    [Header("Streaming calibration (BeginStreamingCalibration / CommitStreamingCalibration)")]
    [Tooltip("Minimum number of per-frame observations a tag must accumulate during the sweep " +
             "before it qualifies for the constellation. Higher = more averaging = less per-tag noise, " +
             "but the experimenter must dwell on each tag longer. Default 10 ≈ 1 s of dwell at 10 Hz " +
             "detection; the median noise floor drops as ~1/√N.")]
    [SerializeField] private int streamingMinObservationsPerTag = 10;

    [Tooltip("Safety timeout: if a streaming session runs longer than this without being committed " +
             "or cancelled, it auto-cancels and logs a warning. SLAM-frame drift across long captures " +
             "degrades the constellation, so prefer short brisk sweeps.")]
    [SerializeField] private float streamingMaxDurationSeconds = 60f;

    [Header("RANSAC + Kabsch")]
    [SerializeField] private float ransacInlierThresholdMeters = 0.005f;
    [SerializeField] private int ransacIterations = 32;
    [SerializeField] private float rotationGateDegrees = 10f;
    [SerializeField] private float residualRmsWarnMeters = 0.003f;

    [Header("Consistency gate (sliding window of candidate corrections)")]
    [SerializeField] private int consistencyFrameCount = 5;
    [SerializeField] private float consistencyTranslationMeters = 0.003f;
    [SerializeField] private float consistencyRotationDegrees = 1f;

    [Tooltip("Clear the consistency buffer if no detections are received for this long.")]
    [SerializeField] private float bufferStalenessSeconds = 0.5f;

    [Header("Trigger and apply")]
    [Tooltip("Smallest *incremental* correction (relative to currently-applied) that triggers an action.")]
    [SerializeField] private float driftTriggerThresholdMeters = 0.01f;

    [SerializeField] private float lerpDurationSeconds = 1f;
    [SerializeField] private float cooldownSeconds = 5f;

    [Header("Debug")]
    [SerializeField] private bool drawGizmos = true;
    [SerializeField] private bool verboseLogging = false;

    // ---- runtime state ----
    private OVRSpatialAnchor _anchor;
    private Transform _anchorTransform;
    private Transform _correctionRoot;
    private GameObject _obstacle;

    private readonly Dictionary<int, Pose> _referenceLocal = new();
    private readonly Queue<Pose> _candidateBuffer = new();

    // Pre-allocated collections to avoid per-frame GC pressure.
    private readonly List<Sample> _samples = new();
    private readonly List<int> _finalInliers = new();
    private readonly HashSet<int> _inlierSet = new();

    private Pose _appliedCorrection = Pose.identity;
    private Pose _lerpFrom;
    private Pose _lerpTo;
    private float _lerpStartTime;
    private bool _lerping;
    private float _cooldownUntil;
    private float _lastDetectionTime;

    private bool _isCalibrating;        // batch mode in-flight
    private int _autoCalibrateCounter;

    // Streaming-calibration state
    private bool _isStreamingCalibration;
    private float _streamingStartTime;
    private readonly Dictionary<int, List<Pose>> _streamingObservations = new();
    // Live count view exposed via StreamingObservationsPerTag.
    private readonly Dictionary<int, int> _streamingObservationCounts = new();

    private System.Random _rng;

    // gizmo state
    private readonly List<Vector3> _gizmoInliersWorld = new();
    private readonly List<Vector3> _gizmoOutliersWorld = new();
    private float _lastResidualRms;

    // ---- public API ----
    public bool IsCalibrated => _anchor != null && _correctionRoot != null;
    public Pose AppliedCorrection => _appliedCorrection;
    public Transform CorrectionRoot => _correctionRoot;
    public GameObject Obstacle => _obstacle;
    public OVRSpatialAnchor ConstellationAnchor => _anchor;
    public IReadOnlyDictionary<int, Pose> ReferenceConstellation => _referenceLocal;
    public float LastResidualRmsMeters => _lastResidualRms;

    public bool IsStreamingCalibration => _isStreamingCalibration;
    public float StreamingElapsedSeconds => _isStreamingCalibration ? Time.time - _streamingStartTime : 0f;

    /// <summary>
    /// Live per-tag observation counts for the active streaming session.
    /// Empty between sessions. Use for UI showing "tag 7: 12 obs, tag 8: 3 obs..."
    /// </summary>
    public IReadOnlyDictionary<int, int> StreamingObservationsPerTag => _streamingObservationCounts;

    public event Action OnConstellationCalibrated;
    public event Action<string> OnCalibrationFailed;
    /// <summary>Fired during batch calibration capture: (capturedFrames, totalFrames, uniqueTags).</summary>
    public event Action<int, int, int> OnCalibrationProgress;
    /// <summary>Fired each detection batch during a streaming session: (totalObservations, uniqueTags).</summary>
    public event Action<int, int> OnStreamingCalibrationProgress;
    public event Action<Pose> OnCorrectionTriggered;
    public event Action OnCorrectionCompleted;
    public event Action<string> OnCorrectionRejected;

    // ---- Unity lifecycle ----
    private void Awake()
    {
        if (!displayManager) displayManager = GetComponent<AprilTagDisplayManager>();
        if (!stereoScanner) stereoScanner = GetComponent<StereoAprilTagScanner>();
        _rng = new System.Random();
    }

    private void OnEnable()
    {
        if (!displayManager)
        {
            Debug.LogError("[ConstellationDriftCorrector] No AprilTagDisplayManager assigned/found. Disabling.");
            enabled = false;
            return;
        }
        if (!stereoScanner)
        {
            Debug.LogError("[ConstellationDriftCorrector] No StereoAprilTagScanner assigned/found. Calibration uses ScanCalibrationAsync which only the stereo scanner provides. Disabling.");
            enabled = false;
            return;
        }
        displayManager.OnTagsDetected += HandleTagsDetected;
    }

    private void OnDisable()
    {
        if (displayManager) displayManager.OnTagsDetected -= HandleTagsDetected;
    }

    private void Update()
    {
        UpdateLerp();

        if (_candidateBuffer.Count > 0
            && Time.time - _lastDetectionTime > bufferStalenessSeconds)
        {
            _candidateBuffer.Clear();
        }

        // Streaming session safety timeout. SLAM drift accumulates linearly with
        // session duration; cap it so a forgotten session doesn't silently produce
        // a degraded calibration.
        if (_isStreamingCalibration
            && Time.time - _streamingStartTime > streamingMaxDurationSeconds)
        {
            Debug.LogWarning($"[ConstellationDriftCorrector] Streaming calibration auto-cancelled after {streamingMaxDurationSeconds:F0}s. Re-start the sweep.");
            CancelStreamingCalibration();
        }
    }

    // ---- public commands ----
    [ContextMenu("Calibrate Now (batch)")]
    private void CalibrateMenu() => _ = Calibrate();

    [ContextMenu("Begin Streaming Calibration")]
    private void BeginStreamingMenu() => BeginStreamingCalibration();

    [ContextMenu("Commit Streaming Calibration")]
    private void CommitStreamingMenu() => CommitStreamingCalibration();

    [ContextMenu("Cancel Streaming Calibration")]
    private void CancelStreamingMenu() => CancelStreamingCalibration();

    [ContextMenu("Reset Calibration (destroys obstacle)")]
    public void ResetCalibration()
    {
        if (_obstacle) Destroy(_obstacle);
        _obstacle = null;

        if (_anchor) Destroy(_anchor.gameObject);
        _anchor = null;
        _anchorTransform = null;
        _correctionRoot = null;

        _referenceLocal.Clear();
        _candidateBuffer.Clear();
        _appliedCorrection = Pose.identity;
        _lerping = false;
        _cooldownUntil = 0f;
        _autoCalibrateCounter = 0;

        _isStreamingCalibration = false;
        _streamingObservations.Clear();
        _streamingObservationCounts.Clear();
    }

    /// <summary>
    /// Batch calibration. Captures the constellation in one ~1-2 s heavy scan
    /// via StereoAprilTagScanner.ScanCalibrationAsync, requires all desired tags
    /// to be in a single camera frustum, and commits the constellation
    /// synchronously when capture completes.
    ///
    /// On a recalibration the existing obstacle GameObject is preserved across
    /// the swap and its local pose under CorrectionRoot is restored. On capture
    /// failure (too few tags, scanner busy), the existing calibration is left
    /// untouched.
    ///
    /// Use BeginStreamingCalibration / CommitStreamingCalibration instead if
    /// tags don't fit in one frustum and the experimenter needs to sweep.
    /// </summary>
    public async Task<bool> Calibrate(CancellationToken ct = default)
    {
        if (_isCalibrating)
        {
            Debug.LogWarning("[ConstellationDriftCorrector] Calibrate() called while another calibration is in progress.");
            return false;
        }
        if (_isStreamingCalibration)
        {
            Debug.LogWarning("[ConstellationDriftCorrector] Calibrate() called while streaming calibration is active. Commit or cancel it first.");
            return false;
        }
        _isCalibrating = true;
        try
        {
            // Pause normal scanning so the calibration capture isn't starved by
            // AprilTagDisplayManager's per-frame ScanFrameAsync grabbing the
            // scanner's _isScanning flag. Restored in finally.
            var displayManagerWasEnabled = displayManager.enabled;
            displayManager.enabled = false;
            AprilTagResult[] capture = null;
            try
            {
                // Up to a few retries: the scanner may still be busy with an
                // in-flight per-frame scan when we get here.
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    capture = await stereoScanner.ScanCalibrationAsync(calibrationFrameCount, ct,
                        (captured, total, tags) => OnCalibrationProgress?.Invoke(captured, total, tags));
                    if (capture != null && capture.Length > 0) break;
                    await Task.Delay(150, ct);
                }
            }
            catch (OperationCanceledException)
            {
                displayManager.enabled = displayManagerWasEnabled;
                return false;
            }
            catch (Exception e)
            {
                var reason = $"Scanner exception: {e.Message}";
                Debug.LogError($"[ConstellationDriftCorrector] {reason}");
                displayManager.enabled = displayManagerWasEnabled;
                OnCalibrationFailed?.Invoke(reason);
                return false;
            }
            displayManager.enabled = displayManagerWasEnabled;

            // Filter to allowed tags with a usable world-space pose.
            var tags = new List<(int tagId, Pose worldPose)>();
            if (capture != null)
            {
                foreach (var r in capture)
                {
                    if (!IsAllowed(r.tagId)) continue;
                    if (!r.worldPoseOverride.HasValue) continue;
                    tags.Add((r.tagId, r.worldPoseOverride.Value));
                }
            }

            if (tags.Count < minTagsForCalibration)
            {
                var reason = $"Too few tags: captured {tags.Count}, need {minTagsForCalibration}";
                Debug.LogError($"[ConstellationDriftCorrector] {reason}. Previous calibration (if any) is unchanged.");
                OnCalibrationFailed?.Invoke(reason);
                return false;
            }

            BuildConstellation(tags, sourceDescription: "batch");
            return true;
        }
        finally
        {
            _isCalibrating = false;
        }
    }

    /// <summary>
    /// Begin a streaming calibration session. Subsequent per-frame detections
    /// accumulate into a per-tag observation buffer. Call
    /// CommitStreamingCalibration when the experimenter has finished the sweep,
    /// or CancelStreamingCalibration to discard.
    ///
    /// Idempotent: starting while already streaming clears the existing buffer
    /// and restarts the timer.
    /// </summary>
    public void BeginStreamingCalibration()
    {
        if (_isCalibrating)
        {
            Debug.LogWarning("[ConstellationDriftCorrector] BeginStreamingCalibration() blocked: batch calibration in progress.");
            return;
        }
        _streamingObservations.Clear();
        _streamingObservationCounts.Clear();
        _streamingStartTime = Time.time;
        _isStreamingCalibration = true;
        if (verboseLogging) Debug.Log("[ConstellationDriftCorrector] Streaming calibration started.");
    }

    /// <summary>
    /// Commit the streaming calibration: median-fuses each qualifying tag's
    /// accumulated observations and builds the constellation. Returns true on
    /// success, false if too few tags reached <see cref="streamingMinObservationsPerTag"/>
    /// observations. On failure the existing calibration (if any) is unchanged
    /// and the streaming buffer is cleared.
    /// </summary>
    public bool CommitStreamingCalibration()
    {
        if (!_isStreamingCalibration)
        {
            Debug.LogWarning("[ConstellationDriftCorrector] CommitStreamingCalibration() called outside a streaming session.");
            return false;
        }

        var tags = new List<(int tagId, Pose worldPose)>();
        foreach (var kv in _streamingObservations)
        {
            if (kv.Value.Count >= streamingMinObservationsPerTag)
            {
                tags.Add((kv.Key, MedianPose(kv.Value)));
            }
        }

        var elapsed = Time.time - _streamingStartTime;
        var observed = _streamingObservations.Count;

        // Tear down session state regardless of outcome.
        _streamingObservations.Clear();
        _streamingObservationCounts.Clear();
        _isStreamingCalibration = false;

        if (tags.Count < minTagsForCalibration)
        {
            var reason = $"Streaming: only {tags.Count} of {observed} observed tags reached " +
                         $"{streamingMinObservationsPerTag}+ observations (need {minTagsForCalibration} qualifying tags). " +
                         $"Elapsed {elapsed:F1}s.";
            Debug.LogError($"[ConstellationDriftCorrector] {reason}. Previous calibration (if any) is unchanged.");
            OnCalibrationFailed?.Invoke(reason);
            return false;
        }

        BuildConstellation(tags, sourceDescription: $"streaming, {elapsed:F1}s sweep");
        return true;
    }

    /// <summary>
    /// Discard the streaming session without committing. Existing calibration
    /// (if any) is unchanged.
    /// </summary>
    public void CancelStreamingCalibration()
    {
        if (!_isStreamingCalibration) return;
        _isStreamingCalibration = false;
        _streamingObservations.Clear();
        _streamingObservationCounts.Clear();
        if (verboseLogging) Debug.Log("[ConstellationDriftCorrector] Streaming calibration cancelled.");
    }

    /// <summary>
    /// Common anchor-build path used by both calibration modes. Centroid →
    /// anchor → CorrectionRoot → reference data in anchor-local space →
    /// obstacle reparent/instantiate → reset correction state.
    /// </summary>
    private void BuildConstellation(IList<(int tagId, Pose worldPose)> tags, string sourceDescription)
    {
        // Centroid of the captured constellation, identity rotation.
        // Anchor orientation is a coordinate convention; per-tag orientations
        // are carried in the reference data.
        var centroid = Vector3.zero;
        for (int i = 0; i < tags.Count; i++) centroid += tags[i].worldPose.position;
        centroid /= tags.Count;

        // Preserve existing obstacle's local pose (the experimenter's authored
        // offset) before tearing down the old anchor.
        GameObject preservedObstacle = null;
        Vector3 preservedLocalPos = Vector3.zero;
        Quaternion preservedLocalRot = Quaternion.identity;
        Vector3 preservedLocalScale = Vector3.one;
        if (_obstacle && _correctionRoot)
        {
            preservedObstacle = _obstacle;
            preservedLocalPos = preservedObstacle.transform.localPosition;
            preservedLocalRot = preservedObstacle.transform.localRotation;
            preservedLocalScale = preservedObstacle.transform.localScale;
            preservedObstacle.transform.SetParent(null, worldPositionStays: true);
        }

        if (_anchor) Destroy(_anchor.gameObject);
        _anchor = null;
        _anchorTransform = null;
        _correctionRoot = null;
        _obstacle = null;

        var anchorGo = new GameObject("ConstellationAnchor");
        anchorGo.transform.SetPositionAndRotation(centroid, Quaternion.identity);
        _anchor = anchorGo.AddComponent<OVRSpatialAnchor>();
        _anchorTransform = anchorGo.transform;

        var rootGo = new GameObject("CorrectionRoot");
        rootGo.transform.SetParent(anchorGo.transform, worldPositionStays: false);
        rootGo.transform.localPosition = Vector3.zero;
        rootGo.transform.localRotation = Quaternion.identity;
        _correctionRoot = rootGo.transform;

        _referenceLocal.Clear();
        for (int i = 0; i < tags.Count; i++)
        {
            var w = tags[i].worldPose;
            var localPos = anchorGo.transform.InverseTransformPoint(w.position);
            var localRot = Quaternion.Inverse(anchorGo.transform.rotation) * w.rotation;
            _referenceLocal[tags[i].tagId] = new Pose(localPos, localRot);
        }

        if (preservedObstacle)
        {
            preservedObstacle.transform.SetParent(_correctionRoot, worldPositionStays: false);
            preservedObstacle.transform.localPosition = preservedLocalPos;
            preservedObstacle.transform.localRotation = preservedLocalRot;
            preservedObstacle.transform.localScale = preservedLocalScale;
            _obstacle = preservedObstacle;
        }
        else if (anchoredObstaclePrefab)
        {
            _obstacle = Instantiate(anchoredObstaclePrefab, _correctionRoot);
        }

        _candidateBuffer.Clear();
        _appliedCorrection = Pose.identity;
        _lerping = false;
        _cooldownUntil = 0f;
        _lastDetectionTime = Time.time;

        Debug.Log($"[ConstellationDriftCorrector] Calibrated with {tags.Count} tags at centroid {centroid} ({sourceDescription}).");
        OnConstellationCalibrated?.Invoke();
    }

    /// <summary>
    /// Per-component position median + sign-corrected slerp average for rotation.
    /// Component-wise median makes position robust to a small number of bad
    /// observations (motion-blurred frame, edge-of-frustum detection). Rotation
    /// uses an incremental slerp with quaternion sign-flip handling so a tag
    /// observed across head poses doesn't average through the long arc.
    /// </summary>
    private static Pose MedianPose(IList<Pose> observations)
    {
        int n = observations.Count;
        if (n == 0) return Pose.identity;
        if (n == 1) return observations[0];

        // Position: component-wise median.
        var xs = new float[n];
        var ys = new float[n];
        var zs = new float[n];
        for (int i = 0; i < n; i++)
        {
            var p = observations[i].position;
            xs[i] = p.x; ys[i] = p.y; zs[i] = p.z;
        }
        Array.Sort(xs); Array.Sort(ys); Array.Sort(zs);
        int mid = n / 2;
        Vector3 medPos;
        if ((n & 1) == 0)
        {
            medPos = new Vector3(
                (xs[mid - 1] + xs[mid]) * 0.5f,
                (ys[mid - 1] + ys[mid]) * 0.5f,
                (zs[mid - 1] + zs[mid]) * 0.5f);
        }
        else
        {
            medPos = new Vector3(xs[mid], ys[mid], zs[mid]);
        }

        // Rotation: incremental slerp with sign correction. Quaternion q and -q
        // represent the same rotation; if Dot(acc, q) is negative we must flip
        // q before slerping or we'll travel the long way around.
        var acc = observations[0].rotation;
        for (int i = 1; i < n; i++)
        {
            var q = observations[i].rotation;
            if (Quaternion.Dot(acc, q) < 0f)
            {
                q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            }
            acc = Quaternion.Slerp(acc, q, 1f / (i + 1));
        }

        return new Pose(medPos, acc);
    }

    private bool IsAllowed(int tagId)
    {
        if (allowedTagIds == null || allowedTagIds.Length == 0) return true;
        for (int i = 0; i < allowedTagIds.Length; i++)
            if (allowedTagIds[i] == tagId) return true;
        return false;
    }

    // ---- per-frame detection handler ----
    private void HandleTagsDetected(AprilTagDisplayManager.TagWorldPose[] poses)
    {
        if (poses == null || poses.Length == 0) return;
        _lastDetectionTime = Time.time;

        // Streaming-calibration accumulation. Runs in parallel to whatever else
        // the corrector would do (if already calibrated, we still apply
        // corrections from the existing constellation while accumulating new
        // observations for the next commit — the experimenter can preview the
        // current state while recalibrating).
        if (_isStreamingCalibration)
        {
            int totalObs = 0;
            foreach (var p in poses)
            {
                if (!IsAllowed(p.TagId)) continue;
                if (!_streamingObservations.TryGetValue(p.TagId, out var list))
                {
                    list = new List<Pose>();
                    _streamingObservations[p.TagId] = list;
                }
                list.Add(new Pose(p.Position, p.Rotation));
                _streamingObservationCounts[p.TagId] = list.Count;
            }
            foreach (var kv in _streamingObservationCounts) totalObs += kv.Value;
            OnStreamingCalibrationProgress?.Invoke(totalObs, _streamingObservationCounts.Count);
        }

        if (!IsCalibrated)
        {
            HandleAutoCalibrate(poses);
            return;
        }

        // Project detections into anchor-local space using the *current* anchor
        // world pose. Solving in anchor-local means Kabsch's rigid output IS the
        // CorrectionRoot.localPose directly — no further coordinate conversion.
        _samples.Clear();
        foreach (var p in poses)
        {
            if (!IsAllowed(p.TagId)) continue;
            if (!_referenceLocal.TryGetValue(p.TagId, out var refLocal)) continue;

            _samples.Add(new Sample
            {
                tagId = p.TagId,
                source = refLocal.position,
                target = _anchorTransform.InverseTransformPoint(p.Position),
                refRot = refLocal.rotation,
                detRot = Quaternion.Inverse(_anchorTransform.rotation) * p.Rotation,
                worldPos = p.Position,
            });
        }

        if (_samples.Count < 3)
        {
            _gizmoInliersWorld.Clear();
            _gizmoOutliersWorld.Clear();
            _candidateBuffer.Clear();
            return;
        }

        if (!RansacKabsch(_samples, out var posInliers, out var posPose))
        {
            _candidateBuffer.Clear();
            OnCorrectionRejected?.Invoke("RANSAC found no consensus");
            return;
        }

        // Rotation gate: drop tags whose rotation disagrees with the consensus
        // solve. Catches misdetections (occlusion, motion blur, edge of frame)
        // earlier than position-only RANSAC would.
        _finalInliers.Clear();
        for (int i = 0; i < posInliers.Count; i++)
        {
            var idx = posInliers[i];
            var s = _samples[idx];
            var expectedDet = posPose.rotation * s.refRot;
            if (Quaternion.Angle(expectedDet, s.detRot) <= rotationGateDegrees)
                _finalInliers.Add(idx);
        }
        if (_finalInliers.Count < 3)
        {
            _candidateBuffer.Clear();
            OnCorrectionRejected?.Invoke($"Rotation gate rejected too many tags ({posInliers.Count} -> {_finalInliers.Count})");
            return;
        }

        var refined = SolveKabsch(_samples, _finalInliers);

        float sumSq = 0f;
        foreach (var idx in _finalInliers)
        {
            var pred = refined.position + refined.rotation * _samples[idx].source;
            sumSq += (pred - _samples[idx].target).sqrMagnitude;
        }
        var rms = Mathf.Sqrt(sumSq / _finalInliers.Count);
        _lastResidualRms = rms;

        _gizmoInliersWorld.Clear();
        _gizmoOutliersWorld.Clear();
        _inlierSet.Clear();
        foreach (var idx in _finalInliers) _inlierSet.Add(idx);
        for (int i = 0; i < _samples.Count; i++)
        {
            if (_inlierSet.Contains(i)) _gizmoInliersWorld.Add(_samples[i].worldPos);
            else _gizmoOutliersWorld.Add(_samples[i].worldPos);
        }

        if (rms > residualRmsWarnMeters)
        {
            _candidateBuffer.Clear();
            if (verboseLogging)
                Debug.LogWarning($"[ConstellationDriftCorrector] Solve RMS {rms * 1000f:F2}mm exceeds {residualRmsWarnMeters * 1000f:F2}mm; discarded.");
            OnCorrectionRejected?.Invoke($"Residual RMS {rms * 1000f:F2}mm too high");

            return;
        }

        _candidateBuffer.Enqueue(refined);
        while (_candidateBuffer.Count > consistencyFrameCount) _candidateBuffer.Dequeue();
        if (_candidateBuffer.Count < consistencyFrameCount) return;

        if (!CandidatesAgree(_candidateBuffer))
        {
            if (verboseLogging) Debug.Log("[ConstellationDriftCorrector] Candidates disagree across consistency window.");
            return;
        }

        if (_lerping || Time.time < _cooldownUntil) return;

        // Magnitude check is *incremental* — measured from the currently-applied
        // correction, not from identity. Otherwise we'd retrigger forever any
        // time the threshold was passed.
        var deltaPos = (refined.position - _appliedCorrection.position).magnitude;
        if (deltaPos < driftTriggerThresholdMeters) return;

        _lerpFrom = _appliedCorrection;
        _lerpTo = refined;
        _lerpStartTime = Time.time;
        _lerping = true;
        OnCorrectionTriggered?.Invoke(refined);
        if (verboseLogging)
        {
            var deltaAng = Quaternion.Angle(_appliedCorrection.rotation, refined.rotation);
            Debug.Log($"[ConstellationDriftCorrector] Trigger Δpos={deltaPos * 1000f:F1}mm Δrot={deltaAng:F2}° rms={rms * 1000f:F2}mm inliers={_finalInliers.Count}/{_samples.Count}");
        }
    }

    private void HandleAutoCalibrate(AprilTagDisplayManager.TagWorldPose[] poses)
    {
        if (!autoCalibrate || _isCalibrating || _isStreamingCalibration) return;

        int allowedCount = 0;
        foreach (var p in poses) if (IsAllowed(p.TagId)) allowedCount++;
        if (allowedCount < minTagsForCalibration)
        {
            _autoCalibrateCounter = 0;
            return;
        }
        _autoCalibrateCounter++;
        if (_autoCalibrateCounter >= autoCalibrateConsistentFrames)
        {
            _autoCalibrateCounter = 0;
            _ = Calibrate();
        }
    }

    private bool CandidatesAgree(IEnumerable<Pose> buffer)
    {
        Vector3 mean = Vector3.zero;
        Quaternion firstRot = Quaternion.identity;
        int n = 0;
        foreach (var p in buffer)
        {
            mean += p.position;
            if (n == 0) firstRot = p.rotation;
            n++;
        }
        if (n == 0) return false;
        mean /= n;

        foreach (var p in buffer)
        {
            if ((p.position - mean).magnitude > consistencyTranslationMeters) return false;
            if (Quaternion.Angle(p.rotation, firstRot) > consistencyRotationDegrees) return false;
        }
        return true;
    }

    private void UpdateLerp()
    {
        if (!_lerping || _correctionRoot == null) return;
        var t = (Time.time - _lerpStartTime) / Mathf.Max(0.001f, lerpDurationSeconds);
        var done = t >= 1f;
        if (done) t = 1f;
        var s = SmoothStep(t);
        _appliedCorrection = new Pose(
            Vector3.Lerp(_lerpFrom.position, _lerpTo.position, s),
            Quaternion.Slerp(_lerpFrom.rotation, _lerpTo.rotation, s));
        _correctionRoot.localPosition = _appliedCorrection.position;
        _correctionRoot.localRotation = _appliedCorrection.rotation;
        if (done)
        {
            _lerping = false;
            _cooldownUntil = Time.time + cooldownSeconds;
            OnCorrectionCompleted?.Invoke();
        }
    }

    private static float SmoothStep(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    // ---- RANSAC + Kabsch ----
    private struct Sample
    {
        public int tagId;
        public Vector3 source;     // anchor-local reference
        public Vector3 target;     // anchor-local detection
        public Quaternion refRot;
        public Quaternion detRot;
        public Vector3 worldPos;   // gizmo-only
    }

    private bool RansacKabsch(List<Sample> samples, out List<int> bestInliers, out Pose bestPose)
    {
        bestInliers = null;
        bestPose = Pose.identity;

        int n = samples.Count;
        if (n < 3) return false;

        if (n == 3)
        {
            bestInliers = new List<int> { 0, 1, 2 };
            bestPose = SolveKabsch(samples, bestInliers);
            return true;
        }

        var sqThr = ransacInlierThresholdMeters * ransacInlierThresholdMeters;
        int bestCount = 0;
        var trio = new List<int>(3);

        for (int iter = 0; iter < ransacIterations; iter++)
        {
            trio.Clear();
            while (trio.Count < 3)
            {
                int candidate = _rng.Next(n);
                if (!trio.Contains(candidate)) trio.Add(candidate);
            }
            var pose = SolveKabsch(samples, trio);

            var inliers = new List<int>();
            for (int j = 0; j < n; j++)
            {
                var pred = pose.position + pose.rotation * samples[j].source;
                if ((pred - samples[j].target).sqrMagnitude <= sqThr) inliers.Add(j);
            }

            if (inliers.Count > bestCount)
            {
                bestCount = inliers.Count;
                bestInliers = inliers;
                bestPose = pose;
            }
        }

        if (bestInliers == null || bestCount < 3) return false;

        bestPose = SolveKabsch(samples, bestInliers);
        return true;
    }

    // Optimal rigid transform via Horn 1987: build the symmetric 4x4 matrix N
    // from the 3x3 cross-covariance H, then the eigenvector of N's largest
    // eigenvalue is the optimal quaternion. Power iteration converges in
    // ~10-20 iterations; we shift N by its absolute-row-sum to guarantee all
    // eigenvalues are positive so the dominant-magnitude eigenvalue is also
    // the dominant-value eigenvalue.
    private static Pose SolveKabsch(List<Sample> samples, List<int> indices)
    {
        int n = indices.Count;
        if (n < 3) return Pose.identity;

        Vector3 pBar = Vector3.zero, qBar = Vector3.zero;
        foreach (var i in indices) { pBar += samples[i].source; qBar += samples[i].target; }
        pBar /= n; qBar /= n;

        float h00 = 0, h01 = 0, h02 = 0;
        float h10 = 0, h11 = 0, h12 = 0;
        float h20 = 0, h21 = 0, h22 = 0;
        foreach (var i in indices)
        {
            var p = samples[i].source - pBar;
            var q = samples[i].target - qBar;
            h00 += p.x * q.x; h01 += p.x * q.y; h02 += p.x * q.z;
            h10 += p.y * q.x; h11 += p.y * q.y; h12 += p.y * q.z;
            h20 += p.z * q.x; h21 += p.z * q.y; h22 += p.z * q.z;
        }

        float trace = h00 + h11 + h22;
        float n00 = trace;
        float n11 = h00 - h11 - h22;
        float n22 = -h00 + h11 - h22;
        float n33 = -h00 - h11 + h22;
        float n01 = h12 - h21;
        float n02 = h20 - h02;
        float n03 = h01 - h10;
        float n12 = h01 + h10;
        float n13 = h20 + h02;
        float n23 = h12 + h21;

        float shift = Mathf.Abs(n00) + Mathf.Abs(n11) + Mathf.Abs(n22) + Mathf.Abs(n33)
                    + 2f * (Mathf.Abs(n01) + Mathf.Abs(n02) + Mathf.Abs(n03)
                          + Mathf.Abs(n12) + Mathf.Abs(n13) + Mathf.Abs(n23));
        float m00 = n00 + shift, m11 = n11 + shift, m22 = n22 + shift, m33 = n33 + shift;

        // Initial guess (1, 0, 0, 0) biases toward identity rotation, which is
        // the right side to converge from in the small-correction regime.
        float v0 = 1f, v1 = 0f, v2 = 0f, v3 = 0f;
        for (int it = 0; it < 30; it++)
        {
            float w0 = m00 * v0 + n01 * v1 + n02 * v2 + n03 * v3;
            float w1 = n01 * v0 + m11 * v1 + n12 * v2 + n13 * v3;
            float w2 = n02 * v0 + n12 * v1 + m22 * v2 + n23 * v3;
            float w3 = n03 * v0 + n13 * v1 + n23 * v2 + m33 * v3;
            float mag = Mathf.Sqrt(w0 * w0 + w1 * w1 + w2 * w2 + w3 * w3);
            if (mag < 1e-12f) { v0 = 1f; v1 = v2 = v3 = 0f; break; }
            v0 = w0 / mag; v1 = w1 / mag; v2 = w2 / mag; v3 = w3 / mag;
        }

        // Horn convention is (w, x, y, z); Unity's Quaternion is (x, y, z, w).
        var rot = new Quaternion(v1, v2, v3, v0);
        var qmag = Mathf.Sqrt(rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w);
        if (qmag < 1e-6f) rot = Quaternion.identity;
        else rot = new Quaternion(rot.x / qmag, rot.y / qmag, rot.z / qmag, rot.w / qmag);

        var t = qBar - rot * pBar;
        return new Pose(t, rot);
    }

    // ---- gizmos ----
    private void OnDrawGizmos()
    {
        if (!drawGizmos) return;

        if (_anchorTransform)
        {
            Gizmos.color = Color.green;
            foreach (var kv in _referenceLocal)
            {
                var w = _anchorTransform.TransformPoint(kv.Value.position);
                Gizmos.DrawWireSphere(w, 0.02f);
            }
        }

        Gizmos.color = Color.yellow;
        foreach (var w in _gizmoInliersWorld) Gizmos.DrawWireCube(w, Vector3.one * 0.03f);

        Gizmos.color = Color.red;
        foreach (var w in _gizmoOutliersWorld) Gizmos.DrawWireCube(w, Vector3.one * 0.03f);

        if (_correctionRoot && IsCalibrated)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(_anchorTransform.position, _correctionRoot.position);
        }

        // Streaming-session preview: draw the running median of each tag's
        // observations so the experimenter can see what they've captured so far.
        if (_isStreamingCalibration && _streamingObservations.Count > 0)
        {
            Gizmos.color = new Color(0.4f, 0.8f, 1f);
            foreach (var kv in _streamingObservations)
            {
                if (kv.Value.Count == 0) continue;
                var med = MedianPose(kv.Value);
                Gizmos.DrawWireSphere(med.position, 0.025f);
            }
        }
    }
}
