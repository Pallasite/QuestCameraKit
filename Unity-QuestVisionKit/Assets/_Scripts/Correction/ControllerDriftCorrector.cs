using System;
using UnityEngine;

/// <summary>
/// Phase 2: self-anchored, gated, controller-derived drift correction. Spawns its
/// own obstacle from a prefab and continuously positions it at the
/// controller-midpoint-derived pose, with its own dedicated
/// <see cref="OVRSpatialAnchor"/> for SLAM holding. Runs in parallel to
/// <c>ConstellationDriftCorrector</c> (AprilTag) and <c>ControllerObstaclePlacer</c>
/// (native). **Totally independent of <c>ConstellationDriftCorrector</c>** — no
/// references, no shared anchor, no shared CorrectionRoot, no event subscriptions.
///
/// <para>Three-layer hierarchy, mirroring the placer + AprilTag pattern:</para>
/// <code>
/// ControllerCorrectionAnchor                    (created on Activate — OVRSpatialAnchor)
///   └── ControllerCorrectionCorrectionRoot      (corrector writes here every LateUpdate)
///         └── SpawnedObstacle                    (a separate prefab instance; finesse can write localPose here)
/// </code>
///
/// <para><b>Lifecycle</b>: Spawn (Start, no anchor) → Follow mode (wrapper tracks
/// midpoint) → Activate (anchor created, reference captured, switches to Corrected
/// mode) → Corrected mode (per-frame gated correction in LateUpdate) → Deactivate
/// (anchor destroyed, back to Follow). Safe to re-activate.</para>
///
/// <para><b>Hybrid activation policy</b>: auto-activates into <see cref="CorrectionWriteMode.Observe"/>
/// the moment <c>provider.Left/RightPositionValid &amp;&amp; rigidBodyValidator.HasBaseline</c>
/// — gates evaluate + logs flow, no obstacle motion. Promotion to
/// <see cref="CorrectionWriteMode.Applied"/> is manual (Inspector,
/// <c>[ContextMenu]</c>, future <c>CorrectionModeManager</c>).</para>
///
/// <para><b>API axes</b> (independent, future-UI-driven): <see cref="WriteMode"/>
/// (Disabled / Observe / Applied), <see cref="CorrectionEnabled"/> (raw pose
/// pass-through vs full algorithm), <see cref="EnabledGates"/> (per-gate bitfield
/// for isolation testing). All changeable at runtime; events fire on change.</para>
///
/// <para><b>Drift hypothesis</b>: the rig-mounted controllers are physically
/// stationary; their reported pose is a high-precision reference for the local
/// SLAM frame. Writing
/// <c>SetPositionAndRotation(currentMidpoint + offset, currentBaselineYaw * rotOffset)</c>
/// to a CorrectionRoot under a drifting anchor makes the obstacle's world pose
/// follow the controllers — so when the anchor drifts (SLAM moves it), the
/// CorrectionRoot's localPose adjusts to compensate, keeping the obstacle where
/// the controllers say it should be.</para>
/// </summary>
[DisallowMultipleComponent]
public sealed class ControllerDriftCorrector : MonoBehaviour
{
    public enum CorrectionWriteMode
    {
        /// <summary>No gate evaluation, no logging, no write. Cheapest.</summary>
        Disabled,
        /// <summary>Gates evaluated and logged. No write to CorrectionRoot — obstacle does not move.</summary>
        Observe,
        /// <summary>Gates evaluated, logged, and accepted corrections written to CorrectionRoot.</summary>
        Applied,
    }

    [Flags]
    public enum GateFlags
    {
        None = 0,
        Validity = 1 << 0,
        Range = 1 << 1,
        Velocity = 1 << 2,
        RigidBody = 1 << 3,
        Facing = 1 << 4,
        StepOver = 1 << 5,
        All = Validity | Range | Velocity | RigidBody | Facing | StepOver,
    }

    // ======================================================================
    // Inspector wiring
    // ======================================================================

    [Header("Wiring")]
    [Tooltip("Source of controller poses + velocities. Auto-resolved if empty.")]
    [SerializeField] private ControllerPoseProvider provider;

    [Tooltip("Rigid-body baseline gate input + recapture-reference trigger. Auto-resolved if empty.")]
    [SerializeField] private ControllerRigidBodyValidator rigidBodyValidator;

    [Tooltip("Prefab to instantiate as this corrector's own obstacle. On Start(), one instance is " +
             "spawned (named '<prefab> (drift-corrector)') as a child of the corrector's own " +
             "CorrectionRoot wrapper. Use a visually-distinct prefab so it's easy to tell apart from " +
             "the AprilTag-spawned and placer-spawned obstacles.")]
    [SerializeField] private GameObject obstaclePrefab;

