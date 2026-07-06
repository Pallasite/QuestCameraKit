using UnityEngine;

/// <summary>
/// Controller-driven port of the FinesseTouch UI panel from the Kines-Perturb
/// experiment. Nudges the active obstacle's local pose (cm/mm translation,
/// deg/0.1° rotation) relative to whatever <c>CorrectionRoot</c> parents it,
/// so the experimenter can fine-tune placement without touching a UI in the
/// headset. The active obstacle is one of three independent correction
/// systems (AprilTag / controller-placer / controller-corrector), runtime-
/// switchable via the toggle button.
///
/// Input handling is delegated to <see cref="QuestControllerInput"/> — this
/// class is the policy layer that maps semantic input events to obstacle
/// transforms and the calibrate trigger.
///
/// IMPORTANT: this writes <c>obstacle.transform.localPosition/localRotation</c>,
/// not the parent CorrectionRoot's. The active correction system overwrites
/// its CorrectionRoot every frame, but the obstacle's local pose under it (the
/// finesse offset) is preserved across correction updates — that's exactly the
/// offset surface FinesseTouch was tuning. Each of the three targets keeps its
/// own independent finesse offset on its own obstacle Transform, so switching
/// the active target doesn't reset the others.
///
/// Control scheme (Quest 3 controllers):
///   L thumbstick X  → nudge local +X / -X    (right / left)
///   L thumbstick Y  → nudge local +Z / -Z    (forward / back)
///   R thumbstick Y  → nudge local +Y / -Y    (up / down)
///   R thumbstick X  → rotate local Y axis    (yaw +/-)
///   Left HandTrigger held = fine mode (mm / 0.1°), released = coarse (cm / 1°)
///   A (right) alone  → reset position offset to zero
///   B (right) alone  → reset rotation offset to identity
///   Both HandTriggers held + A → reset everything (replaces the old X+Y chord)
///   Right HandTrigger + A → batch calibrate constellation (one-shot ScanCalibrationAsync)
///   Right HandTrigger + B → toggle streaming calibration sweep (Begin if idle, Commit if sweeping)
///   Right HandTrigger + R thumbstick click → cancel an in-progress streaming sweep
///   L thumbstick click → cycle finesse target (AprilTag → Placer → Controller → AprilTag …)
/// </summary>
public class ObstacleFinesseController : MonoBehaviour
{
    /// <summary>Which obstacle the finesse bindings currently nudge.</summary>
    public enum FinesseTarget
    {
        /// <summary>The AprilTag-anchored obstacle (<see cref="ConstellationDriftCorrector.Obstacle"/>).</summary>
        AprilTag,
        /// <summary>The controller-placer's spawned obstacle (<see cref="ControllerObstaclePlacer.SpawnedObstacle"/>).</summary>
        Placer,
        /// <summary>The controller-corrector's spawned obstacle (<see cref="ControllerDriftCorrector.SpawnedObstacle"/>).</summary>
        Controller,
    }

    [Header("Wiring")]
    [SerializeField] private QuestControllerInput input;

    [Tooltip("Pulls the AprilTag obstacle from ConstellationDriftCorrector.Obstacle when set. Leave the manual target null to use this auto-resolution path.")]
    [SerializeField] private ConstellationDriftCorrector corrector;

    [Tooltip("Reference to the controller-placer; auto-resolved if empty. Required when activeTarget = Placer.")]
    [SerializeField] private ControllerObstaclePlacer placer;

    [Tooltip("Reference to the controller-drift-corrector; auto-resolved if empty. Required when activeTarget = Controller.")]
    [SerializeField] private ControllerDriftCorrector driftCorrector;

    [Tooltip("Manual override. If set, this transform is nudged directly and corrector / placer / driftCorrector are ignored.")]
    [SerializeField] private Transform manualTarget;

    [Header("Target")]
    [Tooltip("Which obstacle the finesse bindings act on. Toggle at runtime with toggleTargetButton " +
             "(default L thumbstick click).")]
    [SerializeField] private FinesseTarget activeTarget = FinesseTarget.AprilTag;

    [Tooltip("Button that toggles which obstacle the finesse bindings target. Default L thumbstick " +
             "click (PrimaryThumbstick) is verified free vs the other finesse / calibration chords.")]
    [SerializeField] private OVRInput.Button toggleTargetButton = OVRInput.Button.PrimaryThumbstick;

    [Header("Step sizes")]
    [SerializeField] private float coarseTranslationMeters = 0.01f; // 1 cm
    [SerializeField] private float fineTranslationMeters = 0.001f;  // 1 mm
    [SerializeField] private float coarseRotationDegrees = 1f;
    [SerializeField] private float fineRotationDegrees = 0.1f;

