using UnityEngine;

/// <summary>
/// Spawns a user-supplied prefab as a runtime obstacle and positions it at the
/// midpoint of the two Touch controllers, with the obstacle's local +Z aligned
/// *perpendicular* to the inter-controller baseline (i.e., facing the user's
/// walking direction, not along the rig line). A gate-free testing slice of
/// the Phase 2 controller-based correction geometry — a quick way to see and
/// verify the obstacle-between-controllers placement before the full
/// <c>ControllerDriftCorrector</c> (gate stack, EMA, snap) exists.
///
/// <para>Two-layer hierarchy, mirroring the AprilTag <c>CorrectionRoot</c> /
/// <c>Obstacle</c> pattern so the finesse controller can layer a per-session
/// nudge offset on top of the controller-derived placement:</para>
///
/// <code>
/// ControllerPlacerAnchor                 (created on lock — OVRSpatialAnchor)
///   └── ControllerPlacerCorrectionRoot    (placer writes here every LateUpdate)
///         └── SpawnedObstacle              (finesse writes localPose here)
/// </code>
///
/// The placer's <see cref="PlaceObstacle"/> writes the controller-midpoint
/// world pose to the <c>CorrectionRoot</c>; the obstacle's local pose
/// (finesse offset) sits on top and is preserved across follow updates and
/// across the lock/unlock anchor reparenting.
///
/// Convention: when the experimenter places the controllers while facing
/// world +Z, baseline ≈ +X and the obstacle's local +Z resolves to world +Z.
/// Use <see cref="rotationOffsetEuler"/> (e.g. <c>(0, 180, 0)</c>) to flip if
/// the obstacle ends up facing the wrong way.
///
/// Modes:
///   - Following (default): the correction-root tracks the live controller
///     midpoint every LateUpdate. Pick up a controller → obstacle follows.
///   - Locked: a dedicated <see cref="OVRSpatialAnchor"/> is created at the
///     current correction-root pose; the correction-root reparents under it
///     for SLAM-robust anchoring. While the anchor exists, the placer emits
///     state_snapshot rows at 30Hz (correction_source=controller_placer) so
///     its pose can be compared against the AprilTag anchor_baseline in
///     post-analysis. Toggle with the lock button.
///
/// Controls (Inspector-rebindable; defaults verified free vs ObstacleFinesseController):
///   - Left index trigger  -> toggle follow / locked (creates / destroys the anchor)
///   - Right index trigger -> echo the full control scheme + diagnostic status
///     to the Pipeline HUD (useful for confirming the placer is alive in-headset)
///
/// Pose is read from <see cref="ControllerPoseProvider"/> (Phase 1). Not the
/// real correction: no validity/range/velocity/rigid-body/facing/step-over
/// gates, no EMA, no snap, no write to ConstellationDriftCorrector's
/// CorrectionRoot. The placer's anchor is a *separate, dedicated* anchor —
/// it doesn't share state with the AprilTag drift correction system, and the
/// two anchors are tracked independently in the session log so their drift
/// can be compared.
/// </summary>
[DisallowMultipleComponent]
public sealed class ControllerObstaclePlacer : MonoBehaviour
{
    [Header("Wiring")]
    [Tooltip("Source of controller poses. Auto-resolved if left empty.")]
    [SerializeField] private ControllerPoseProvider provider;

    [Tooltip("Prefab to instantiate as the runtime test obstacle. On Start(), one " +
             "instance is spawned (named '<prefab> (placer-spawned)') as a child of an " +
             "intermediate CorrectionRoot wrapper. LateUpdate moves the wrapper to the " +
             "controller midpoint while following; the obstacle's local pose under the " +
             "wrapper is the finesse offset (ObstacleFinesseController writes there when " +
             "its target is Placer). Use a visually distinct prefab so it's easy to tell " +
             "apart from the AprilTag-spawned obstacle.")]
    [SerializeField] private GameObject obstaclePrefab;

    [Tooltip("Optional. The control scheme + diagnostic status is echoed here on " +
             "the echo-controls button press. Auto-resolved if empty.")]
    [SerializeField] private PipelineStatusHUD hud;

    [Header("Placement geometry")]
    [Tooltip("Vertical offset added to the controller-midpoint, in meters. " +             "Use this to drop the obstacle from controller/rig height down onto the gait mat.")]
    [SerializeField] private float verticalOffsetMeters = 0f;

