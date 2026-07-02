using System;
using System.Globalization;
using UnityEngine;

/// <summary>
/// Single/double-AprilTag obstacle placement for the stepped-back experimental
/// flow. Builds and drives the world-locked transform chain:
///
/// <code>
/// ObstacleAnchorRoot   (OVRSpatialAnchor in the Anchored variant; plain root otherwise)
///   └── TagOffset       (this controller writes the solver's proposed pose here, per policy)
///       └── FinesseOffset  (ObstacleFinesseController nudges this — the baked-in offset)
///           └── Obstacle    (ObstacleController target; trial perturbation lives below it)
/// </code>
///
/// The active <see cref="ITagPlacementSolver"/> turns per-frame AprilTag
/// detections into a proposed base pose. How that proposal reaches the visible
/// obstacle is governed by <see cref="VisualUpdatePolicy"/> (Deferred default =
/// apply between trials; SmoothedLive = low-pass live; RawLive = snap live).
///
/// Placement is <see cref="PlacementTrigger.Manual"/> by default: the experimenter
/// frames the tag and calls <see cref="CapturePlacement"/> ("place now") for the
/// highest-quality stable capture. Variant / policy / solver have runtime setters +
/// cycles so an in-headset control surface (or a future web bridge) can A/B them —
/// see <see cref="ExperimenterSessionControls"/>.
///
/// Logging reuses the existing SessionLogger schema (no column changes): state_snapshot
/// rows with mode=observe carry the tag-proposed pose, mode=applied carry the obstacle
/// base's actual world pose; correction_event rows record each applied between-trial
/// correction. correction_source = the solver's label (apriltag_single / apriltag_pair).
/// </summary>
[DisallowMultipleComponent]
public sealed class ObstaclePlacementController : MonoBehaviour
{
    [Header("Wiring")]
    [Tooltip("Source of world-space tag detections. Auto-resolved if empty.")]
    [SerializeField] private AprilTagDisplayManager displayManager;

    [Tooltip("Prefab spawned as the visible obstacle (placed under FinesseOffset).")]
    [SerializeField] private GameObject obstaclePrefab;

    [Tooltip("Trial controller. Its manualTarget is pointed at the spawned obstacle at " +
             "runtime; its OnObstacleReset is the 'participant passed' signal used to apply " +
             "Deferred corrections. Auto-resolved if empty.")]
    [SerializeField] private ObstacleController obstacleController;

    [Tooltip("Optional. Finesse controller; its manualTarget is pointed at FinesseOffset at " +
             "runtime so the baked-in nudge layer sits between the tag offset and the obstacle.")]
    [SerializeField] private ObstacleFinesseController finesseController;

    [Tooltip("Optional HUD for transient placement/status messages. Auto-resolved if empty.")]
    [SerializeField] private PipelineStatusHUD hud;

    [Header("Variant (Inspector-switchable; also runtime via setters)")]
    [SerializeField] private TrackingVariant trackingVariant = TrackingVariant.Anchored;
    [SerializeField] private VisualUpdatePolicy visualPolicy = VisualUpdatePolicy.Deferred;
    [SerializeField] private TagSolverMode solverMode = TagSolverMode.SingleTag;

    [Tooltip("Manual = place only on an explicit CapturePlacement() (best quality). " +
             "AutoOnFirstStable = place as soon as a stable tag pose is seen.")]
    [SerializeField] private PlacementTrigger placementMode = PlacementTrigger.Manual;

    [Header("Single-tag solver")]
    [Tooltip("Tag ID to place from. -1 = use the nearest detected tag.")]
    [SerializeField] private int singleTagId = -1;
    [Tooltip("Only place/refine when the camera is within this distance of the tag (m).")]
    [SerializeField] private float singleTagMaxDistanceMeters = 1.0f;

    [Header("Two-tag solver")]
    [SerializeField] private int twoTagIdA = 0;
    [SerializeField] private int twoTagIdB = 1;
    [SerializeField] private float twoTagVerticalOffsetMeters = 0f;
    [SerializeField] private Vector3 twoTagRotationOffsetEuler = Vector3.zero;

    [Header("Stability gate (shared by the active solver)")]
    [SerializeField] private int gateWindowSize = 10;
    [SerializeField] private float gateMaxPositionSpreadMeters = 0.005f;
    [SerializeField] private float gateMaxRotationSpreadDegrees = 2f;
    [SerializeField] private float gateMaxObservationAgeSeconds = 0.5f;

    [Header("Smoothing (SmoothedLive policy)")]
    [Tooltip("Fraction-per-second the obstacle closes toward the tag pose. Higher = snappier.")]
    [SerializeField, Range(0.1f, 20f)] private float smoothingRatePerSecond = 4f;