    [Header("Feedback")]
    [SerializeField] private bool hapticOnNudge = true;
    [SerializeField, Range(0f, 1f)] private float hapticAmplitude = 0.4f;
    [SerializeField] private float hapticDurationSeconds = 0.04f;
    [SerializeField] private bool logEachNudge = false;

    private Transform Target
    {
        get
        {
            if (manualTarget) return manualTarget;
            switch (activeTarget)
            {
                case FinesseTarget.AprilTag:
                    return (corrector && corrector.Obstacle) ? corrector.Obstacle.transform : null;
                case FinesseTarget.Placer:
                    return (placer != null) ? placer.SpawnedObstacle : null;
                case FinesseTarget.Controller:
                    return (driftCorrector != null) ? driftCorrector.SpawnedObstacle : null;
            }
            return null;
        }
    }

    /// <summary>Read-only view of which obstacle the finesse bindings are currently driving.</summary>
    public FinesseTarget ActiveTarget => activeTarget;

    /// <summary>
    /// Point the finesse bindings at a runtime-created Transform (the FinesseOffset
    /// layer in the single-tag placement chain). Overrides the AprilTag/Placer/Controller
    /// target resolution. Pass null to revert to the enum-based resolution.
    /// </summary>
    public void SetManualTarget(Transform target) => manualTarget = target;

    public bool FineMode => input && input.IsHeld(OVRInput.Button.PrimaryHandTrigger);
    private float TranslationStep => FineMode ? fineTranslationMeters : coarseTranslationMeters;
    private float RotationStep => FineMode ? fineRotationDegrees : coarseRotationDegrees;

    private IHudTransientSink _hud;

    private void Awake()
    {
        if (!input)
        {
            input = GetComponent<QuestControllerInput>();
            if (!input) Debug.LogWarning("[FinesseController] No QuestControllerInput assigned or found on this GameObject. Assign one in the Inspector.");
        }
        if (!corrector && !manualTarget)
        {
            Debug.LogWarning("[FinesseController] No ConstellationDriftCorrector or manual target assigned. Assign one in the Inspector.");
        }
        // Auto-resolve the placer, drift corrector, and HUD for the runtime
        // target-switch flow. Each non-AprilTag corrector is only required when
        // its corresponding target is active; the HUD is optional (haptic +
        // Debug.Log still give feedback if it's missing).
        if (!placer) placer = FindAnyObjectByType<ControllerObstaclePlacer>();
        if (!driftCorrector) driftCorrector = FindAnyObjectByType<ControllerDriftCorrector>();
        if (_hud == null) _hud = HudSink.Find();
    }

    private void OnEnable()
    {
        if (!input)
        {
            Debug.LogError("[FinesseController] No QuestControllerInput found. Disabling.");
            enabled = false;
            return;
        }
        input.OnStickFire += HandleStickFire;
        if (corrector)
        {
            corrector.OnConstellationCalibrated += HandleCalibrationSuccess;
            corrector.OnCalibrationFailed += HandleCalibrationFailure;
        }
    }

    private void OnDisable()
    {
        if (input) input.OnStickFire -= HandleStickFire;
        if (corrector)
        {
            corrector.OnConstellationCalibrated -= HandleCalibrationSuccess;
            corrector.OnCalibrationFailed -= HandleCalibrationFailure;
        }
    }

    private void HandleCalibrationSuccess()
    {
        // Double-tap both controllers on success.
        StartCoroutine(DoublePulse());
    }

    private void HandleCalibrationFailure(string reason)
    {
        // Long buzz on both controllers on failure.
        if (!hapticOnNudge) return;
        OVRInput.SetControllerVibration(1f, hapticAmplitude * 0.8f, OVRInput.Controller.LTouch);
        OVRInput.SetControllerVibration(1f, hapticAmplitude * 0.8f, OVRInput.Controller.RTouch);
        CancelInvoke(nameof(StopHaptics));
        Invoke(nameof(StopHaptics), 0.3f);
    }

    private System.Collections.IEnumerator DoublePulse()
    {
        if (!hapticOnNudge) yield break;
        Pulse(OVRInput.Controller.LTouch);
        Pulse(OVRInput.Controller.RTouch);
        yield return new WaitForSeconds(0.12f);
        Pulse(OVRInput.Controller.LTouch);
        Pulse(OVRInput.Controller.RTouch);
    }