    [Tooltip("Optional. Diagnostic status echoes here on the future mode-coordinator's request. Auto-resolved if empty.")]
    [SerializeField] private PipelineStatusHUD hud;

    [Tooltip("Used for the facing + step-over gates. Auto-resolved to Camera.main.")]
    [SerializeField] private Camera headsetCamera;

    // ======================================================================
    // Placement geometry (applied to obstacle pose in both follow and corrected modes)
    // ======================================================================

    [Header("Placement geometry")]
    [Tooltip("Vertical offset added to the controller midpoint (meters). Drops the obstacle from " +
             "controller/rig height down onto the gait mat.")]
    [SerializeField] private float verticalOffsetMeters = 0f;

    [Tooltip("Euler rotation offset applied after the perpendicular yaw. (0,180,0) flips the obstacle " +
             "if it ends up facing toward the user instead of along the walking direction, or compensates " +
             "for a prefab whose long axis isn't its local X.")]
    [SerializeField] private Vector3 rotationOffsetEuler = Vector3.zero;

    // ======================================================================
    // Gate thresholds (Inspector-tunable; defaults from the original handoff spec)
    // ======================================================================

    [Header("Gate thresholds")]
    [Tooltip("Reject if either controller is farther than this from the headset (meters).")]
    [SerializeField] private float workingRangeMeters = 2.0f;

    [Tooltip("Reject if either controller's self-computed linear velocity exceeds this (m/s). " +
             "Catches IMU dead-reckoning while the rig should be physically static.")]
    [SerializeField] private float maxVelocityMps = 0.02f;

    [Tooltip("Reject when the headset projected onto the walk axis is within this distance of the " +
             "obstacle (meters). Suppresses corrections during the actual step-over event.")]
    [SerializeField] private float stepOverZoneMeters = 0.30f;

    // ======================================================================
    // Filter / smoothing (post-gate)
    // ======================================================================

    [Header("Filter / smoothing")]
    [Tooltip("EMA lerp coefficient applied to the desired obstacle world pose each frame. " +
             "Lower = smoother, slower to converge. Spec default 0.05.")]
    [SerializeField, Range(0.001f, 1f)] private float emaAlpha = 0.05f;

    [Tooltip("If the new desired position differs from the EMA-smoothed value by more than this " +
             "(meters), bypass EMA and snap. Logs a snap_event row.")]
    [SerializeField] private float snapThresholdMeters = 0.015f;

    [Tooltip("Rotation snap threshold (degrees).")]
    [SerializeField] private float snapThresholdDegrees = 10f;

    [Tooltip("Hard ceiling on the per-second translation delta written to the CorrectionRoot (m/s).")]
    [SerializeField] private float maxCorrectionRateMps = 0.05f;

    [Tooltip("Hard ceiling on the per-second rotation delta (deg/s).")]
    [SerializeField] private float maxCorrectionRateDegPerSec = 5f;

    // ======================================================================
    // Boot-time behavior (initial values for the runtime-changeable axes)
    // ======================================================================

    [Header("Boot-time behavior (changeable at runtime)")]
    [Tooltip("When true, auto-activates the moment its own conditions are met (both controllers valid " +
             "AND rigidBodyValidator.HasBaseline). No AprilTag dependency.")]
    [SerializeField] private bool autoActivate = true;

    [Tooltip("Write mode on activation. Hybrid policy default: Observe — gates evaluate + log, no " +
             "obstacle motion. Promote to Applied manually after reviewing a session of log data.")]
    [SerializeField] private CorrectionWriteMode startWriteMode = CorrectionWriteMode.Observe;

    [Tooltip("CorrectionEnabled on activation. true = full algorithm (gates → EMA → snap). " +
             "false = raw pose pass-through (no gates/EMA/snap; obstacle follows raw controller " +
             "midpoint). A/B baseline for validating the correction logic helps.")]
    [SerializeField] private bool startCorrectionEnabled = true;

    [Tooltip("Initial gate enable set. The future UI will expose this as per-gate checkboxes.")]
    [SerializeField] private GateFlags startEnabledGates = GateFlags.All;

    // ======================================================================
    // Logging cadence
    // ======================================================================

    [Header("Anchor logging")]
    [Tooltip("State_snapshot rows are emitted at this rate while anchored (correction_source=controller). " +
             "Mirrors the placer's 5Hz so the corrected anchor's pose can be compared against " +
             "anchor_baseline and controller_placer streams in offline analysis.")]
    [SerializeField, Range(1f, 30f)] private float anchorSnapshotRateHz = 5f;

    // ======================================================================
    // Public state (read-only)
    // ======================================================================

    /// <summary>True once Activate() has run and the anchor + correction root exist.</summary>
    public bool IsAnchored => _anchorGo != null;