    [Header("Behavior")]
    [Tooltip("Hide the obstacle's renderers until the first stable placement.")]
    [SerializeField] private bool hideUntilPlaced = true;

    [Header("Logging")]
    [SerializeField] private bool logMeasurements = true;
    [SerializeField, Range(1f, 30f)] private float measurementLogRateHz = 30f;

    // ---- runtime ----
    private Transform _anchorRoot;
    private Transform _tagOffset;
    private Transform _finesseOffset;
    private Transform _obstacle;
    private GameObject _anchorRootGo;
    private OVRSpatialAnchor _anchor;
    private Renderer[] _obstacleRenderers;

    private ITagPlacementSolver _solver;
    private AprilTagPoseStabilityGate _gate;

    private bool _placed;
    private bool _captureRequested;
    private bool _hasPending;
    private Pose _pendingProposed;
    private Pose _latestProposed;
    private bool _hasLatest;
    private float _nextMeasurementLogTime;
    private Transform _cameraRef;

    // ---- public surface (chords + future web bridge call these) ----
    public bool IsPlaced => _placed;
    public bool HasTagCandidate => _hasLatest;
    public Transform ObstacleTransform => _obstacle;
    public TrackingVariant Variant => trackingVariant;
    public VisualUpdatePolicy Policy => visualPolicy;
    public TagSolverMode Solver => solverMode;
    public string SourceLabel => _solver != null ? _solver.SourceLabel : "(none)";

    /// <summary>Fired after the obstacle is (re)placed.</summary>
    public event Action OnPlaced;
    /// <summary>Fired when variant / policy / solver changes (for HUD / web sync).</summary>
    public event Action OnConfigChanged;

    private void Awake()
    {
        if (!displayManager) displayManager = FindAnyObjectByType<AprilTagDisplayManager>();
        if (!obstacleController) obstacleController = FindAnyObjectByType<ObstacleController>();
        if (!finesseController) finesseController = FindAnyObjectByType<ObstacleFinesseController>();
        if (!hud) hud = FindAnyObjectByType<PipelineStatusHUD>();
    }

    private void Start()
    {
        if (!obstaclePrefab)
        {
            Debug.LogError("[ObstaclePlacement] No obstaclePrefab assigned. Disabling.");
            enabled = false;
            return;
        }

        BuildChain();
        BuildSolver();

        if (obstacleController) obstacleController.SetManualTarget(_obstacle);
        if (finesseController) finesseController.SetManualTarget(_finesseOffset);
        if (obstacleController) obstacleController.OnObstacleReset += ApplyPendingCorrection;
    }

    private void OnEnable()
    {
        if (displayManager) displayManager.OnTagsDetected += HandleDetections;
    }

    private void OnDisable()
    {
        if (displayManager) displayManager.OnTagsDetected -= HandleDetections;
    }

    private void OnDestroy()
    {
        if (obstacleController) obstacleController.OnObstacleReset -= ApplyPendingCorrection;
    }

    private void BuildChain()
    {
        _anchorRootGo = new GameObject("ObstacleAnchorRoot");
        _anchorRoot = _anchorRootGo.transform;

        var tagOffsetGo = new GameObject("TagOffset");
        _tagOffset = tagOffsetGo.transform;
        _tagOffset.SetParent(_anchorRoot, worldPositionStays: false);

        var finesseGo = new GameObject("FinesseOffset");
        _finesseOffset = finesseGo.transform;
        _finesseOffset.SetParent(_tagOffset, worldPositionStays: false);

        var obstacleGo = Instantiate(obstaclePrefab, _finesseOffset);
        obstacleGo.name = $"{obstaclePrefab.name} (placement-spawned)";
        obstacleGo.transform.localPosition = Vector3.zero;
        obstacleGo.transform.localRotation = Quaternion.identity;
        _obstacle = obstacleGo.transform;

        _obstacleRenderers = _obstacle.GetComponentsInChildren<Renderer>(true);
        if (hideUntilPlaced) SetObstacleVisible(false);
    }

