using UnityEngine;

/// <summary>
/// Positions a user-supplied obstacle Transform at the midpoint of the two Touch
/// controllers, with yaw derived from the inter-controller baseline. This is a
/// gate-free testing slice of the Phase 2 controller-based correction geometry —
/// a quick way to see and verify the obstacle-between-controllers placement
/// before the full <c>ControllerDriftCorrector</c> (gate stack, EMA, snap) is
/// built.
///
/// Modes:
///   - Following (default): the obstacle tracks the live controller midpoint
///     every LateUpdate. Pick up a controller and the obstacle follows.
///   - Locked: the obstacle is frozen in place. Toggle with the lock button.
///
/// Controls (Inspector-rebindable; defaults verified free vs ObstacleFinesseController):
///   - Left index trigger  -> toggle follow / locked
///   - Right index trigger -> echo the full control scheme to the Pipeline HUD
///
/// The obstacle is NOT spawned — drag your own Transform into the <see cref="obstacle"/>
/// field (a Root Obstacle instance, a placeholder cube, whatever you're testing
/// with). Pose is read from <see cref="ControllerPoseProvider"/> (Phase 1).
///
/// Not the real correction: no validity/range/velocity/rigid-body/facing/step-over
/// gates, no EMA, no snap, no write to the spatial anchor's CorrectionRoot. The
/// placer just moves a Transform. The full pipeline remains Phase 2.
/// </summary>
[DisallowMultipleComponent]
public sealed class ControllerObstaclePlacer : MonoBehaviour
{
    [Header("Wiring")]
    [Tooltip("Source of controller poses. Auto-resolved if left empty.")]
    [SerializeField] private ControllerPoseProvider provider;

    [Tooltip("The obstacle to position between the controllers. Drag any Transform here — " +
             "a Root Obstacle instance, a placeholder cube, whatever you're testing with.")]
    [SerializeField] private Transform obstacle;

    [Tooltip("Optional. The control scheme is echoed here on the echo-controls button press. Auto-resolved if empty.")]
    [SerializeField] private PipelineStatusHUD hud;

    [Header("Placement geometry")]
    [Tooltip("Vertical offset added to the controller-midpoint, in meters. " +
             "Use this to drop the obstacle from controller/rig height down onto the gait mat.")]
    [SerializeField] private float verticalOffsetMeters = 0f;

    [Tooltip("When on, only baseline yaw is applied and the obstacle stays level with the floor. " +
             "When off, the obstacle tilts with the raw baseline vector.")]
    [SerializeField] private bool levelToFloor = true;

    [Tooltip("Euler rotation offset applied after the baseline yaw. Use this to align your " +
             "obstacle's long axis with the baseline (e.g. 0,90,0 if its long axis is local X).")]
    [SerializeField] private Vector3 rotationOffsetEuler = Vector3.zero;

    [Header("Bindings (defaults verified free vs ObstacleFinesseController)")]
    [Tooltip("Toggles follow / locked.")]
    [SerializeField] private OVRInput.Button lockToggleButton = OVRInput.Button.PrimaryIndexTrigger;

    [Tooltip("Echoes the current control scheme to the Pipeline HUD.")]
    [SerializeField] private OVRInput.Button echoControlsButton = OVRInput.Button.SecondaryIndexTrigger;

    [Header("Feedback")]
    [SerializeField] private bool hapticOnLockToggle = true;

    /// <summary>True while the obstacle is frozen (not following the controllers).</summary>
    public bool IsLocked { get; private set; }

    private void Awake()
    {
        if (!provider) provider = FindAnyObjectByType<ControllerPoseProvider>();
        if (!hud) hud = FindAnyObjectByType<PipelineStatusHUD>();
    }

    private void OnEnable()
    {
        if (!provider)
            Debug.LogError("[ControllerObstaclePlacer] No ControllerPoseProvider found. Placement disabled.");
        if (!obstacle)
            Debug.LogWarning("[ControllerObstaclePlacer] No obstacle assigned. Drag a Transform into the 'obstacle' field.");
    }

    // LateUpdate so controller poses are already sampled this frame
    // (ControllerPoseProvider samples in Update).
    private void LateUpdate()
    {
        if (OVRInput.GetDown(lockToggleButton)) ToggleLock();
        if (OVRInput.GetDown(echoControlsButton)) EchoControls();

        if (!IsLocked) PlaceObstacle();
    }

    private void PlaceObstacle()
    {
        if (provider == null || obstacle == null) return;
        if (!provider.LeftPositionValid || !provider.RightPositionValid) return;

        Vector3 lPos = provider.LeftPose.position;
        Vector3 rPos = provider.RightPose.position;

        Vector3 midpoint = (lPos + rPos) * 0.5f;
        midpoint.y += verticalOffsetMeters;

        Vector3 baseline = rPos - lPos;
        Quaternion rot;
        if (levelToFloor)
        {
            Vector3 flat = new Vector3(baseline.x, 0f, baseline.z);
            rot = flat.sqrMagnitude < 1e-6f
                ? obstacle.rotation
                : Quaternion.LookRotation(flat, Vector3.up);
        }
        else
        {
            rot = baseline.sqrMagnitude < 1e-6f
                ? obstacle.rotation
                : Quaternion.LookRotation(baseline, Vector3.up);
        }
        rot *= Quaternion.Euler(rotationOffsetEuler);

        obstacle.SetPositionAndRotation(midpoint, rot);
    }

    /// <summary>Toggle between following the controllers and being frozen in place.</summary>
    [ContextMenu("Toggle Lock")]
    public void ToggleLock()
    {
        IsLocked = !IsLocked;

        if (hapticOnLockToggle && provider != null)
        {
            provider.Pulse(OVRInput.Controller.LTouch, 1f, 0.5f, 0.06f);
            provider.Pulse(OVRInput.Controller.RTouch, 1f, 0.5f, 0.06f);
        }

        string state = IsLocked ? "LOCKED" : "following controllers";
        EchoToHud($"<color=#FFFF88>Obstacle {state}</color>");
        Debug.Log($"[ControllerObstaclePlacer] Obstacle {state}.");

        if (SessionLogger.Instance != null)
        {
            SessionLogger.Instance.Enqueue(LogEvent.SessionEvent(
                "obstacle_placer_lock", $"locked={(IsLocked ? 1 : 0)}"));
        }
    }

    /// <summary>Echo the full control scheme (placer + finesse/calibration) to the Pipeline HUD.</summary>
    [ContextMenu("Echo Controls To HUD")]
    public void EchoControls()
    {
        const string msg =
            "<b>Drift Correction — Controls</b>\n" +
            "L index trigger : lock / unlock obstacle\n" +
            "R index trigger : show this\n" +
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
}
