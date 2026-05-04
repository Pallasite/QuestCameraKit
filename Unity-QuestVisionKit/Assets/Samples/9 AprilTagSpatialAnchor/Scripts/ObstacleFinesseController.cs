using UnityEngine;

/// <summary>
/// Controller-driven port of the FinesseTouch UI panel from the Kines-Perturb
/// experiment. Nudges the obstacle's local pose (cm/mm translation, deg/0.1°
/// rotation) relative to its parent <see cref="ConstellationDriftCorrector.CorrectionRoot"/>,
/// so the experimenter can fine-tune placement against the AprilTag
/// constellation without having to touch a UI in the headset.
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
///
/// Stick input is discrete: each push past <c>stickFireThreshold</c> fires one
/// nudge, and the stick must return below <c>stickRearmThreshold</c> before
/// another nudge fires. Hold the stick to repeat at <c>repeatRateHz</c>.
/// </summary>
public class ObstacleFinesseController : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("Pulls the obstacle from ConstellationDriftCorrector.Obstacle when set. Leave the manual target null to use this auto-resolution path.")]
    [SerializeField] private ConstellationDriftCorrector corrector;

    [Tooltip("Manual override. If set, this transform is nudged directly and the corrector is ignored.")]
    [SerializeField] private Transform manualTarget;

    [Header("Step sizes")]
    [SerializeField] private float coarseTranslationMeters = 0.01f; // 1 cm
    [SerializeField] private float fineTranslationMeters = 0.001f;  // 1 mm
    [SerializeField] private float coarseRotationDegrees = 1f;
    [SerializeField] private float fineRotationDegrees = 0.1f;

    [Header("Stick discretization")]
    [SerializeField, Range(0.3f, 0.95f)] private float stickFireThreshold = 0.7f;
    [SerializeField, Range(0.05f, 0.5f)] private float stickRearmThreshold = 0.3f;

    [Tooltip("When stick is held past the fire threshold, repeat nudges at this rate (Hz). Set 0 to disable repeat (one nudge per flick).")]
    [SerializeField] private float repeatRateHz = 5f;

    [Tooltip("Delay before auto-repeat kicks in after the initial fire (seconds).")]
    [SerializeField] private float repeatInitialDelaySeconds = 0.35f;

    [Header("Feedback")]
    [SerializeField] private bool hapticOnNudge = true;
    [SerializeField, Range(0f, 1f)] private float hapticAmplitude = 0.4f;
    [SerializeField] private float hapticDurationSeconds = 0.04f;
    [SerializeField] private bool logEachNudge = false;

    // Per-axis stick state. Index: 0=Lx, 1=Ly, 2=Rx, 3=Ry. Sign tracks last fired direction.
    private readonly int[] _firedSign = new int[4];
    private readonly float[] _nextRepeatTime = new float[4];

    private Transform Target
    {
        get
        {
            if (manualTarget) return manualTarget;
            if (corrector && corrector.Obstacle) return corrector.Obstacle.transform;
            return null;
        }
    }

    public bool FineMode => OVRInput.Get(OVRInput.Button.PrimaryHandTrigger);
    private float TranslationStep => FineMode ? fineTranslationMeters : coarseTranslationMeters;
    private float RotationStep => FineMode ? fineRotationDegrees : coarseRotationDegrees;

    private void Awake()
    {
        if (!corrector && !manualTarget)
        {
            corrector = FindObjectOfType<ConstellationDriftCorrector>();
        }
    }

    private void Update()
    {
        var target = Target;
        if (!target) return;

        var lStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);
        var rStick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);

        // Translation: left stick X→localX, left stick Y→localZ, right stick Y→localY.
        if (TryConsumeStick(0, lStick.x, out var sx)) NudgeLocal(target, new Vector3(sx * TranslationStep, 0, 0), "X", OVRInput.Controller.LTouch);
        if (TryConsumeStick(1, lStick.y, out var sz)) NudgeLocal(target, new Vector3(0, 0, sz * TranslationStep), "Z", OVRInput.Controller.LTouch);
        if (TryConsumeStick(3, rStick.y, out var sy)) NudgeLocal(target, new Vector3(0, sy * TranslationStep, 0), "Y", OVRInput.Controller.RTouch);

        // Rotation: right stick X → yaw around local Y.
        if (TryConsumeStick(2, rStick.x, out var syaw)) RotateLocalY(target, syaw * RotationStep);

        // Resets.
        if (OVRInput.GetDown(OVRInput.Button.One)) ResetPosition(target);
        if (OVRInput.GetDown(OVRInput.Button.Two)) ResetRotation(target);
        if (OVRInput.Get(OVRInput.Button.Three) && OVRInput.Get(OVRInput.Button.Four)
            && (OVRInput.GetDown(OVRInput.Button.Three) || OVRInput.GetDown(OVRInput.Button.Four)))
        {
            ResetAll(target);
        }
    }

    /// <summary>
    /// Returns true and outputs ±1 the first time the axis crosses the fire
    /// threshold, then again every 1/repeatRateHz while held. Resets when the
    /// axis returns under the rearm threshold or flips sign.
    /// </summary>
    private bool TryConsumeStick(int axisIndex, float value, out int sign)
    {
        sign = 0;
        float abs = Mathf.Abs(value);
        int currentSign = value > 0 ? 1 : (value < 0 ? -1 : 0);

        // Rearm when stick relaxes or flips direction.
        if (abs < stickRearmThreshold || currentSign != _firedSign[axisIndex])
        {
            if (abs < stickRearmThreshold)
            {
                _firedSign[axisIndex] = 0;
                _nextRepeatTime[axisIndex] = 0f;
            }
        }

        if (abs < stickFireThreshold) return false;

        if (_firedSign[axisIndex] != currentSign)
        {
            _firedSign[axisIndex] = currentSign;
            _nextRepeatTime[axisIndex] = Time.time + repeatInitialDelaySeconds;
            sign = currentSign;
            return true;
        }

        if (repeatRateHz > 0f && Time.time >= _nextRepeatTime[axisIndex])
        {
            _nextRepeatTime[axisIndex] = Time.time + 1f / repeatRateHz;
            sign = currentSign;
            return true;
        }
        return false;
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