    /// <summary>True when the most recent LateUpdate's gate stack accepted a frame
    /// (regardless of <see cref="WriteMode"/>). Reset on reject. To check whether a
    /// write actually happened, use <c>IsActive &amp;&amp; WriteMode == Applied</c>.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Whether result is written to CorrectionRoot. See <see cref="CorrectionWriteMode"/>.</summary>
    public CorrectionWriteMode WriteMode
    {
        get => _writeMode;
        set
        {
            if (_writeMode == value) return;
            _writeMode = value;
            EmitSourceStateChange("write_mode", value.ToString());
            OnWriteModeChanged?.Invoke(value);
        }
    }

    /// <summary>true = full correction algorithm; false = raw pose pass-through (no gates/EMA/snap).</summary>
    public bool CorrectionEnabled
    {
        get => _correctionEnabled;
        set
        {
            if (_correctionEnabled == value) return;
            _correctionEnabled = value;
            EmitSourceStateChange("correction_enabled", value ? "1" : "0");
            OnCorrectionEnabledChanged?.Invoke(value);
        }
    }

    /// <summary>Per-gate enable bitfield. Future UI exposes as checkboxes.</summary>
    public GateFlags EnabledGates
    {
        get => _enabledGates;
        set
        {
            if (_enabledGates == value) return;
            _enabledGates = value;
            EmitSourceStateChange("enabled_gates", ((int)value).ToString());
            OnEnabledGatesChanged?.Invoke(value);
        }
    }

    /// <summary>The spawned obstacle's Transform (finesse target — child of CorrectionRoot).</summary>
    public Transform SpawnedObstacle => _spawnedObstacle;

    /// <summary>The CorrectionRoot wrapper between the anchor and the obstacle.</summary>
    public Transform CorrectionRoot => _correctionRoot;

    /// <summary>The dedicated OVRSpatialAnchor, or null while in follow mode.</summary>
    public OVRSpatialAnchor Anchor => _anchor;

    /// <summary>World-space perpendicular to the controller baseline, in the floor plane.
    /// Captured at <see cref="Activate"/> + <see cref="RecaptureReference"/>; used by the
    /// facing + step-over gates. Zero in follow mode.</summary>
    public Vector3 WalkAxisReference => _walkAxisRef;

    // ======================================================================
    // Events (future CorrectionModeManager + per-mode UI subscribes)
    // ======================================================================

    public event Action OnActivated;
    public event Action OnDeactivated;
    public event Action<CorrectionWriteMode> OnWriteModeChanged;
    public event Action<bool> OnCorrectionEnabledChanged;
    public event Action<GateFlags> OnEnabledGatesChanged;

    // ======================================================================
    // Private state
    // ======================================================================

    private Transform _correctionRoot;
    private Transform _spawnedObstacle;
    private GameObject _anchorGo;
    private OVRSpatialAnchor _anchor;

    private CorrectionWriteMode _writeMode;
    private bool _correctionEnabled;
    private GateFlags _enabledGates;

    // Reference state captured at Activate / RecaptureReference.
    private Vector3 _midpointRef;            // for log convenience only; correction math doesn't need it
    private Quaternion _baselineYawRef;      // for log convenience only
    private Vector3 _walkAxisRef;            // for facing + step-over gates
    private bool _referenceCaptured;

    // EMA state.
    private Pose _emaWorld;
    private bool _emaInitialized;

    // Headset velocity tracking (for the facing gate's velocity-along-axis check).
    private Vector3 _prevHeadsetPos;
    private bool _havePrevHeadset;
    private Vector3 _headsetVelocity;

    // Logging cadence.
    private float _nextAnchorSnapshotTime;

    // Source-state-change debouncing.
    private bool _wasActive;
    private bool _haveLoggedNoHeadsetWarning;

    // Subscription guard.
    private bool _subscribed;

    // ======================================================================
    // Unity lifecycle
    // ======================================================================

    private void Awake()
    {
        if (!provider) provider = FindAnyObjectByType<ControllerPoseProvider>();
        if (!rigidBodyValidator) rigidBodyValidator = FindAnyObjectByType<ControllerRigidBodyValidator>();
        if (!hud) hud = FindAnyObjectByType<PipelineStatusHUD>();
        if (!headsetCamera) headsetCamera = Camera.main;

        _writeMode = startWriteMode;
        _correctionEnabled = startCorrectionEnabled;
        _enabledGates = startEnabledGates;
    }

    private void OnEnable()
    {
        SubscribeToBaselineEvent();
    }

    private void OnDisable()
    {
        UnsubscribeFromBaselineEvent();
        // Tear down the anchor cleanly on disable so we don't leave a stray OVRSpatialAnchor running.
        if (IsAnchored) Deactivate();
    }