    private void Update()
    {
        if (!input) return;

        // Target switch (no modifier — defaults to L thumbstick click). Runs
        // before all the grip-modified chords so it can't be eaten by them.
        if (input.WasPressedThisFrame(toggleTargetButton))
        {
            ToggleActiveTarget();
            return;
        }

        var leftGrip = input.IsHeld(OVRInput.Button.PrimaryHandTrigger);
        var rightGrip = input.IsHeld(OVRInput.Button.SecondaryHandTrigger);
        var aPressed = input.WasPressedThisFrame(OVRInput.Button.One);
        var bPressed = input.WasPressedThisFrame(OVRInput.Button.Two);

        // Resolution order matters — earlier branches must win over later ones
        // that share a button.

        // 1. Both grips + A → reset all (wins over right-grip+A → calibrate)
        if (leftGrip && rightGrip && aPressed)
        {
            var t = Target;
            if (t) ResetAll(t);
            return;
        }

        // 2. Calibration chords — must work before an obstacle exists. Only
        //    claimed when a ConstellationDriftCorrector is actually present
        //    (old constellation scenes). In the single/double-tag scenes there
        //    is no corrector, so R-grip+A/B stay free for other control
        //    surfaces instead of being eaten by a warning no-op.
        if (corrector != null)
        {
            if (rightGrip && aPressed)
            {
                TriggerCalibrate();
                return;
            }
            if (rightGrip && bPressed)
            {
                TriggerStreamingToggle();
                return;
            }
            if (rightGrip && input.WasPressedThisFrame(OVRInput.Button.SecondaryThumbstick))
            {
                TriggerStreamingCancel();
                return;
            }
        }

        // 3. Per-axis resets (no grip modifier).
        var target = Target;
        if (!target) return;

        if (!rightGrip && aPressed) ResetPosition(target);
        if (!rightGrip && bPressed) ResetRotation(target);
    }

    private void HandleStickFire(QuestControllerInput.StickAxis axis, int sign)
    {
        var target = Target;
        if (!target) return;

        switch (axis)
        {
            case QuestControllerInput.StickAxis.LeftX:
                NudgeLocal(target, new Vector3(sign * TranslationStep, 0, 0), "X", OVRInput.Controller.LTouch);
                break;
            case QuestControllerInput.StickAxis.LeftY:
                NudgeLocal(target, new Vector3(0, 0, sign * TranslationStep), "Z", OVRInput.Controller.LTouch);
                break;
            case QuestControllerInput.StickAxis.RightY:
                NudgeLocal(target, new Vector3(0, sign * TranslationStep, 0), "Y", OVRInput.Controller.RTouch);
                break;
            case QuestControllerInput.StickAxis.RightX:
                RotateLocalY(target, sign * RotationStep);
                break;
        }
    }

    /// <summary>
    /// Fire-and-forget calibration via the assigned ConstellationDriftCorrector.
    /// Logs and pulses both controllers so the experimenter gets immediate
    /// confirmation; the corrector itself takes ~calibrationFrameCount frames
    /// to capture and may quietly fail (too few tags, scanner busy) — watch
    /// the corrector's log lines for the outcome.
    /// </summary>
    public void TriggerCalibrate()
    {
        if (!corrector)
        {
            Debug.LogWarning("[FinesseController] Calibrate chord pressed but no ConstellationDriftCorrector assigned.");
            return;
        }
        Debug.Log("[FinesseController] Calibrate chord -> ConstellationDriftCorrector.Calibrate()");
        Pulse(OVRInput.Controller.LTouch);
        Pulse(OVRInput.Controller.RTouch);
        _ = corrector.Calibrate();
    }

    /// <summary>
    /// Right-grip + B chord: if no streaming session is active, begin one
    /// (the experimenter then sweeps the headset across the tag layout). If a
    /// session is already active, commit it. Success/failure is surfaced
    /// through the corrector's existing OnConstellationCalibrated /
    /// OnCalibrationFailed events, which this component already subscribes to
    /// for haptic feedback.
    /// </summary>
    public void TriggerStreamingToggle()
    {
        if (!corrector)
        {
            Debug.LogWarning("[FinesseController] Streaming toggle chord pressed but no ConstellationDriftCorrector assigned.");
            return;
        }
        if (corrector.IsStreamingCalibration)
        {
            Debug.Log("[FinesseController] Streaming chord -> CommitStreamingCalibration()");
            Pulse(OVRInput.Controller.LTouch);
            Pulse(OVRInput.Controller.RTouch);
            corrector.CommitStreamingCalibration();
        }
        else
        {
            Debug.Log("[FinesseController] Streaming chord -> BeginStreamingCalibration()");
            Pulse(OVRInput.Controller.RTouch);
            corrector.BeginStreamingCalibration();
        }
    }

