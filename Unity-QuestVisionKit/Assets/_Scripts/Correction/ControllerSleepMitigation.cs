using System.Collections;
using UnityEngine;

/// <summary>
/// Keeps both Touch controllers awake during long stationary sessions by pulsing
/// a low-amplitude haptic at a configurable cadence. Without intervention a Quest
/// 3 controller seated stationary on the rig will sleep within minutes and stop
/// reporting valid poses, which kills the controller-based drift correction
/// (Phase 2's Subsystem 2).
///
/// Also acts as the disconnect/reconnect watchdog: reads connection and validity
/// flags from <see cref="ControllerPoseProvider"/> each frame and emits
/// <c>sleep_event</c> rows on state transitions.
///
/// Pulse defaults are deliberately gentle (50ms × 0.02 amplitude) — well below
/// perceptual threshold for a stationary held controller and orders of magnitude
/// quieter than the finesse-channel <c>ObstacleFinesseController.Pulse</c>
/// (40ms × 0.4). Tighten <see cref="pulseIntervalSeconds"/> to 3s if the 10-min
/// stationary test still shows sleep events.
/// </summary>
[DisallowMultipleComponent]
public sealed class ControllerSleepMitigation : MonoBehaviour
{
    [Header("Wiring")]
    [SerializeField] private ControllerPoseProvider provider;

    [Header("Keep-alive pulse")]
    [Tooltip("Seconds between keep-alive pulses. Spec default 5s. Tighten to 3s if sleep persists.")]
    [SerializeField, Range(1f, 30f)] private float pulseIntervalSeconds = 5f;

    [Tooltip("Pulse duration (seconds). 50ms is the spec default and well under any IMU ringing concern.")]
    [SerializeField, Range(0.01f, 0.5f)] private float pulseDurationSeconds = 0.05f;

    [Tooltip("Pulse amplitude. 0.02 is far below perceptual threshold for a stationary controller.")]
    [SerializeField, Range(0f, 1f)] private float pulseAmplitude = 0.02f;

    [Tooltip("Pulse frequency (0-1, hardware-mapped). 1 = max frequency curve.")]
    [SerializeField, Range(0f, 1f)] private float pulseFrequency = 1f;

    [Header("Last-known-good cache")]
    [Tooltip("If reconnect produces a pose this far from the last-known-good pose, log a warning. Indicates physical disturbance.")]
    [SerializeField, Range(0.01f, 1f)] private float reconnectMovementWarnMeters = 0.05f;

    private bool _running;
    private float _lastPulseTime;
    private bool _prevLeftConnected = true;
    private bool _prevRightConnected = true;
    private Vector3 _lastLeftKnownPos, _lastRightKnownPos;
    private bool _haveLastKnown;

    private void Awake()
    {
        if (!provider) provider = FindObjectOfType<ControllerPoseProvider>();
    }

    private void OnEnable()
    {
        if (!provider)
        {
            Debug.LogError("[ControllerSleepMitigation] No ControllerPoseProvider found. Disabling.");
            enabled = false;
            return;
        }
        _running = true;
        _lastPulseTime = Time.time;  // skip first pulse so we don't fire immediately on enable
        StartCoroutine(KeepAliveLoop());
    }

    private void OnDisable()
    {
        _running = false;
    }

    private IEnumerator KeepAliveLoop()
    {
        while (_running)
        {
            yield return new WaitForSeconds(pulseIntervalSeconds);
            if (!_running) yield break;

            // Pulse both controllers (independently — each starts its own auto-stop coroutine).
            provider.Pulse(OVRInput.Controller.LTouch, pulseFrequency, pulseAmplitude, pulseDurationSeconds);
            provider.Pulse(OVRInput.Controller.RTouch, pulseFrequency, pulseAmplitude, pulseDurationSeconds);

            float sinceLast = Time.time - _lastPulseTime;
            _lastPulseTime = Time.time;

            if (SessionLogger.Instance != null)
            {
                var e = LogEvent.SleepEvent("pulse", sinceLast);
                e.ConnectedL = provider.LeftConnected;
                e.ConnectedR = provider.RightConnected;
                e.PositionValidL = provider.LeftPositionValid;
                e.PositionValidR = provider.RightPositionValid;
                SessionLogger.Instance.Enqueue(e);
            }
        }
    }

    private void Update()
    {
        if (provider == null) return;

        // Disconnect / reconnect watchdog.
        if (provider.LeftConnected != _prevLeftConnected)
        {
            EmitConnectionTransition(OVRInput.Controller.LTouch, provider.LeftConnected, provider.LeftPose.position, ref _lastLeftKnownPos);
            _prevLeftConnected = provider.LeftConnected;
        }
        if (provider.RightConnected != _prevRightConnected)
        {
            EmitConnectionTransition(OVRInput.Controller.RTouch, provider.RightConnected, provider.RightPose.position, ref _lastRightKnownPos);
            _prevRightConnected = provider.RightConnected;
        }

        // Cache last-known-good position while connected + valid.
        if (provider.LeftConnected && provider.LeftPositionValid)
            _lastLeftKnownPos = provider.LeftPose.position;
        if (provider.RightConnected && provider.RightPositionValid)
            _lastRightKnownPos = provider.RightPose.position;
        _haveLastKnown = true;
    }

    private void EmitConnectionTransition(OVRInput.Controller ctrl, bool nowConnected, Vector3 currentPos, ref Vector3 lastKnown)
    {
        if (SessionLogger.Instance == null) return;
        string side = ctrl == OVRInput.Controller.LTouch ? "L" : "R";
        string type = nowConnected ? "reconnect" : "disconnect";
        var e = LogEvent.SleepEvent(type);
        if (ctrl == OVRInput.Controller.LTouch)
        {
            e.ConnectedL = nowConnected;
            e.ControllerLPos = currentPos;
        }
        else
        {
            e.ConnectedR = nowConnected;
            e.ControllerRPos = currentPos;
        }
        SessionLogger.Instance.Enqueue(e);

        if (nowConnected && _haveLastKnown)
        {
            float moved = Vector3.Distance(currentPos, lastKnown);
            if (moved > reconnectMovementWarnMeters)
            {
                var warn = LogEvent.SessionEvent("reconnect_moved",
                    $"side={side};moved_m={moved:F3};warn_threshold_m={reconnectMovementWarnMeters:F3}");
                SessionLogger.Instance.Enqueue(warn);
                Debug.LogWarning($"[ControllerSleepMitigation] {side} reconnect moved {moved * 100f:F1}cm from last-known — physical disturbance likely.");
            }
        }
    }
}