    private void Start()
    {
        if (obstaclePrefab == null)
        {
            Debug.LogWarning("[ControllerDriftCorrector] No obstaclePrefab assigned. Drag a prefab into the Inspector.");
            EchoToHud("<color=#FF8888>ControllerDriftCorrector: no prefab assigned</color>");
            return;
        }
        if (provider == null)
        {
            Debug.LogError("[ControllerDriftCorrector] No ControllerPoseProvider found. Disabling.");
            enabled = false;
            return;
        }

        // Spawn the wrapper + obstacle at scene root, identical to the placer.
        var rootGo = new GameObject("ControllerCorrectionCorrectionRoot");
        _correctionRoot = rootGo.transform;

        var go = Instantiate(obstaclePrefab, _correctionRoot);
        go.name = $"{obstaclePrefab.name} (drift-corrector)";
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        _spawnedObstacle = go.transform;

        EchoToHud($"<color=#88FF88>Spawned '{obstaclePrefab.name}' for drift corrector</color>");
        Debug.Log($"[ControllerDriftCorrector] Spawned '{obstaclePrefab.name}' under ControllerCorrectionCorrectionRoot.");
    }

    private void OnDestroy()
    {
        UnsubscribeFromBaselineEvent();
        if (_anchorGo != null) Destroy(_anchorGo);
        if (_correctionRoot != null && _correctionRoot.gameObject != null)
            Destroy(_correctionRoot.gameObject);
    }

    // ======================================================================
    // Per-frame correction (LateUpdate runs after ControllerPoseProvider's Update)
    // ======================================================================

    private void LateUpdate()
    {
        if (provider == null || _correctionRoot == null) return;

        UpdateHeadsetVelocity();

        // Auto-activate when own conditions are met — controllers valid + baseline captured.
        // No AprilTag dependency.
        if (autoActivate && !IsAnchored && CanActivate())
            Activate();

        if (!IsAnchored)
        {
            // Follow mode — wrapper tracks the live controller-midpoint pose, no anchor.
            FollowMidpoint();
            return;
        }

        // Anchored: run the corrected-mode logic per WriteMode.
        if (_writeMode == CorrectionWriteMode.Disabled)
        {
            // Cheapest path: anchor exists but no logic running.
            if (_wasActive) { _wasActive = false; EmitSourceStateChange("active", "0"); }
            // Anchor-snapshot 5Hz still runs even when Disabled — the anchor's own pose is data.
            MaybeEmitAnchorSnapshot();
            return;
        }

        // Gates run for both Observe and Applied.
        var decision = EvaluateGates();
        IsActive = decision.accepted;

        // Compute the desired obstacle world pose this frame.
        Vector3 currentMidpoint = ComputeMidpoint();
        Quaternion currentYaw = ComputeBaselineYaw();
        Vector3 desiredPos = currentMidpoint + new Vector3(0f, verticalOffsetMeters, 0f);
        Quaternion desiredRot = currentYaw * Quaternion.Euler(rotationOffsetEuler);

        // "Proposed" delta = how far off the obstacle is from its desired pose right now.
        // Measured relative to the CorrectionRoot's pre-write world pose — i.e. the raw
        // drift signal this frame. Meaningful in all modes (Observe, Applied, rejected).
        Vector3 prevCorrectionRootPos = _correctionRoot.position;
        Quaternion prevCorrectionRootRot = _correctionRoot.rotation;
        float proposedDeltaPos = Vector3.Distance(desiredPos, prevCorrectionRootPos);
        float proposedDeltaRot = Quaternion.Angle(desiredRot, prevCorrectionRootRot);

        bool snapped = false;
        float emaAlphaUsed = 0f;

        if (decision.accepted)
        {
            if (_correctionEnabled)
            {
                // Update the EMA regardless of WriteMode — gives a "would-write" pose
                // in Observe mode, so the log captures what the algorithm would have
                // done. Only Applied mode actually writes to CorrectionRoot.
                if (!_emaInitialized)
                {
                    _emaWorld = new Pose(desiredPos, desiredRot);
                    _emaInitialized = true;
                }

                float posSnapDelta = Vector3.Distance(desiredPos, _emaWorld.position);
                float rotSnapDelta = Quaternion.Angle(desiredRot, _emaWorld.rotation);
                if (posSnapDelta > snapThresholdMeters || rotSnapDelta > snapThresholdDegrees)
                {
                    snapped = true;
                    _emaWorld = new Pose(desiredPos, desiredRot);
                    emaAlphaUsed = 1f;
                }
                else
                {
                    _emaWorld = new Pose(
                        Vector3.Lerp(_emaWorld.position, desiredPos, emaAlpha),
                        Quaternion.Slerp(_emaWorld.rotation, desiredRot, emaAlpha));
                    emaAlphaUsed = emaAlpha;
                }

                if (_writeMode == CorrectionWriteMode.Applied)
                {
                    // Max-rate cap, then write.
                    ApplyMaxRateClamp(ref _emaWorld, Time.deltaTime);
                    _correctionRoot.SetPositionAndRotation(_emaWorld.position, _emaWorld.rotation);
                }
                // Observe: EMA updated, nothing written.
            }
            else
            {
                // Raw pose pass-through — bypass EMA/snap entirely.
                _emaWorld = new Pose(desiredPos, desiredRot);
                _emaInitialized = true;
                emaAlphaUsed = 1f;
                if (_writeMode == CorrectionWriteMode.Applied)
                {
                    _correctionRoot.SetPositionAndRotation(desiredPos, desiredRot);
                }
            }
        }
        // else: rejected. EMA / CorrectionRoot unchanged.

        // "Applied" delta = how much the CorrectionRoot's world pose actually moved
        // this frame. Zero in Observe mode and on rejected frames.
        float appliedDeltaPos = Vector3.Distance(_correctionRoot.position, prevCorrectionRootPos);
        float appliedDeltaRot = Quaternion.Angle(_correctionRoot.rotation, prevCorrectionRootRot);

        LogCorrectionEvent(decision, proposedDeltaPos, proposedDeltaRot,
                           appliedDeltaPos, appliedDeltaRot, emaAlphaUsed);

        if (snapped) LogSnapEvent(desiredPos, desiredRot);

        // Source-state-change on IsActive transitions.
        if (IsActive != _wasActive)
        {
            _wasActive = IsActive;
            EmitSourceStateChange("active", IsActive ? "1" : "0");
        }

        MaybeEmitAnchorSnapshot();
    }

