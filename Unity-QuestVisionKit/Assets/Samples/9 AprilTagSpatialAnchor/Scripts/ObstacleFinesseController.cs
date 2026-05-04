using UnityEngine;

/// <summary>
/// Controller-driven port of the FinesseTouch UI panel from the Kines-Perturb
/// experiment. Nudges the obstacle's local pose (cm/mm translation, deg/0.1°
/// rotation) relative to its parent <see cref="ConstellationDriftCorrector.CorrectionRoot"/>,
/// so the experimenter can fine-tune placement against the AprilTag
/// constellation without having to touch a UI in the headset.
///
/// Input handling is delegated to <see cref="QuestControllerInput"/> — this
/// class is the policy layer that maps semantic input events to obstacle
/// transforms and the calibrate trigger.
///
/// IMPORTANT: this writes <c>obstacle.transform.localPosition/localRotation</c>,
/// not CorrectionRoot's. The drift corrector overwrites CorrectionRoot every
/// Update() while lerping, but it preserves the obstacle's local pose across
/// recalibration (see <see cref="ConstellationDriftCorrector.Calibrate"/>),
/// which is exactly the offset surface FinesseTouch was tuning.
///
/// Control scheme (Quest 3 controllers):
///   L thumbstick X  → nudge local +X / -X    (right / left)
///   L thumbstick Y  → nudge local +Z / -Z    (forward / back)
///   R thumbstick Y  → nudge local +Y / -Y    (up / down)
///   R thumbstick X  → rotate local Y axis    (yaw +/-)
///   Left HandTrigger held = fine mode (mm / 0.1°), released = coarse (cm / 1°)
///   A (right)  → reset position offset to zero
///   B (right)  → reset rotation offset to identity
///   X + Y both held (left) → reset everything
///   Right HandTrigger held + A → (re)calibrate constellation
/// </summary>
public class ObstacleFinesseController : MonoBehaviour
{
    [Header("Wiring")]
    [SerializeField] private QuestControllerInput input;

    [Tooltip("Pulls the obstacle from ConstellationDriftCorrector.Obstacle when set. Leave the manual target null to use this auto-resolution path.")]
    [SerializeField] private ConstellationDriftCorrector corrector;

    [Tooltip("Manual override. If set, this transform is nudged directly and the corrector is ignored.")]
    [SerializeField] private Transform manualTarget;

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
            if (corrector && corrector.Obstacle) return corrector.Obstacle.transform;
            return null;
        }
    }

    public bool FineMode => input && input.IsHeld(OVRInput.Button.PrimaryHandTrigger);
    private float TranslationStep => FineMode ? fineTranslationMeters : coarseTranslationMeters;
    private float RotationStep => FineMode ? fineRotationDegrees : coarseRotationDegrees;

    private void Awake()
    {
        if (!input)
        {
            input = GetComponent<QuestControllerInput>();
            if (!input) input = FindObjectOfType<QuestControllerInput>();
        }
        if (!corrector && !manualTarget)
        {
            corrector = FindObjectOfType<ConstellationDriftCorrector>();
        }
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
    }

    private void OnDisable()
    {
        if (input) input.OnStickFire -= HandleStickFire;
    }

    private void Update()
    {
        if (!input) return;

        // Calibrate chord first — must work even before an obstacle exists.
        var rightGrip = input.IsHeld(OVRInput.Button.SecondaryHandTrigger);
        if (rightGrip && input.WasPressedThisFrame(OVRInput.Button.One))
        {
            TriggerCalibrate();
            return;
        }

        var target = Target;
        if (!target) return;

        // Resets — A is gated on right-grip-released so it doesn't double as calibrate.
        if (!rightGrip && input.WasPressedThisFrame(OVRInput.Button.One)) ResetPosition(target);
        if (input.WasPressedThisFrame(OVRInput.Button.Two)) ResetRotation(target);
        if (input.IsHeld(OVRInput.Button.Three) && input.IsHeld(OVRInput.Button.Four)
            && (input.WasPressedThisFrame(OVRInput.Button.Three) || input.WasPressedThisFrame(OVRInput.Button.Four)))
        {
            ResetAll(target);
        }
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