    private void BuildSolver()
    {
        _gate = new AprilTagPoseStabilityGate
        {
            WindowSize = gateWindowSize,
            MaxPositionSpreadMeters = gateMaxPositionSpreadMeters,
            MaxRotationSpreadDegrees = gateMaxRotationSpreadDegrees,
            MaxObservationAgeSeconds = gateMaxObservationAgeSeconds,
        };

        _solver = solverMode switch
        {
            TagSolverMode.SingleTag => new SingleTagSolver(singleTagId, singleTagMaxDistanceMeters, _gate, CameraRef()),
            TagSolverMode.TwoTagLine => new TwoTagLineSolver(twoTagIdA, twoTagIdB, twoTagVerticalOffsetMeters, twoTagRotationOffsetEuler, _gate),
            TagSolverMode.Constellation => new ConstellationSolver(),
            _ => new SingleTagSolver(singleTagId, singleTagMaxDistanceMeters, _gate, CameraRef()),
        };
    }

    private void HandleDetections(AprilTagDisplayManager.TagWorldPose[] detections)
    {
        if (_solver == null) return;
        if (!_solver.TryGetPose(detections, Time.time, out var proposed)) return;

        _latestProposed = proposed;
        _hasLatest = true;

        if (!_placed)
        {
            bool shouldPlace = placementMode == PlacementTrigger.AutoOnFirstStable || _captureRequested;
            if (shouldPlace)
            {
                _captureRequested = false;
                PlaceInitial(proposed);
            }
            return;
        }

        switch (visualPolicy)
        {
            case VisualUpdatePolicy.RawLive:
                _tagOffset.SetPositionAndRotation(proposed.position, proposed.rotation);
                break;
            case VisualUpdatePolicy.SmoothedLive:
                break; // applied in Update()
            case VisualUpdatePolicy.Deferred:
                _pendingProposed = proposed;
                _hasPending = true;
                break;
        }

        LogMeasurement(proposed);
    }

    private void Update()
    {
        if (_placed && visualPolicy == VisualUpdatePolicy.SmoothedLive && _hasLatest)
        {
            float t = 1f - Mathf.Exp(-smoothingRatePerSecond * Time.deltaTime);
            var pos = Vector3.Lerp(_tagOffset.position, _latestProposed.position, t);
            var rot = Quaternion.Slerp(_tagOffset.rotation, _latestProposed.rotation, t);
            _tagOffset.SetPositionAndRotation(pos, rot);
        }
    }

    // ---- placement control (public for chords / web) ----

    /// <summary>"Place now": clears the gate for a fresh high-quality window, then places
    /// on the next stable detection. No-op (with feedback) if already placed.</summary>
    public void CapturePlacement()
    {
        if (_placed)
        {
            Hud("Already placed — Recapture first to re-place.");
            return;
        }
        _solver?.Reset();
        _captureRequested = true;
        Hud("Capturing — hold steady on the tag…");
        Debug.Log("[ObstaclePlacement] CapturePlacement requested.");
    }

    /// <summary>Clear the placement; experimenter must Place-now again to re-capture.</summary>
    [ContextMenu("Recapture (clear placement)")]
    public void Recapture() => ClearPlacement(autoReplace: false);

    private void RecaptureAndReplace() => ClearPlacement(autoReplace: true);

    private void ClearPlacement(bool autoReplace)
    {
        _placed = false;
        _hasPending = false;
        _hasLatest = false;
        _captureRequested = autoReplace;
        _solver?.Reset();
        if (_anchor) { Destroy(_anchor); _anchor = null; }
        if (hideUntilPlaced) SetObstacleVisible(false);
        Hud(autoReplace ? "Re-placing with new config…" : "Cleared — Place now to re-capture.");
    }

    // ---- runtime config (public for chords / web) ----

    public void CycleVisualPolicy()
    {
        var next = visualPolicy switch
        {
            VisualUpdatePolicy.Deferred => VisualUpdatePolicy.SmoothedLive,
            VisualUpdatePolicy.SmoothedLive => VisualUpdatePolicy.RawLive,
            _ => VisualUpdatePolicy.Deferred,
        };
        SetVisualPolicy(next);
    }

    public void SetVisualPolicy(VisualUpdatePolicy p)
    {
        visualPolicy = p;
        if (p == VisualUpdatePolicy.Deferred) _hasPending = false;
        OnConfigChanged?.Invoke();
        Hud($"Policy: {p}");
    }

    public void CycleTrackingVariant()
        => SetTrackingVariant(trackingVariant == TrackingVariant.Anchored ? TrackingVariant.WorldRoot : TrackingVariant.Anchored);

    public void SetTrackingVariant(TrackingVariant v)
    {
        if (v == trackingVariant) return;
        trackingVariant = v;
        OnConfigChanged?.Invoke();
        Hud($"Variant: {v}");
        if (_placed) RecaptureAndReplace();
    }

    /// <summary>Cycles Single ↔ TwoTagLine (Constellation is out of scope this pass).</summary>
    public void CycleSolverMode()
        => SetSolverMode(solverMode == TagSolverMode.SingleTag ? TagSolverMode.TwoTagLine : TagSolverMode.SingleTag);