    // ======================================================================
    // Public API — lifecycle + runtime axes
    // ======================================================================

    /// <summary>True when activation conditions are met (controllers valid + baseline captured).</summary>
    public bool CanActivate()
    {
        if (provider == null) return false;
        if (rigidBodyValidator == null) return false;
        if (!provider.LeftPositionValid || !provider.RightPositionValid) return false;
        if (!rigidBodyValidator.HasBaseline) return false;
        return true;
    }

    /// <summary>
    /// Create the dedicated <see cref="OVRSpatialAnchor"/>, capture the reference
    /// state, and switch to corrected mode. Idempotent — no-op when already anchored.
    /// </summary>
    [ContextMenu("Activate")]
    public void Activate()
    {
        if (IsAnchored) { Debug.LogWarning("[ControllerDriftCorrector] Already anchored; Activate() is a no-op."); return; }
        if (_correctionRoot == null) { Debug.LogError("[ControllerDriftCorrector] No correction root; cannot activate."); return; }
        if (!CanActivate()) { Debug.LogError("[ControllerDriftCorrector] CanActivate() is false; cannot activate."); return; }

        // First put the wrapper at the current desired pose so the anchor lands there.
        Vector3 midpoint = ComputeMidpoint();
        Quaternion yaw = ComputeBaselineYaw();
        Vector3 spawnPos = midpoint + new Vector3(0f, verticalOffsetMeters, 0f);
        Quaternion spawnRot = yaw * Quaternion.Euler(rotationOffsetEuler);
        _correctionRoot.SetPositionAndRotation(spawnPos, spawnRot);

        _anchorGo = new GameObject("ControllerCorrectionAnchor");
        _anchorGo.transform.SetPositionAndRotation(_correctionRoot.position, _correctionRoot.rotation);
        _anchor = _anchorGo.AddComponent<OVRSpatialAnchor>();

        // Reparent the CorrectionRoot under the anchor, preserving its world pose.
        _correctionRoot.SetParent(_anchorGo.transform, worldPositionStays: true);

        CaptureReferenceState();
        _emaInitialized = false;
        _nextAnchorSnapshotTime = 0f;

        Debug.Log($"[ControllerDriftCorrector] Activated at {_anchorGo.transform.position}. WriteMode={_writeMode} CorrectionEnabled={_correctionEnabled} EnabledGates={_enabledGates}");
        EchoToHud($"<color=#88FFFF>Drift corrector activated ({_writeMode})</color>");

        if (SessionLogger.Instance != null)
        {
            string detail = $"midpoint_ref={Vec3Str(_midpointRef)};baseline_yaw_ref={QuatStr(_baselineYawRef)};" +
                            $"walk_axis_ref={Vec3Str(_walkAxisRef)};write_mode={_writeMode};" +
                            $"correction_enabled={(_correctionEnabled ? 1 : 0)};enabled_gates={(int)_enabledGates}";
            SessionLogger.Instance.Enqueue(LogEvent.SessionEvent("controller_corrector_activated", detail));
        }
        EmitSourceStateChange("activated", _writeMode.ToString());
        OnActivated?.Invoke();
    }

