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
/// Convention: when the experimenter places the controllers while facing
/// world +Z, baseline ≈ +X and the obstacle's local +Z resolves to world +Z.
/// Use <see cref="rotationOffsetEuler"/> (e.g. <c>(0, 180, 0)</c>) to flip if
/// the obstacle ends up facing the wrong way.
///
/// The placer instantiates <see cref="obstaclePrefab"/> on Start() and updates
/// that instance's world pose every LateUpdate while following. The instance
/// is named "&lt;prefab&gt; (placer-spawned)" so it's easy to find in the
/// hierarchy and tell apart from the AprilTag-spawned obstacle.
///
/// Modes:
///   - Following (default): the spawned obstacle tracks the live controller
///     midpoint every LateUpdate. Pick up a controller → obstacle follows.
///   - Locked: a dedicated <see cref="OVRSpatialAnchor"/> is created at the
///     current obstacle pose; the obstacle reparents under it for SLAM-robust
///     anchoring. While the anchor exists, the placer emits state_snapshot
///     rows at 5Hz (correction_source=controller_placer) so its pose can be
///     compared against the AprilTag anchor_baseline in post-analysis. Toggle
///     with the lock button.
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
             "instance is spawned (named '<prefab> (placer-spawned)') and LateUpdate " +
             "moves that instance to the controller midpoint while following. Use a " +
             "visually distinct prefab so it's easy to tell apart from the " +
             "AprilTag-spawned obstacle.")]
    [SerializeField] private GameObject obstaclePrefab;

    [Tooltip("Optional. The control scheme + diagnostic status is echoed here on " +
             "the echo-controls button press. Auto-resolved if empty.")]
    [SerializeField] private PipelineStatusHUD hud;

    [Header("Placement geometry")]
    [Tooltip("Vertical offset added to the controller-midpoint, in meters. " +
             "Use this to drop the obstacle from controller/rig height down onto the gait mat.")]
    [SerializeField] private float verticalOffsetMeters = 0f;

    [Tooltip("Euler rotation offset applied after the perpendicular yaw. Use (0,180,0) to flip " +
             "the obstacle if it ends up facing toward the user instead of along the walking " +
             "direction, or to compensate for a prefab whose long axis isn't its local X.")]
    [SerializeField] private Vector3 rotationOffsetEuler = Vector3.zero;

    [Header("Bindings (defaults verified free vs ObstacleFinesseController)")]
    [Tooltip("Toggles follow / locked. On lock, creates a dedicated OVRSpatialAnchor and " +
             "reparents the obstacle under it for SLAM-robust anchoring.")]
    [SerializeField] private OVRInput.Button lockToggleButton = OVRInput.Button.PrimaryIndexTrigger;

    [Tooltip("Echoes the current control scheme + diagnostic status to the Pipeline HUD.")]
    [SerializeField] private OVRInput.Button echoControlsButton = OVRInput.Button.SecondaryIndexTrigger;

    [Header("Feedback")]
    [SerializeField] private bool hapticOnLockToggle = true;

    [Header("Anchor logging")]
    [Tooltip("While the dedicated anchor exists, emit state_snapshot rows at this rate " +
             "(correction_source=controller_placer) so the placer-anchor pose can be " +
             "compared against the AprilTag anchor_baseline source in post.")]
    [SerializeField, Range(1f, 30f)] private float anchorSnapshotRateHz = 5f;

    /// <summary>True while the obstacle is frozen (not following the controllers).</summary>
    public bool IsLocked { get; private set; }

    /// <summary>The runtime-spawned obstacle Transform, or null if no prefab was assigned.</summary>
    public Transform SpawnedObstacle => _spawnedObstacle;

    /// <summary>The dedicated OVRSpatialAnchor created on lock, or null while unlocked.</summary>
    public OVRSpatialAnchor PlacerAnchor => _anchor;

    /// <summary>True while the placer anchor exists (i.e. while locked).</summary>
    public bool IsAnchorActive => _anchor != null;

    private Transform _spawnedObstacle;
    private GameObject _anchorGo;
    private OVRSpatialAnchor _anchor;
    private float _nextAnchorSnapshotTime;

    private void Awake()
    {
        if (!provider) provider = FindAnyObjectByType<ControllerPoseProvider>();
        if (!hud) hud = FindAnyObjectByType<PipelineStatusHUD>();
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
        var go = Instantiate(obstaclePrefab);
        go.name = $"{obstaclePrefab.name} (placer-spawned)";
        _spawnedObstacle = go.transform;
        EchoToHud($"<color=#88FF88>Spawned '{obstaclePrefab.name}' for placer</color>");
        Debug.Log($"[ControllerObstaclePlacer] Spawned '{obstaclePrefab.name}'.");
    }

    private void OnDestroy()
    {
        // Clean up anchor first (detaches obstacle back to scene root), then destroy the obstacle.
        DestroyAnchor();
        if (_spawnedObstacle != null && _spawnedObstacle.gameObject != null)
            Destroy(_spawnedObstacle.gameObject);
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
        if (provider == null || _spawnedObstacle == null) return;
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
            ? _spawnedObstacle.rotation   // controllers stacked vertically; hold prior rotation
            : Quaternion.LookRotation(forward, Vector3.up);
        rot *= Quaternion.Euler(rotationOffsetEuler);

        _spawnedObstacle.SetPositionAndRotation(midpoint, rot);
    }

    /// <summary>
    /// Toggle between following the controllers and being frozen + anchored.
    /// On lock: creates a dedicated <see cref="OVRSpatialAnchor"/> at the
    /// current obstacle pose and reparents the obstacle under it (SLAM
    /// maintains the anchored pose against drift). On unlock: detaches the
    /// obstacle and destroys the anchor; following resumes next frame.
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

        string msg =
            "<b>Drift Correction — Controls</b>\n" +
            "L index trigger : lock / unlock obstacle (creates/destroys anchor)\n" +
            "R index trigger : show this\n" +
            "<b>Status</b>\n" +
            $"Locked: {(IsLocked ? "yes" : "no")} · Spawned: {yesNoSpawn} · Anchor: {yesNoAnchor}\n" +
            $"L valid: {yesNoL} · R valid: {yesNoR}\n" +
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

    private void CreateAnchor()
    {
        if (_anchorGo != null)
        {
            Debug.LogWarning("[ControllerObstaclePlacer] Anchor already exists; skipping create.");
            return;
        }
        if (_spawnedObstacle == null)
        {
            Debug.LogWarning("[ControllerObstaclePlacer] No spawned obstacle to anchor.");
            return;
        }

        _anchorGo = new GameObject("ControllerPlacerAnchor");
        _anchorGo.transform.SetPositionAndRotation(_spawnedObstacle.position, _spawnedObstacle.rotation);
        _anchor = _anchorGo.AddComponent<OVRSpatialAnchor>();

        // Reparent the obstacle under the anchor, preserving its current world
        // pose. Subsequent SLAM corrections to the anchor move the obstacle with it.
        _spawnedObstacle.SetParent(_anchorGo.transform, worldPositionStays: true);

        // Reset snapshot cadence so the first row goes out promptly on the next LateUpdate.
        _nextAnchorSnapshotTime = 0f;

        Debug.Log($"[ControllerObstaclePlacer] Anchor created at {_anchorGo.transform.position}.");
    }

    private void DestroyAnchor()
    {
        if (_anchorGo == null && _anchor == null) return;

        // Detach the obstacle back to scene root, preserving its current world pose.
        // (The next LateUpdate-with-IsLocked-false will immediately overwrite that
        // pose with the live controller midpoint, so this is mostly for cleanliness
        // during the same-frame unlock + follow transition.)
        if (_spawnedObstacle != null && _spawnedObstacle.gameObject != null)
            _spawnedObstacle.SetParent(null, worldPositionStays: true);

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