    [Tooltip("Euler rotation offset applied after the perpendicular yaw. Use (0,180,0) to flip " +
             "the obstacle if it ends up facing toward the user instead of along the walking " +
             "direction, or to compensate for a prefab whose long axis isn't its local X.")]
    [SerializeField] private Vector3 rotationOffsetEuler = Vector3.zero;

    [Header("Bindings (defaults verified free vs ObstacleFinesseController)")]
    [Tooltip("Toggles follow / locked. On lock, creates a dedicated OVRSpatialAnchor and " +
             "reparents the correction-root (which contains the obstacle) under it.")]
    [SerializeField] private OVRInput.Button lockToggleButton = OVRInput.Button.PrimaryIndexTrigger;

    [Tooltip("Echoes the current control scheme + diagnostic status to the Pipeline HUD.")]
    [SerializeField] private OVRInput.Button echoControlsButton = OVRInput.Button.SecondaryIndexTrigger;

    [Header("Feedback")]
    [SerializeField] private bool hapticOnLockToggle = true;

    [Header("Anchor logging")]
    [Tooltip("While the dedicated anchor exists, emit state_snapshot rows at this rate " +
             "(correction_source=controller_placer) so the placer-anchor pose can be " +
             "compared against the AprilTag anchor_baseline source in post.")]
    [SerializeField, Range(1f, 30f)] private float anchorSnapshotRateHz = 30f;

    /// <summary>True while the obstacle is frozen (not following the controllers).</summary>
    public bool IsLocked { get; private set; }

    /// <summary>The runtime-spawned obstacle Transform (the finesse target — child of the correction root).</summary>
    public Transform SpawnedObstacle => _spawnedObstacle;

    /// <summary>The intermediate CorrectionRoot wrapper between the anchor and the obstacle. The placer writes its world pose; the obstacle's localPose under it is the finesse offset.</summary>
    public Transform PlacerCorrectionRoot => _correctionRoot;

    /// <summary>The dedicated OVRSpatialAnchor created on lock, or null while unlocked.</summary>
    public OVRSpatialAnchor PlacerAnchor => _anchor;

    /// <summary>True while the placer anchor exists (i.e. while locked).</summary>
    public bool IsAnchorActive => _anchor != null;

    private Transform _correctionRoot;
    private Transform _spawnedObstacle;
    private GameObject _anchorGo;
    private OVRSpatialAnchor _anchor;
    private float _nextAnchorSnapshotTime;
    private ObstacleFinesseController _finesseController;

    private void Awake()
    {
        if (!provider) provider = FindAnyObjectByType<ControllerPoseProvider>();
        if (!hud) hud = FindAnyObjectByType<PipelineStatusHUD>();
        // Cache the finesse controller so the echo can show the current target.
        // Optional — the placer doesn't depend on it for placement.
        if (_finesseController == null)
            _finesseController = FindAnyObjectByType<ObstacleFinesseController>();
    }

    private void OnEnable()
    {
        if (!provider)
            Debug.LogError("[ControllerObstaclePlacer] No ControllerPoseProvider found. Placement disabled.");
    }

    private void Start()
    {
        if (obstaclePrefab == null)
        {
            Debug.LogWarning("[ControllerObstaclePlacer] No obstaclePrefab assigned. Drag a prefab into the Inspector.");
            EchoToHud("<color=#FF8888>ControllerObstaclePlacer: no prefab assigned</color>");
            return;
        }

        // Wrapper Transform. Placer writes its world pose every LateUpdate while
        // following. On lock the anchor parents this wrapper; the obstacle child
        // inherits anchoring + retains its finesse offset.
        var rootGo = new GameObject("ControllerPlacerCorrectionRoot");
        _correctionRoot = rootGo.transform;

        // Obstacle is a child of the wrapper. Its localPose is the finesse offset
        // (zero by default; ObstacleFinesseController writes here when targeting Placer).
        var go = Instantiate(obstaclePrefab, _correctionRoot);
        go.name = $"{obstaclePrefab.name} (placer-spawned)";
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        _spawnedObstacle = go.transform;

        EchoToHud($"<color=#88FF88>Spawned '{obstaclePrefab.name}' for placer</color>");
        Debug.Log($"[ControllerObstaclePlacer] Spawned '{obstaclePrefab.name}' under ControllerPlacerCorrectionRoot.");
    }

    private void OnDestroy()
    {
        // Detach + drop the anchor first (DestroyAnchor reparents the correction
        // root back to scene root if locked), then destroy the wrapper which
        // takes the obstacle child with it.
        DestroyAnchor();
        if (_correctionRoot != null && _correctionRoot.gameObject != null)
            Destroy(_correctionRoot.gameObject);
    }

