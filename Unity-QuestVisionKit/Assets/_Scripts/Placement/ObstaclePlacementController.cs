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
/// apply between trials on obstacle reset; SmoothedLive = low-pass live;
/// RawLive = snap live).
///
/// Placement is <see cref="PlacementTrigger.Manual"/> by default: the experimenter
/// frames the tag and commits via <see cref="CapturePlacement"/> ("place now").
/// During the place-hold a translucent <b>ghost preview</b> can be shown at the
/// live tag-proposed pose (<see cref="BeginPlacementPreview"/>).
///
/// Experimental conditions are bundled as <see cref="ConditionPreset"/>s cycled
/// via <see cref="CyclePreset"/>; every config change (preset or individual
/// setter) is logged as <c>session_event subtype=config_change</c> so A/B
/// comparisons are attributable in the CSV. Deferred corrections older than
/// <see cref="pendingProposalMaxAgeSeconds"/> are rejected (stale after tag
/// occlusion) and logged as rejected correction_events.
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

    [Header("Condition presets")]
    [Tooltip("Named {solver, policy, variant} bundles cycled as one action. First entry = boot " +
             "default. Add more in the Inspector for A/B sessions (see OperatorQuickstart.md).")]
    [SerializeField] private ConditionPreset[] presets = Array.Empty<ConditionPreset>();

    [Header("Variant (set by presets; individually settable via the public API)")]
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

    [Header("Deferred corrections")]
    [Tooltip("A held (Deferred) correction older than this is rejected as stale instead of " +
             "applied — protects against a minutes-old proposal surviving tag occlusion.")]
    [SerializeField] private float pendingProposalMaxAgeSeconds = 5f;

    [Header("Ghost preview")]
    [Tooltip("Translucent material for the placement ghost (URP Unlit transparent, palette cyan).")]
    [SerializeField] private Material ghostMaterial;

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
    private float _pendingSetTime;
    private Pose _latestProposed;
    private bool _hasLatest;
    private float _latestSetTime;
    private float _lastTagSeenTime = float.NegativeInfinity;
    private float _nextMeasurementLogTime;
    private Transform _cameraRef;
    private int _presetIndex;

    private IHudTransientSink _hud;
    private bool _hudSearched;

    private GameObject _ghost;
    private bool _previewActive;
    private float _lastCorrectionMm = -1f;

    // ---- public surface (chords + HUD + future web console) ----
    public bool IsPlaced => _placed;
    public bool HasTagCandidate => _hasLatest;
    public Transform ObstacleTransform => _obstacle;
    public TrackingVariant Variant => trackingVariant;
    public VisualUpdatePolicy Policy => visualPolicy;
    public TagSolverMode Solver => solverMode;
    public string SourceLabel => _solver != null ? _solver.SourceLabel : "(none)";

    /// <summary>Name of the active preset, or "custom" after an individual setter diverged.</summary>
    public string CurrentPresetName { get; private set; } = "custom";

    /// <summary>True between CapturePlacement() and the stable capture landing.</summary>
    public bool IsCaptureRequested => _captureRequested;

    // Capture-progress surface for the HUD guidance zone.
    public int CaptureSampleCount => _gate?.SampleCount ?? 0;
    public int CaptureWindowSize => gateWindowSize;
    public float CapturePositionSpreadMeters => _gate?.LastPositionSpread ?? 0f;
    public float CaptureRotationSpreadDegrees => _gate?.LastRotationSpread ?? 0f;

    /// <summary>Seconds since any tag was last detected (infinity if never).</summary>
    public float SecondsSinceLastTag => Time.time - _lastTagSeenTime;

    /// <summary>Magnitude of the last applied Deferred correction (mm); -1 if none yet.</summary>
    public float LastCorrectionMm => _lastCorrectionMm;

    /// <summary>Anchor state for the diagnostics zone: none / creating / created / localized.</summary>
    public string AnchorStatus
    {
        get
        {
            if (trackingVariant != TrackingVariant.Anchored) return "world-root";
            if (_anchor == null) return _placed ? "MISSING" : "none";
            if (_anchor.Localized) return "localized";
            if (_anchor.Created) return "created";
            return "creating";
        }
    }

    /// <summary>Latest stable solver proposal, if one exists.</summary>
    public bool TryGetLatestProposal(out Pose pose)
    {
        pose = _latestProposed;
        return _hasLatest;
    }

    /// <summary>Fired after the obstacle is (re)placed.</summary>
    public event Action OnPlaced;
    /// <summary>Fired when variant / policy / solver / preset changes (for HUD / web sync).</summary>
    public event Action OnConfigChanged;

    private void Awake()
    {
        if (!displayManager) displayManager = FindAnyObjectByType<AprilTagDisplayManager>();
        if (!obstacleController) obstacleController = FindAnyObjectByType<ObstacleController>();
        if (!finesseController) finesseController = FindAnyObjectByType<ObstacleFinesseController>();
    }

    private void Start()
    {
        if (!obstaclePrefab)
        {
            Debug.LogError("[ObstaclePlacement] No obstaclePrefab assigned. Disabling.");
            enabled = false;
            return;
        }

        // Boot into the first preset when one exists (keeps scene serialized
        // values as fallback when the list is empty).
        if (presets != null && presets.Length > 0)
        {
            _presetIndex = 0;
            ApplyPresetInternal(presets[0], logChange: false);
        }

        BuildChain();
        BuildSolver();

        if (obstacleController) obstacleController.SetManualTarget(_obstacle);
        if (finesseController) finesseController.SetManualTarget(_finesseOffset);
        if (obstacleController) obstacleController.OnObstacleReset += ApplyPendingCorrection;

        LogConfigChange("boot");
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
        if (_ghost != null) Destroy(_ghost);
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
        // Stamp visibility on ANY detection batch, before the solver filters by
        // ID/stability — the HUD's "tag last seen" reads this.
        if (detections != null && detections.Length > 0) _lastTagSeenTime = Time.time;

        if (_solver == null) return;
        if (!_solver.TryGetPose(detections, Time.time, out var proposed)) return;

        _latestProposed = proposed;
        _hasLatest = true;
        _latestSetTime = Time.time;

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
                _pendingSetTime = Time.time;
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

        if (_previewActive && _ghost != null && _hasLatest)
        {
            _ghost.transform.SetPositionAndRotation(_latestProposed.position, _latestProposed.rotation);
            if (!_ghost.activeSelf) _ghost.SetActive(true);
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
        EndPlacementPreview();
        Hud(autoReplace ? "Re-placing with new config…" : "Cleared — Place now to re-capture.");
    }

    // ---- ghost preview (public: controls call Begin on hold-start, End on cancel) ----

    /// <summary>Show the translucent ghost at the live tag-proposed pose.</summary>
    public void BeginPlacementPreview()
    {
        if (_placed) return;
        _previewActive = true;
        if (_ghost == null) _ghost = BuildGhost();
        // Shown by Update() once a stable proposal exists (avoids a ghost at origin).
        if (_ghost != null && !_hasLatest) _ghost.SetActive(false);
    }

    /// <summary>Hide the ghost (hold cancelled or placement committed).</summary>
    public void EndPlacementPreview()
    {
        _previewActive = false;
        if (_ghost != null) _ghost.SetActive(false);
    }

    private GameObject BuildGhost()
    {
        if (obstaclePrefab == null) return null;
        var ghost = Instantiate(obstaclePrefab);
        ghost.name = "Obstacle Ghost (preview)";

        // Strip everything that behaves: OcclusionSwapper reassigns materials at
        // runtime (would stomp the ghost material), colliders would collide.
        foreach (var mb in ghost.GetComponentsInChildren<MonoBehaviour>(true)) Destroy(mb);
        foreach (var col in ghost.GetComponentsInChildren<Collider>(true)) Destroy(col);

        if (ghostMaterial != null)
        {
            foreach (var r in ghost.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = ghostMaterial;
                r.sharedMaterials = mats;
            }
        }

        ghost.SetActive(false);
        return ghost;
    }

    // ---- runtime config (public for chords / web) ----

    /// <summary>Advance to the next preset in the list (wraps). Re-places if already placed.</summary>
    public void CyclePreset()
    {
        if (presets == null || presets.Length == 0)
        {
            Hud("No presets configured (Inspector list is empty)");
            return;
        }
        _presetIndex = (_presetIndex + 1) % presets.Length;
        ApplyPreset(presets[_presetIndex]);
    }

    /// <summary>Apply a named condition bundle as one action.</summary>
    public void ApplyPreset(ConditionPreset preset) => ApplyPresetInternal(preset, logChange: true);

    private void ApplyPresetInternal(ConditionPreset preset, bool logChange)
    {
        CurrentPresetName = string.IsNullOrEmpty(preset.name) ? "unnamed" : preset.name;

        bool solverChanged = preset.solver != solverMode;
        bool variantChanged = preset.variant != trackingVariant;

        visualPolicy = preset.policy;
        trackingVariant = preset.variant;
        solverMode = preset.solver;
        if (visualPolicy == VisualUpdatePolicy.Deferred) _hasPending = false;
        if (solverChanged && _gate != null) BuildSolver();

        OnConfigChanged?.Invoke();
        if (logChange)
        {
            LogConfigChange($"preset:{CurrentPresetName}");
            Hud($"Preset: <b>{CurrentPresetName}</b> · {solverMode} · {visualPolicy} · {trackingVariant}");
        }

        if (_placed && (solverChanged || variantChanged)) RecaptureAndReplace();
    }

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
        if (p == visualPolicy) return;
        visualPolicy = p;
        CurrentPresetName = "custom";
        if (p == VisualUpdatePolicy.Deferred) _hasPending = false;
        OnConfigChanged?.Invoke();
        LogConfigChange("set_policy");
        Hud($"Policy: {p}");
    }

    public void CycleTrackingVariant()
        => SetTrackingVariant(trackingVariant == TrackingVariant.Anchored ? TrackingVariant.WorldRoot : TrackingVariant.Anchored);

    public void SetTrackingVariant(TrackingVariant v)
    {
        if (v == trackingVariant) return;
        trackingVariant = v;
        CurrentPresetName = "custom";
        OnConfigChanged?.Invoke();
        LogConfigChange("set_variant");
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
        CurrentPresetName = "custom";
        BuildSolver();
        OnConfigChanged?.Invoke();
        LogConfigChange("set_solver");
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
        EndPlacementPreview();

        Debug.Log($"[ObstaclePlacement] Placed ({solverMode}, {trackingVariant}, {visualPolicy}) at {pose.position}.");
        Hud($"Placed · {solverMode} · {trackingVariant}");
        LogMeasurement(pose);

        if (SessionLogger.Instance != null)
        {
            SessionLogger.Instance.Enqueue(LogEvent.SessionEvent(
                "obstacle_placed",
                $"solver={_solver.SourceLabel};preset={CurrentPresetName};variant={trackingVariant};policy={visualPolicy};pos={Fmt(pose.position)}"));
        }

        OnPlaced?.Invoke();
    }

    /// <summary>Applies the held (Deferred) correction to the obstacle base between trials.</summary>
    private void ApplyPendingCorrection()
    {
        if (!_placed || visualPolicy != VisualUpdatePolicy.Deferred || !_hasPending) return;

        float age = Time.time - _pendingSetTime;
        if (age > pendingProposalMaxAgeSeconds)
        {
            // Tag hasn't been seen recently — the held proposal is stale
            // (headset drift since it was measured makes it wrong to apply).
            _hasPending = false;
            Debug.Log($"[ObstaclePlacement] Deferred correction skipped: proposal {age:F1}s old (> {pendingProposalMaxAgeSeconds:F0}s).");
            if (SessionLogger.Instance != null)
            {
                SessionLogger.Instance.Enqueue(LogEvent.CorrectionEvent(
                    _solver.SourceLabel, mode: "applied", accepted: false,
                    rejectionReason: "stale_proposal"));
            }
            return;
        }

        var beforePos = _tagOffset.position;
        var beforeRot = _tagOffset.rotation;
        _tagOffset.SetPositionAndRotation(_pendingProposed.position, _pendingProposed.rotation);
        _hasPending = false;

        float dPos = Vector3.Distance(beforePos, _pendingProposed.position);
        float dRot = Quaternion.Angle(beforeRot, _pendingProposed.rotation);
        _lastCorrectionMm = dPos * 1000f;
        Debug.Log($"[ObstaclePlacement] Deferred correction applied between trials: {dPos * 1000f:F1} mm / {dRot:F2} deg.");

        if (SessionLogger.Instance != null)
        {
            SessionLogger.Instance.Enqueue(LogEvent.CorrectionEvent(
                _solver.SourceLabel, mode: "applied", accepted: true,
                deltaPositionM: dPos, deltaRotationDeg: dRot, correctionAppliedM: dPos));
        }
    }

    private void LogConfigChange(string reason)
    {
        if (SessionLogger.Instance == null) return;
        SessionLogger.Instance.Enqueue(LogEvent.SessionEvent(
            "config_change",
            $"preset={CurrentPresetName};solver={solverMode};policy={visualPolicy};variant={trackingVariant};placed={(_placed ? 1 : 0)};reason={reason}"));
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
        if (!_hudSearched) { _hud = HudSink.Find(); _hudSearched = true; }
        _hud?.ShowTransient(msg, 3f);
    }

    private static string Fmt(Vector3 v)
        => string.Format(CultureInfo.InvariantCulture, "{0:F3}|{1:F3}|{2:F3}", v.x, v.y, v.z);
}