    public void SetSolverMode(TagSolverMode m)
    {
        if (m == solverMode) return;
        solverMode = m;
        BuildSolver();
        OnConfigChanged?.Invoke();
        Hud($"Solver: {m}");
        if (_placed) RecaptureAndReplace();
    }

    public string StatusLine()
        => $"{(_placed ? "PLACED" : (_hasLatest ? "tag ready" : "no tag"))} · {solverMode} · {trackingVariant} · {visualPolicy}";

    // ---- internals ----

    private void PlaceInitial(Pose pose)
    {
        if (trackingVariant == TrackingVariant.Anchored)
        {
            _anchorRoot.SetPositionAndRotation(pose.position, pose.rotation);
            _anchor = _anchorRootGo.AddComponent<OVRSpatialAnchor>();
        }

        _tagOffset.SetPositionAndRotation(pose.position, pose.rotation);
        _placed = true;
        if (hideUntilPlaced) SetObstacleVisible(true);

        Debug.Log($"[ObstaclePlacement] Placed ({solverMode}, {trackingVariant}, {visualPolicy}) at {pose.position}.");
        Hud($"Placed · {solverMode} · {trackingVariant}");
        LogMeasurement(pose);

        if (SessionLogger.Instance != null)
        {
            SessionLogger.Instance.Enqueue(LogEvent.SessionEvent(
                "obstacle_placed",
                $"solver={_solver.SourceLabel};variant={trackingVariant};policy={visualPolicy};pos={Fmt(pose.position)}"));
        }

        OnPlaced?.Invoke();
    }

    /// <summary>Applies the held (Deferred) correction to the obstacle base between trials.</summary>
    private void ApplyPendingCorrection()
    {
        if (!_placed || visualPolicy != VisualUpdatePolicy.Deferred || !_hasPending) return;

        var beforePos = _tagOffset.position;
        var beforeRot = _tagOffset.rotation;
        _tagOffset.SetPositionAndRotation(_pendingProposed.position, _pendingProposed.rotation);
        _hasPending = false;

        float dPos = Vector3.Distance(beforePos, _pendingProposed.position);
        float dRot = Quaternion.Angle(beforeRot, _pendingProposed.rotation);
        Debug.Log($"[ObstaclePlacement] Deferred correction applied between trials: {dPos * 1000f:F1} mm / {dRot:F2} deg.");

        if (SessionLogger.Instance != null)
        {
            SessionLogger.Instance.Enqueue(LogEvent.CorrectionEvent(
                _solver.SourceLabel, mode: "applied", accepted: true,
                deltaPositionM: dPos, deltaRotationDeg: dRot, correctionAppliedM: dPos));
        }
    }

    private void LogMeasurement(Pose proposed)
    {
        if (!logMeasurements || SessionLogger.Instance == null) return;
        if (Time.unscaledTime < _nextMeasurementLogTime) return;
        _nextMeasurementLogTime = Time.unscaledTime + 1f / Mathf.Max(0.1f, measurementLogRateHz);

        var cam = CameraRef();
        Vector3? headPos = cam ? cam.position : (Vector3?)null;
        Quaternion? headRot = cam ? cam.rotation : (Quaternion?)null;

        var obs = LogEvent.StateSnapshot(_solver.SourceLabel, mode: "observe");
        obs.AnchorPos = proposed.position;
        obs.AnchorRot = proposed.rotation;
        obs.HeadsetPos = headPos;
        obs.HeadsetRot = headRot;
        SessionLogger.Instance.Enqueue(obs);

        var app = LogEvent.StateSnapshot(_solver.SourceLabel, mode: "applied");
        app.AnchorPos = _tagOffset.position;
        app.AnchorRot = _tagOffset.rotation;
        app.HeadsetPos = headPos;
        app.HeadsetRot = headRot;
        SessionLogger.Instance.Enqueue(app);
    }

    private Transform CameraRef()
    {
        if (!_cameraRef && Camera.main) _cameraRef = Camera.main.transform;
        return _cameraRef;
    }

    private void SetObstacleVisible(bool visible)
    {
        if (_obstacleRenderers == null) return;
        foreach (var r in _obstacleRenderers)
            if (r) r.enabled = visible;
    }

    private void Hud(string msg)
    {
        if (hud) hud.ShowTransient(msg, 3f);
    }

    private static string Fmt(Vector3 v)
        => string.Format(CultureInfo.InvariantCulture, "{0:F3}|{1:F3}|{2:F3}", v.x, v.y, v.z);
}