    // LateUpdate so controller poses are already sampled this frame
    // (ControllerPoseProvider samples in Update).
    private void LateUpdate()
    {
        if (OVRInput.GetDown(lockToggleButton)) ToggleLock();
        if (OVRInput.GetDown(echoControlsButton)) EchoControls();

        if (!IsLocked) PlaceObstacle();

        // While the dedicated anchor exists, emit periodic state_snapshot rows so
        // the placer-anchor pose can be compared against the AprilTag anchor_baseline
        // source (logged by AnchorBaselineLogger) in post-analysis.
        if (_anchor != null && Time.unscaledTime >= _nextAnchorSnapshotTime)
        {
            _nextAnchorSnapshotTime = Time.unscaledTime + 1f / Mathf.Max(0.1f, anchorSnapshotRateHz);
            EmitAnchorSnapshot();
        }
    }

    private void PlaceObstacle()
    {
        if (provider == null || _correctionRoot == null) return;
        if (!provider.LeftPositionValid || !provider.RightPositionValid) return;

        Vector3 lPos = provider.LeftPose.position;
        Vector3 rPos = provider.RightPose.position;

        Vector3 midpoint = (lPos + rPos) * 0.5f;
        midpoint.y += verticalOffsetMeters;

        Vector3 baseline = rPos - lPos;

        // Obstacle's local +Z points perpendicular to the baseline, in the
        // floor plane. With the user facing world +Z when placing the
        // controllers, baseline ≈ +X and Cross(+X, +Y) = +Z — obstacle faces
        // the walking direction, not the rig line. Cross(*, up) always lands
        // in XZ regardless of the baseline's Y component, so a tilted rig
        // doesn't tilt the obstacle. If tilt-preservation is wanted later it
        // needs a different design (rolling local +X along the raw baseline).
        Vector3 forward = Vector3.Cross(baseline, Vector3.up);

        Quaternion rot = forward.sqrMagnitude < 1e-6f
            ? _correctionRoot.rotation    // controllers stacked vertically; hold prior rotation
            : Quaternion.LookRotation(forward, Vector3.up);
        rot *= Quaternion.Euler(rotationOffsetEuler);

        // Write to the wrapper, NOT the obstacle. The obstacle's localPose
        // under the wrapper is the finesse offset and is owned by
        // ObstacleFinesseController when its target is Placer.
        _correctionRoot.SetPositionAndRotation(midpoint, rot);
    }

    /// <summary>
    /// Toggle between following the controllers and being frozen + anchored.
    /// On lock: creates a dedicated <see cref="OVRSpatialAnchor"/> at the
    /// current correction-root pose and reparents the correction-root (with
    /// its obstacle child) under it (SLAM maintains the anchored pose). On
    /// unlock: detaches the correction-root and destroys the anchor;
    /// following resumes next frame.
    /// </summary>
    [ContextMenu("Toggle Lock")]
    public void ToggleLock()
    {
        IsLocked = !IsLocked;

        if (IsLocked) CreateAnchor();
        else DestroyAnchor();

        if (hapticOnLockToggle && provider != null)
        {
            provider.Pulse(OVRInput.Controller.LTouch, 1f, 0.5f, 0.06f);
            provider.Pulse(OVRInput.Controller.RTouch, 1f, 0.5f, 0.06f);
        }

        string state = IsLocked ? "LOCKED + ANCHORED" : "following controllers";
        EchoToHud($"<color=#FFFF88>Obstacle {state}</color>");
        Debug.Log($"[ControllerObstaclePlacer] Obstacle {state}.");

        if (SessionLogger.Instance != null)
        {
            var detail = $"locked={(IsLocked ? 1 : 0)};anchor={(_anchor != null ? 1 : 0)}";
            SessionLogger.Instance.Enqueue(LogEvent.SessionEvent("obstacle_placer_lock", detail));
        }
    }