    /// <summary>
    /// Right-grip + R thumbstick click chord: cancel an in-progress streaming
    /// sweep. No-op if no session is active. Longer haptic pulse than
    /// begin/commit so the gesture feels distinct.
    /// </summary>
    public void TriggerStreamingCancel()
    {
        if (!corrector) return;
        if (!corrector.IsStreamingCalibration) return; // no-op when idle
        Debug.Log("[FinesseController] Cancel chord -> CancelStreamingCalibration()");
        // Longer-than-default pulse to mark cancel as distinct from begin/commit.
        if (hapticOnNudge)
        {
            OVRInput.SetControllerVibration(1f, hapticAmplitude * 0.8f, OVRInput.Controller.RTouch);
            CancelInvoke(nameof(StopHaptics));
            Invoke(nameof(StopHaptics), 0.2f);
        }
        corrector.CancelStreamingCalibration();
    }

    /// <summary>
    /// Cycle which obstacle the finesse bindings drive, in order:
    /// AprilTag (the <see cref="ConstellationDriftCorrector"/>'s obstacle) →
    /// Placer (the <see cref="ControllerObstaclePlacer"/>'s spawned obstacle) →
    /// Controller (the <see cref="ControllerDriftCorrector"/>'s spawned obstacle) →
    /// back to AprilTag. All three retain their independent local offsets
    /// across switches — the offsets live on each obstacle's
    /// <c>transform.localPose</c>, not on this controller.
    /// </summary>
    [ContextMenu("Toggle Finesse Target")]
    public void ToggleActiveTarget()
    {
        activeTarget = activeTarget switch
        {
            FinesseTarget.AprilTag => FinesseTarget.Placer,
            FinesseTarget.Placer => FinesseTarget.Controller,
            FinesseTarget.Controller => FinesseTarget.AprilTag,
            _ => FinesseTarget.AprilTag,
        };

        // Tactile + visual confirmation. Double-pulse both controllers so the
        // gesture is distinct from a normal nudge.
        if (hapticOnNudge)
        {
            Pulse(OVRInput.Controller.LTouch);
            Pulse(OVRInput.Controller.RTouch);
        }

        if (_hud == null) _hud = HudSink.Find();
        if (_hud != null)
        {
            _hud.ShowTransient($"<color=#FFFF88>Finesse target: {activeTarget.ToString().ToUpperInvariant()}</color>", 4f);
        }

        Debug.Log($"[FinesseController] Finesse target switched to {activeTarget}.");

        if (SessionLogger.Instance != null)
        {
            SessionLogger.Instance.Enqueue(LogEvent.SessionEvent(
                "finesse_target", $"target={activeTarget.ToString().ToLowerInvariant()}"));
        }
    }

    private void NudgeLocal(Transform t, Vector3 deltaLocal, string label, OVRInput.Controller pulseOn)
    {
        t.localPosition += deltaLocal;
        Pulse(pulseOn);
        if (logEachNudge)
            Debug.Log($"[FinesseController] +{label} {(FineMode ? "mm" : "cm")} -> localPos={t.localPosition}");
    }

    private void RotateLocalY(Transform t, float degrees)
    {
        t.localRotation *= Quaternion.Euler(0f, degrees, 0f);
        Pulse(OVRInput.Controller.RTouch);
        if (logEachNudge)
            Debug.Log($"[FinesseController] yaw {(FineMode ? "0.1°" : "1°")} -> localEuler={t.localRotation.eulerAngles}");
    }

    public void ResetPosition(Transform t = null)
    {
        t ??= Target; if (!t) return;
        t.localPosition = Vector3.zero;
        Pulse(OVRInput.Controller.RTouch);
        Debug.Log("[FinesseController] localPosition reset to zero.");
    }

    public void ResetRotation(Transform t = null)
    {
        t ??= Target; if (!t) return;
        t.localRotation = Quaternion.identity;
        Pulse(OVRInput.Controller.RTouch);
        Debug.Log("[FinesseController] localRotation reset to identity.");
    }

    public void ResetAll(Transform t = null)
    {
        t ??= Target; if (!t) return;
        t.localPosition = Vector3.zero;
        t.localRotation = Quaternion.identity;
        Pulse(OVRInput.Controller.LTouch);
        Pulse(OVRInput.Controller.RTouch);
        Debug.Log("[FinesseController] full reset.");
    }

    private void Pulse(OVRInput.Controller c)
    {
        if (!hapticOnNudge) return;
        OVRInput.SetControllerVibration(1f, hapticAmplitude, c);
        CancelInvoke(nameof(StopHaptics));
        Invoke(nameof(StopHaptics), hapticDurationSeconds);
    }

    private void StopHaptics()
    {
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.LTouch);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
    }
}