    /// <summary>
    /// Destroy the dedicated anchor and detach the wrapper. Safe to call when not
    /// anchored (no-op). Follow mode resumes the next LateUpdate.
    /// </summary>
    [ContextMenu("Deactivate")]
    public void Deactivate()
    {
        if (!IsAnchored) return;

        if (_correctionRoot != null && _correctionRoot.gameObject != null)
            _correctionRoot.SetParent(null, worldPositionStays: true);

        if (_anchorGo != null) Destroy(_anchorGo);
        _anchorGo = null;
        _anchor = null;
        _referenceCaptured = false;
        _emaInitialized = false;
        IsActive = false;

        Debug.Log("[ControllerDriftCorrector] Deactivated.");
        EchoToHud("<color=#FFFF88>Drift corrector deactivated</color>");

        if (SessionLogger.Instance != null)
            SessionLogger.Instance.Enqueue(LogEvent.SessionEvent("controller_corrector_deactivated"));
        EmitSourceStateChange("deactivated", null);
        OnDeactivated?.Invoke();
    }

    /// <summary>
    /// Re-capture the reference state (walk axis, midpoint/yaw for log convenience) and
    /// reset the EMA. Called automatically when the rig-baseline is recaptured (the
    /// controllers physically moved — old reference is stale); also exposed for
    /// manual reset.
    /// </summary>
    [ContextMenu("Recapture Reference")]
    public void RecaptureReference()
    {
        if (provider == null) return;
        if (!provider.LeftPositionValid || !provider.RightPositionValid)
        {
            Debug.LogWarning("[ControllerDriftCorrector] RecaptureReference() skipped — controller poses invalid.");
            return;
        }

        CaptureReferenceState();
        _emaInitialized = false;
        Debug.Log("[ControllerDriftCorrector] Reference recaptured.");
        if (SessionLogger.Instance != null)
        {
            string detail = $"midpoint_ref={Vec3Str(_midpointRef)};baseline_yaw_ref={QuatStr(_baselineYawRef)};" +
                            $"walk_axis_ref={Vec3Str(_walkAxisRef)}";
            SessionLogger.Instance.Enqueue(LogEvent.SessionEvent("controller_corrector_recapture", detail));
        }
    }

    // ContextMenu shortcuts for the runtime axes.
    [ContextMenu("Write Mode → Applied")]  private void SetWriteModeApplied()  => WriteMode = CorrectionWriteMode.Applied;
    [ContextMenu("Write Mode → Observe")]  private void SetWriteModeObserve()  => WriteMode = CorrectionWriteMode.Observe;
    [ContextMenu("Write Mode → Disabled")] private void SetWriteModeDisabled() => WriteMode = CorrectionWriteMode.Disabled;
    [ContextMenu("Toggle CorrectionEnabled")] private void ToggleCorrectionEnabled() => CorrectionEnabled = !CorrectionEnabled;
    [ContextMenu("Enable All Gates")]      private void SetAllGates()         => EnabledGates = GateFlags.All;
    [ContextMenu("Disable All Gates")]     private void SetNoGates()          => EnabledGates = GateFlags.None;

    // ======================================================================
    // Internals — gate evaluation
    // ======================================================================

    private struct GateDecision
    {
        public bool accepted;
        public string rejectionReason;        // null when accepted
        public float controllerDistanceM;     // max(headset→L, headset→R) — for logging
        public float controllerVelocityMps;   // max(|LVel|, |RVel|) — for logging
    }

