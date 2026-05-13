using System.Collections;
using UnityEngine;

/// <summary>
/// Singleton MonoBehaviour that samples Touch controller pose/validity/battery/haptics
/// once per frame and exposes a stable read-only surface to downstream consumers.
///
/// Poll-driven, in contrast to the existing <see cref="QuestControllerInput"/>
/// which is event-driven (button/stick fires). Sleep mitigation, rigid-body
/// validation, anchor-baseline logging, and (Phase 2) controller drift correction
/// all read from the same per-frame snapshot here so they agree on what was
/// observed in a given frame.
///
/// Pose source: <see cref="OVRCameraRig.leftHandAnchor"/> / <c>rightHandAnchor</c>
/// transforms — already in world space, no manual tracking-space transform needed.
/// The <c>*Detached</c> anchor variants are deliberately not used (those don't
/// move with tracking-space repositioning, which is the opposite of what we want).
///
/// Velocity is self-computed from pose-delta because
/// <c>OVRInput.GetLocalControllerVelocity</c> is extrapolation-corrupted (returns
/// the runtime's predicted velocity, which is identical to its predicted position
/// derivative — useless for detecting dead-reckoning during occlusion). Same
/// caveat applies to <c>GetControllerPositionValid</c>, which returns <c>true</c>
/// for extrapolated poses — consumers should cross-check velocity-from-delta
/// against a sanity bound (e.g. 2 cm/s for the spec's Subsystem 2 gate).
///
/// Battery is sampled once per <see cref="batterySampleIntervalSeconds"/> (spec
/// default 60s) and emitted as a <c>sleep_event</c> row with subtype
/// <c>battery_sample</c>. Cached between samples and exposed on the public surface.
/// </summary>
[DisallowMultipleComponent]
public sealed class ControllerPoseProvider : MonoBehaviour
{
    public static ControllerPoseProvider Instance { get; private set; }

    [SerializeField] private OVRCameraRig cameraRig;

    [Tooltip("Battery sample interval (seconds). Spec calls for 1/min.")]
    [SerializeField, Range(5f, 300f)] private float batterySampleIntervalSeconds = 60f;

    // ---- public read-only surface ----
    public Pose LeftPose { get; private set; } = Pose.identity;
    public Pose RightPose { get; private set; } = Pose.identity;
    public Vector3 LeftVelocity { get; private set; }
    public Vector3 RightVelocity { get; private set; }
    public bool LeftPositionValid { get; private set; }
    public bool RightPositionValid { get; private set; }
    public bool LeftOrientationValid { get; private set; }
    public bool RightOrientationValid { get; private set; }
    public bool LeftConnected { get; private set; }
    public bool RightConnected { get; private set; }
    public float LeftBatteryPercent { get; private set; } = -1f;
    public float RightBatteryPercent { get; private set; } = -1f;
    public double LastSampleSessionTime { get; private set; }
    public float DeltaTime { get; private set; }

    public Transform LeftHandAnchor => _leftAnchor;
    public Transform RightHandAnchor => _rightAnchor;
    public OVRCameraRig CameraRig => cameraRig;

    private Transform _leftAnchor;
    private Transform _rightAnchor;
    private Vector3 _prevLeftPos, _prevRightPos;
    private bool _havePrevPose;
    private float _nextBatterySampleTime;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning($"[ControllerPoseProvider] Duplicate instance on {gameObject.name}; destroying this one.");
            Destroy(this);
            return;
        }
        Instance = this;
        if (!cameraRig) cameraRig = FindObjectOfType<OVRCameraRig>();
    }

    private void Start()
    {
        ResolveAnchors();
        if (_leftAnchor == null || _rightAnchor == null)
        {
            Debug.LogError("[ControllerPoseProvider] Could not resolve OVRCameraRig hand anchors. Pose surface will not populate.");
        }
    }

    private void OnDisable()
    {
        // Stop any active haptics so we don't leave controllers buzzing on scene unload.
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.LTouch);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
    }

    private void Update()
    {
        if (_leftAnchor == null || _rightAnchor == null) ResolveAnchors();
        if (_leftAnchor == null || _rightAnchor == null) return;

        var dt = Time.deltaTime;
        DeltaTime = dt;

        var lpos = _leftAnchor.position;
        var lrot = _leftAnchor.rotation;
        var rpos = _rightAnchor.position;
        var rrot = _rightAnchor.rotation;

        if (_havePrevPose && dt > 1e-6f)
        {
            LeftVelocity = (lpos - _prevLeftPos) / dt;
            RightVelocity = (rpos - _prevRightPos) / dt;
        }
        else
        {
            LeftVelocity = Vector3.zero;
            RightVelocity = Vector3.zero;
        }

        LeftPose = new Pose(lpos, lrot);
        RightPose = new Pose(rpos, rrot);
        _prevLeftPos = lpos;
        _prevRightPos = rpos;
        _havePrevPose = true;

        LeftPositionValid = OVRInput.GetControllerPositionValid(OVRInput.Controller.LTouch);
        RightPositionValid = OVRInput.GetControllerPositionValid(OVRInput.Controller.RTouch);
        LeftOrientationValid = OVRInput.GetControllerOrientationValid(OVRInput.Controller.LTouch);
        RightOrientationValid = OVRInput.GetControllerOrientationValid(OVRInput.Controller.RTouch);
        LeftConnected = OVRInput.IsControllerConnected(OVRInput.Controller.LTouch);
        RightConnected = OVRInput.IsControllerConnected(OVRInput.Controller.RTouch);

        if (Time.time >= _nextBatterySampleTime)
        {
            LeftBatteryPercent = OVRInput.GetControllerBatteryPercentRemaining(OVRInput.Controller.LTouch);
            RightBatteryPercent = OVRInput.GetControllerBatteryPercentRemaining(OVRInput.Controller.RTouch);
            _nextBatterySampleTime = Time.time + batterySampleIntervalSeconds;
            if (SessionLogger.Instance != null)
            {
                var e = LogEvent.SleepEvent("battery_sample");
                e.BatteryLPercent = LeftBatteryPercent;
                e.BatteryRPercent = RightBatteryPercent;
                e.ConnectedL = LeftConnected;
                e.ConnectedR = RightConnected;
                SessionLogger.Instance.Enqueue(e);
            }
        }

        if (SessionLogger.Instance != null) LastSampleSessionTime = SessionLogger.Instance.NowSession;
    }

    private void ResolveAnchors()
    {
        if (!cameraRig) cameraRig = FindObjectOfType<OVRCameraRig>();
        if (!cameraRig) return;
        _leftAnchor = cameraRig.leftHandAnchor;
        _rightAnchor = cameraRig.rightHandAnchor;
    }

    /// <summary>
    /// Fire a single haptic pulse on the specified controller. Auto-stops via
    /// coroutine. Safe to call repeatedly — overlapping pulses run their own
    /// stop timers (the controller keeps vibrating until the latest one fires).
    /// </summary>
    public void Pulse(OVRInput.Controller controller, float frequency, float amplitude, float durationSeconds)
    {
        OVRInput.SetControllerVibration(Mathf.Clamp01(frequency), Mathf.Clamp01(amplitude), controller);
        StartCoroutine(StopAfter(controller, Mathf.Max(0.01f, durationSeconds)));
    }

    private IEnumerator StopAfter(OVRInput.Controller controller, float seconds)
    {
        yield return new WaitForSeconds(seconds);
        OVRInput.SetControllerVibration(0f, 0f, controller);
    }
}