    /// <summary>Echo the full control scheme + diagnostic status to the Pipeline HUD.</summary>
    [ContextMenu("Echo Controls To HUD")]
    public void EchoControls()
    {
        bool lValid = provider != null && provider.LeftPositionValid;
        bool rValid = provider != null && provider.RightPositionValid;
        string yesNoSpawn = _spawnedObstacle != null ? "yes" : "<color=#FF8888>NO</color>";
        string yesNoAnchor = _anchor != null ? "yes" : "no";
        string yesNoL = lValid ? "yes" : "<color=#FF8888>NO</color>";
        string yesNoR = rValid ? "yes" : "<color=#FF8888>NO</color>";

        if (_finesseController == null)
            _finesseController = FindAnyObjectByType<ObstacleFinesseController>();
        string finesseTarget = _finesseController != null
            ? _finesseController.ActiveTarget.ToString()
            : "(no finesse)";

        string msg =
            "<b>Drift Correction — Controls</b>\n" +
            "L index trigger : lock / unlock obstacle (creates/destroys anchor)\n" +
            "R index trigger : show this\n" +
            "L thumbstick click : cycle finesse target (AprilTag → Placer → Controller → …)\n" +
            "<b>Status</b>\n" +
            $"Locked: {(IsLocked ? "yes" : "no")} · Spawned: {yesNoSpawn} · Anchor: {yesNoAnchor}\n" +
            $"L valid: {yesNoL} · R valid: {yesNoR} · Finesse: {finesseTarget}\n" +
            "<b>Obstacle finesse</b>\n" +
            "L/R thumbsticks : nudge (X/Z/Y) + yaw\n" +
            "L grip (hold) : fine mode\n" +
            "A / B : reset position / rotation\n" +
            "both grips + A : reset all\n" +
            "R grip + A : batch calibrate\n" +
            "R grip + B : streaming sweep (begin/commit)\n" +
            "R grip + R-stick click : cancel sweep";
        EchoToHud(msg);
        Debug.Log("[ControllerObstaclePlacer] Echoed controls to HUD.");
    }

    private void EchoToHud(string message)
    {
        if (hud != null) hud.ShowTransient(message, 8f);
    }

    // ---- dedicated anchor lifecycle ----
    //
    // The placer's anchor is created on lock and destroyed on unlock. It's a
    // *separate* OVRSpatialAnchor from the one owned by ConstellationDriftCorrector
    // (which anchors the AprilTag constellation). Keeping them separate means
    // each correction system manages its own offsets and the session log can
    // track both in parallel for drift comparison.
    //
    // The reparented child is the CorrectionRoot (which contains the obstacle
    // as its own child), not the obstacle directly — this preserves the
    // two-layer hierarchy across lock/unlock, exactly as the AprilTag system
    // does with its CorrectionRoot.

    private void CreateAnchor()
    {
        if (_anchorGo != null)
        {
            Debug.LogWarning("[ControllerObstaclePlacer] Anchor already exists; skipping create.");
            return;
        }
        if (_correctionRoot == null)
        {
            Debug.LogWarning("[ControllerObstaclePlacer] No correction root to anchor.");
            return;
        }

        _anchorGo = new GameObject("ControllerPlacerAnchor");
        _anchorGo.transform.SetPositionAndRotation(_correctionRoot.position, _correctionRoot.rotation);
        _anchor = _anchorGo.AddComponent<OVRSpatialAnchor>();

        // Reparent the correction root (and its obstacle child) under the anchor,
        // preserving the current world pose. Subsequent SLAM corrections to the
        // anchor will move both with it; the obstacle's finesse offset relative
        // to the correction root is preserved automatically.
        _correctionRoot.SetParent(_anchorGo.transform, worldPositionStays: true);

        // Reset snapshot cadence so the first row goes out promptly on the next LateUpdate.
        _nextAnchorSnapshotTime = 0f;

        Debug.Log($"[ControllerObstaclePlacer] Anchor created at {_anchorGo.transform.position}.");
    }

    private void DestroyAnchor()
    {
        if (_anchorGo == null && _anchor == null) return;

        // Detach the correction root back to scene root, preserving its
        // current world pose. (The next LateUpdate-with-IsLocked-false will
        // immediately overwrite that pose with the live controller midpoint,
        // so this is mostly for cleanliness during the same-frame unlock +
        // follow transition.)
        if (_correctionRoot != null && _correctionRoot.gameObject != null)
            _correctionRoot.SetParent(null, worldPositionStays: true);

        if (_anchorGo != null) Destroy(_anchorGo);
        _anchorGo = null;
        _anchor = null;

        Debug.Log("[ControllerObstaclePlacer] Anchor destroyed.");
    }

    private void EmitAnchorSnapshot()
    {
        if (SessionLogger.Instance == null || _anchorGo == null) return;
        var e = LogEvent.StateSnapshot("controller_placer", mode: "applied");
        e.AnchorPos = _anchorGo.transform.position;
        e.AnchorRot = _anchorGo.transform.rotation;
        SessionLogger.Instance.Enqueue(e);
    }
}