    private GateDecision EvaluateGates()
    {
        var d = new GateDecision { accepted = true, rejectionReason = null };

        Vector3 leftVel = provider.LeftVelocity;
        Vector3 rightVel = provider.RightVelocity;
        d.controllerVelocityMps = Mathf.Max(leftVel.magnitude, rightVel.magnitude);

        Vector3 headsetPos = headsetCamera != null ? headsetCamera.transform.position : Vector3.zero;
        if (headsetCamera != null)
        {
            float lDist = Vector3.Distance(headsetPos, provider.LeftPose.position);
            float rDist = Vector3.Distance(headsetPos, provider.RightPose.position);
            d.controllerDistanceM = Mathf.Max(lDist, rDist);
        }

        // 1. Validity
        if ((_enabledGates & GateFlags.Validity) != 0)
        {
            if (!provider.LeftPositionValid)    return Reject(d, "validity_L");
            if (!provider.RightPositionValid)   return Reject(d, "validity_R");
            if (!provider.LeftOrientationValid) return Reject(d, "orientation_L");
            if (!provider.RightOrientationValid)return Reject(d, "orientation_R");
        }

        // 2. Working range
        if ((_enabledGates & GateFlags.Range) != 0 && headsetCamera != null)
        {
            if (Vector3.Distance(headsetPos, provider.LeftPose.position) > workingRangeMeters)
                return Reject(d, "range_L");
            if (Vector3.Distance(headsetPos, provider.RightPose.position) > workingRangeMeters)
                return Reject(d, "range_R");
        }

        // 3. Velocity sanity
        if ((_enabledGates & GateFlags.Velocity) != 0)
        {
            if (leftVel.magnitude > maxVelocityMps) return Reject(d, "velocity_L");
            if (rightVel.magnitude > maxVelocityMps) return Reject(d, "velocity_R");
        }

        // 4. Rigid body
        if ((_enabledGates & GateFlags.RigidBody) != 0 && rigidBodyValidator != null)
        {
            if (rigidBodyValidator.ValidationEnforced && !rigidBodyValidator.CurrentInTolerance)
                return Reject(d, "rigid_body");
        }

        // 5. Facing (position-based)
        if ((_enabledGates & GateFlags.Facing) != 0 && headsetCamera != null && _referenceCaptured && _spawnedObstacle != null)
        {
            Vector3 obstaclePos = _spawnedObstacle.position;
            float headOnAxis = Vector3.Dot(headsetPos - obstaclePos, _walkAxisRef);
            float velOnAxis = Vector3.Dot(_headsetVelocity, _walkAxisRef);
            // Reject when the headset is on the same side of the obstacle as its
            // direction of motion (i.e. past the obstacle and moving away).
            // sign(headOnAxis) == sign(velOnAxis) && both non-trivial.
            if (Mathf.Abs(velOnAxis) > 0.05f && Mathf.Sign(headOnAxis) == Mathf.Sign(velOnAxis))
                return Reject(d, "facing");
        }

        // 6. Step-over
        if ((_enabledGates & GateFlags.StepOver) != 0 && headsetCamera != null && _referenceCaptured && _spawnedObstacle != null)
        {
            Vector3 obstaclePos = _spawnedObstacle.position;
            float axisDist = Mathf.Abs(Vector3.Dot(headsetPos - obstaclePos, _walkAxisRef));
            if (axisDist < stepOverZoneMeters)
                return Reject(d, "step_over");
        }

        return d;
    }

    private static GateDecision Reject(GateDecision d, string reason)
    {
        d.accepted = false;
        d.rejectionReason = reason;
        return d;
    }

    // ======================================================================
    // Internals — math helpers
    // ======================================================================

    private Vector3 ComputeMidpoint()
    {
        return (provider.LeftPose.position + provider.RightPose.position) * 0.5f;
    }

    private Quaternion ComputeBaselineYaw()
    {
        Vector3 baseline = provider.RightPose.position - provider.LeftPose.position;
        Vector3 forward = Vector3.Cross(baseline, Vector3.up);
        if (forward.sqrMagnitude < 1e-6f)
            return _correctionRoot != null ? _correctionRoot.rotation : Quaternion.identity;
        return Quaternion.LookRotation(forward, Vector3.up);
    }

    private Vector3 ComputeWalkAxis()
    {
        // Perpendicular to baseline, in the floor plane.
        Vector3 baseline = provider.RightPose.position - provider.LeftPose.position;
        Vector3 axis = Vector3.Cross(baseline, Vector3.up);
        axis.y = 0f;
        if (axis.sqrMagnitude < 1e-6f) return Vector3.forward;
        return axis.normalized;
    }

    private void CaptureReferenceState()
    {
        _midpointRef = ComputeMidpoint();
        _baselineYawRef = ComputeBaselineYaw();
        _walkAxisRef = ComputeWalkAxis();
        _referenceCaptured = true;
    }

    private void FollowMidpoint()
    {
        if (!provider.LeftPositionValid || !provider.RightPositionValid) return;

        Vector3 midpoint = ComputeMidpoint();
        Quaternion yaw = ComputeBaselineYaw();
        Vector3 desiredPos = midpoint + new Vector3(0f, verticalOffsetMeters, 0f);
        Quaternion desiredRot = yaw * Quaternion.Euler(rotationOffsetEuler);
        _correctionRoot.SetPositionAndRotation(desiredPos, desiredRot);
    }

    private void ApplyMaxRateClamp(ref Pose target, float deltaTime)
    {
        // Read the actual current CorrectionRoot world pose (what got written last frame).
        Vector3 currentPos = _correctionRoot.position;
        Quaternion currentRot = _correctionRoot.rotation;

        // Position clamp.
        float maxPosStep = maxCorrectionRateMps * Mathf.Max(deltaTime, 1e-6f);
        Vector3 posDelta = target.position - currentPos;
        if (posDelta.magnitude > maxPosStep)
            target.position = currentPos + posDelta.normalized * maxPosStep;

        // Rotation clamp.
        float maxRotStep = maxCorrectionRateDegPerSec * Mathf.Max(deltaTime, 1e-6f);
        float rotDelta = Quaternion.Angle(currentRot, target.rotation);
        if (rotDelta > maxRotStep && rotDelta > 1e-4f)
        {
            float t = maxRotStep / rotDelta;
            target.rotation = Quaternion.Slerp(currentRot, target.rotation, t);
        }
    }

