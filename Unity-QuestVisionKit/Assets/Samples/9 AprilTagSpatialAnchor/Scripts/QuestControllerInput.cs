using System;
using UnityEngine;

/// <summary>
/// Reusable controller-input layer for Quest 3 Touch controllers. Polls OVRInput
/// each frame, discretizes thumbstick deflections into ±1 "fire" events with
/// rearm + auto-repeat, and exposes raw button state via thin pass-through
/// helpers. Knows nothing about obstacles, calibration, or any specific
/// downstream behavior — consumers subscribe to <see cref="OnStickFire"/> and
/// query <see cref="IsHeld"/> / <see cref="WasPressedThisFrame"/> to map input
/// to whatever they're driving.
///
/// Drop one on a GameObject in the scene and reference it from controllers like
/// <see cref="ObstacleFinesseController"/>. Multiple consumers can share the
/// same instance.
/// </summary>
public class QuestControllerInput : MonoBehaviour
{
    public enum StickAxis { LeftX, LeftY, RightX, RightY }

    [Header("Stick discretization")]
    [SerializeField, Range(0.3f, 0.95f)] private float stickFireThreshold = 0.7f;
    [SerializeField, Range(0.05f, 0.5f)] private float stickRearmThreshold = 0.3f;

    [Tooltip("When stick is held past the fire threshold, repeat fires at this rate (Hz). 0 = one fire per flick.")]
    [SerializeField] private float repeatRateHz = 5f;

    [Tooltip("Delay after the initial fire before auto-repeat kicks in (seconds).")]
    [SerializeField] private float repeatInitialDelaySeconds = 0.35f;

    /// <summary>Fired once when an axis crosses the fire threshold, then again at <c>repeatRateHz</c> while held. Sign is ±1.</summary>
    public event Action<StickAxis, int> OnStickFire;

    private readonly int[] _firedSign = new int[4];
    private readonly float[] _nextRepeatTime = new float[4];

    public bool IsHeld(OVRInput.Button b) => OVRInput.Get(b);
    public bool WasPressedThisFrame(OVRInput.Button b) => OVRInput.GetDown(b);
    public bool WasReleasedThisFrame(OVRInput.Button b) => OVRInput.GetUp(b);

    private void Update()
    {
        var l = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);
        var r = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);

        if (TryConsumeStick(0, l.x, out var sx)) OnStickFire?.Invoke(StickAxis.LeftX, sx);
        if (TryConsumeStick(1, l.y, out var sy)) OnStickFire?.Invoke(StickAxis.LeftY, sy);
        if (TryConsumeStick(2, r.x, out var srx)) OnStickFire?.Invoke(StickAxis.RightX, srx);
        if (TryConsumeStick(3, r.y, out var sry)) OnStickFire?.Invoke(StickAxis.RightY, sry);
    }

    private bool TryConsumeStick(int axisIndex, float value, out int sign)
    {
        sign = 0;
        float abs = Mathf.Abs(value);
        int currentSign = value > 0 ? 1 : (value < 0 ? -1 : 0);

        if (abs < stickRearmThreshold)
        {
            _firedSign[axisIndex] = 0;
            _nextRepeatTime[axisIndex] = 0f;
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
}
