using UnityEngine;

/// <summary>
/// Phase-aware AprilTag scan profiles. Field test measured 54-59 fps with
/// 40 Hz scanning (detection runs synchronously on the main thread), and the
/// judder was the top comfort complaint — while placement quality wants MORE
/// pixels, not more frames.
///
///   Quality (Setup/Ready): full-resolution capture, no distance gate. Lag
///   here is acceptable in service of a better pose solution.
///   Minimal (Running/Paused/Complete): low rate, standard downsample, and
///   the last-seen-tag distance gate on — the scanner idles entirely until
///   the walker is back within useful pose range. Deferred corrections only
///   consume detections between trials, so nothing is lost.
///
/// The web console's cycleScanProfile action forces either profile for
/// on-device A/B (auto -> quality -> minimal -> auto).
/// </summary>
[DisallowMultipleComponent]
public sealed class ScanProfilePolicy : MonoBehaviour
{
    public enum Mode { AutoByPhase, ForceQuality, ForceMinimal }

    [Header("Wiring (auto-resolved if empty)")]
    [SerializeField] private SessionFlowController flow;
    [SerializeField] private AprilTagDisplayManager tagManager;

    [Header("Quality profile — placement (Setup/Ready)")]
    [Tooltip("Scan rate during placement. Pose quality comes from resolution + the stability gate's observation window, not raw rate.")]
    [SerializeField, Range(0.5f, 90f)] private float qualityScanRateHz = 20f;
    [Tooltip("Downsample divisor during placement. 1 = full camera resolution (the calibration path's setting) - most pixels-on-tag, best pose.")]
    [SerializeField, Range(1, 8)] private int qualitySampleFactor = 1;

    [Header("Minimal profile — trials (Running/Paused/Complete)")]
    [Tooltip("Scan rate during trials. Detections are measurement-only under the Deferred policy; 8 Hz refills the stability window in ~1 s near the tag.")]
    [SerializeField, Range(0.5f, 90f)] private float minimalScanRateHz = 8f;
    [Tooltip("Downsample divisor during trials (2 = the scanner's long-standing per-frame setting).")]
    [SerializeField, Range(1, 8)] private int minimalSampleFactor = 2;
    [Tooltip("Idle the scanner entirely beyond the last-seen-tag cutoff (tag size x multiplier, configured on AprilTagDisplayManager).")]
    [SerializeField] private bool minimalDistanceGate = true;

    public Mode CurrentMode { get; private set; } = Mode.AutoByPhase;

    /// <summary>"auto:quality", "forced:minimal", ... — for HUD/console display.</summary>
    public string ModeLabel
        => (CurrentMode == Mode.AutoByPhase ? "auto:" : "forced:") + _activeProfile;

    private string _activeProfile = "?";

    private void Awake()
    {
        if (!flow) flow = FindAnyObjectByType<SessionFlowController>();
        if (!tagManager) tagManager = FindAnyObjectByType<AprilTagDisplayManager>();
    }

    private void OnEnable()
    {
        if (flow != null) flow.OnPhaseChanged += HandlePhaseChanged;
        Apply();
    }

    private void OnDisable()
    {
        if (flow != null) flow.OnPhaseChanged -= HandlePhaseChanged;
    }

    /// <summary>Web-console knob: auto -> force quality -> force minimal -> auto.</summary>
    public void CycleMode()
    {
        CurrentMode = CurrentMode switch
        {
            Mode.AutoByPhase => Mode.ForceQuality,
            Mode.ForceQuality => Mode.ForceMinimal,
            _ => Mode.AutoByPhase,
        };
        Apply();
    }

    private void HandlePhaseChanged(SessionPhase prev, SessionPhase next) => Apply();

    private void Apply()
    {
        if (tagManager == null) return;

        // No flow controller (sample scene): behave like placement — gate off.
        bool quality = CurrentMode == Mode.ForceQuality
                       || (CurrentMode == Mode.AutoByPhase
                           && (flow == null
                               || flow.Phase == SessionPhase.Setup
                               || flow.Phase == SessionPhase.Ready));

        tagManager.ScanRateHz = quality ? qualityScanRateHz : minimalScanRateHz;
        tagManager.DistanceGateToLastTag = !quality && minimalDistanceGate;
        if (tagManager.Scanner != null)
            tagManager.Scanner.SampleFactor = quality ? qualitySampleFactor : minimalSampleFactor;

        _activeProfile = quality ? "quality" : "minimal";
    }
}