    private void UpdateHeadsetVelocity()
    {
        if (headsetCamera == null)
        {
            if (!_haveLoggedNoHeadsetWarning)
            {
                Debug.LogWarning("[ControllerDriftCorrector] No headset Camera found — facing + step-over gates will fail-open.");
                _haveLoggedNoHeadsetWarning = true;
            }
            return;
        }
        Vector3 pos = headsetCamera.transform.position;
        float dt = Time.deltaTime;
        if (_havePrevHeadset && dt > 1e-6f)
            _headsetVelocity = (pos - _prevHeadsetPos) / dt;
        else
            _headsetVelocity = Vector3.zero;
        _prevHeadsetPos = pos;
        _havePrevHeadset = true;
    }

    // ======================================================================
    // Logging
    // ======================================================================

    private void LogCorrectionEvent(GateDecision decision,
                                    float proposedDeltaPos, float proposedDeltaRot,
                                    float appliedDeltaPos, float appliedDeltaRot,
                                    float emaAlphaUsed)
    {
        if (SessionLogger.Instance == null) return;

        string modeStr = WriteModeToString(_writeMode);

        var row = LogEvent.CorrectionEvent(
            correctionSource: "controller",
            mode: modeStr,
            accepted: decision.accepted,
            rejectionReason: decision.rejectionReason,
            deltaPositionM: proposedDeltaPos,
            deltaRotationDeg: proposedDeltaRot,
            emaAlphaApplied: emaAlphaUsed,
            correctionAppliedM: appliedDeltaPos,
            controllerDistanceM: decision.controllerDistanceM,
            controllerVelocityMps: decision.controllerVelocityMps);
        // Note: appliedDeltaRot is also useful but the CorrectionEvent schema only
        // has one rotation field (delta_rotation_deg). The proposed delta wins —
        // it's the raw signal. The applied rotation delta is reconstructable from
        // consecutive state_snapshot rows of the anchor / correction-root pose.
        SessionLogger.Instance.Enqueue(row);
    }

    private static string WriteModeToString(CorrectionWriteMode m) => m switch
    {
        CorrectionWriteMode.Applied  => "applied",
        CorrectionWriteMode.Observe  => "observe",
        CorrectionWriteMode.Disabled => "disabled",
        _ => "n/a",
    };

    private void LogSnapEvent(Vector3 desiredPos, Quaternion desiredRot)
    {
        if (SessionLogger.Instance == null) return;
        float deltaPos = Vector3.Distance(desiredPos, _emaWorld.position);
        float deltaRot = Quaternion.Angle(desiredRot, _emaWorld.rotation);
        SessionLogger.Instance.Enqueue(LogEvent.SnapEvent("controller", deltaPos, deltaRot));
    }

    private void MaybeEmitAnchorSnapshot()
    {
        if (SessionLogger.Instance == null || !IsAnchored) return;
        if (Time.unscaledTime < _nextAnchorSnapshotTime) return;
        _nextAnchorSnapshotTime = Time.unscaledTime + 1f / Mathf.Max(0.1f, anchorSnapshotRateHz);

        var e = LogEvent.StateSnapshot("controller", mode: "applied");
        e.AnchorPos = _anchorGo.transform.position;
        e.AnchorRot = _anchorGo.transform.rotation;
        SessionLogger.Instance.Enqueue(e);
    }

    private void EmitSourceStateChange(string newState, string reason)
    {
        if (SessionLogger.Instance == null) return;
        SessionLogger.Instance.Enqueue(LogEvent.SourceStateChange("controller", WriteModeToString(_writeMode), newState, reason));
    }

    // ======================================================================
    // Event subscriptions
    // ======================================================================

    private void SubscribeToBaselineEvent()
    {
        if (_subscribed || rigidBodyValidator == null) return;
        rigidBodyValidator.OnBaselineCaptured += HandleBaselineCaptured;
        _subscribed = true;
    }

    private void UnsubscribeFromBaselineEvent()
    {
        if (!_subscribed || rigidBodyValidator == null) return;
        rigidBodyValidator.OnBaselineCaptured -= HandleBaselineCaptured;
        _subscribed = false;
    }

    private void HandleBaselineCaptured()
    {
        // The controllers physically moved (rig was recalibrated). Any captured
        // reference is now stale; re-capture if we're already activated.
        if (_referenceCaptured) RecaptureReference();
    }

    // ======================================================================
    // Tiny helpers
    // ======================================================================

    private void EchoToHud(string message)
    {
        if (hud != null) hud.ShowTransient(message, 5f);
    }

    private static string Vec3Str(Vector3 v) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F4}|{1:F4}|{2:F4}", v.x, v.y, v.z);

    private static string QuatStr(Quaternion q) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F4}|{1:F4}|{2:F4}|{3:F4}", q.x, q.y, q.z, q.w);
}
