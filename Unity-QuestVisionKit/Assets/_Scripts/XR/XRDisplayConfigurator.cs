using System.Collections;
using System.Globalization;
using System.Linq;
using UnityEngine;

/// <summary>
/// Operator panel for Quest display refresh and CPU/GPU performance levels.
/// Attach to a scene GameObject (the <c>Display Config</c> root in the single-
/// tag scenes) and pick a refresh rate from the dropdown.
///
/// Default is 72 Hz, deliberately. Field testing at 90 Hz showed the app
/// framerate wobbling in the 80-90 range, and the judder was worse for comfort
/// than the lower rate: 72 Hz gives the renderer 13.9 ms per frame instead of
/// 11.1 ms, ~25% more headroom, which is enough to hold a steady cadence. A
/// walking participant judging obstacle height is sensitive to pacing, so a
/// stable 72 beats an intermittent 90. Operators previously forced this from
/// the headset's own system settings, which nothing recorded — now it is a
/// serialized property of the build and every session logs what it ran at.
///
/// Quest 3 exposes {72, 80, 90, 120}; 120 is opt-in and may be absent. If the
/// requested value isn't in <c>OVRManager.display.displayFrequenciesAvailable</c>
/// the request is logged and ignored (previous frequency retained), so the
/// <c>applied=</c> field in the log is the ground truth, not the Inspector.
///
/// Note that <c>Application.targetFrameRate</c> and <c>QualitySettings.vSyncCount</c>
/// are NOT levers on this platform — the Oculus compositor drives frame pacing
/// off the display frequency — so this component deliberately touches neither.
///
/// CPU/GPU performance levels buy thermal/clock headroom for the renderer.
/// These go through <c>OVRPlugin.suggestedCpuPerfLevel</c>, NOT the older
/// <c>OVRPlugin.cpuLevel</c> ints this component used to set: the project runs
/// the OpenXR loader (com.unity.xr.oculus isn't even installed), where the old
/// ints call a legacy OVRP 1.1.0 entry point that does nothing. The modern
/// property routes through the OpenXR <c>XR_EXT_performance_settings</c>
/// extension, which must be enabled for Android in OpenXRPackageSettings —
/// if it is off, these levels are silently inert.
///
/// <c>SustainedHigh</c> is the default and the highest level safe to hold for a
/// whole session. <c>Boost</c> is explicitly a short-burst level; the runtime may
/// throttle it, so over a 45-90 min walk it can make pacing worse, not better.
///
/// Logged via <see cref="SessionLogger"/> as a <c>session_event</c> with
/// <c>subtype=display_frequency</c> on every apply — boot, doff/don re-assert,
/// web-console cycle, or context menu — so a mid-session change is visible to
/// the analyst rather than silently invalidating the comparison.
/// </summary>
[DisallowMultipleComponent]
public sealed class XRDisplayConfigurator : MonoBehaviour
{
    /// <summary>
    /// Ordinal-backed on purpose: index 0 is 72 Hz, so a scene that loses its
    /// serialized value (this field replaced a free float) lands on the
    /// intended default instead of an invalid member.
    /// </summary>
    public enum DisplayHz { Hz72 = 0, Hz80 = 1, Hz90 = 2, Hz120 = 3 }

    /// <summary>
    /// Mirrors <c>OVRPlugin.ProcessorPerformanceLevel</c>. Declared locally so the
    /// Inspector dropdown doesn't offer the SDK enum's <c>EnumSize</c> sentinel
    /// as a selectable (and meaningless) option.
    /// </summary>
    public enum PerfLevel { PowerSavings = 0, SustainedLow = 1, SustainedHigh = 2, Boost = 3 }

    [Header("Display frequency")]
    [Tooltip("Display refresh to request. 72 is the default and the smoothest measured setting — " +
             "90 showed an 80-90 fps wobble in the field. 120 is opt-in on Quest 3 and may not be " +
             "available; unsupported requests are logged and ignored, leaving the rate unchanged.")]
    [SerializeField] private DisplayHz targetHz = DisplayHz.Hz72;

    [Tooltip("Re-request the target rate if something else changes it mid-session (headset doff/don, " +
             "system power policy). Capped at a few attempts so the app never fights the OS in a loop.")]
    [SerializeField] private bool reassertOnExternalChange = true;

    [Tooltip("Seconds to wait before reading back the applied rate. The set is asynchronous, so a " +
             "same-frame readback reports the OLD value even on success. 0.25s is comfortably past it.")]
    [SerializeField, Range(0.05f, 2f)] private float confirmDelaySeconds = 0.25f;

    [Header("Performance levels (thermal headroom)")]
    [Tooltip("CPU performance hint. SustainedHigh is the SDK default and the highest level safe " +
             "to hold for a whole session. Boost is a SHORT-BURST level — the runtime may throttle " +
             "it, so it can hurt pacing over a 45-90 min walk. Requires the XR Performance Settings " +
             "feature enabled for Android in OpenXRPackageSettings, else this is inert.")]
    [SerializeField] private PerfLevel cpuLevel = PerfLevel.SustainedHigh;

    [Tooltip("GPU performance hint. See cpuLevel — raising either costs battery.")]
    [SerializeField] private PerfLevel gpuLevel = PerfLevel.SustainedHigh;

    /// <summary>The rate the Inspector asks for. May differ from what the device granted.</summary>
    public float RequestedHz => ToHz(targetHz);

    /// <summary>The rate the device actually reported after the last apply. 0 with no HMD.</summary>
    public float AppliedHz { get; private set; }

    /// <summary>"72 Hz" — for HUD/console display.</summary>
    public string HzLabel => $"{ToHz(targetHz):0} Hz";

    // The OS wins if it refuses us. Without a cap, a refused request plus
    // reassert would ping-pong forever against the system's own setting.
    private const int MaxReassertAttempts = 3;
    private int _reassertAttempts;

    private static float ToHz(DisplayHz hz) => hz switch
    {
        DisplayHz.Hz72 => 72f,
        DisplayHz.Hz80 => 80f,
        DisplayHz.Hz90 => 90f,
        DisplayHz.Hz120 => 120f,
        _ => 72f,
    };

    private static OVRPlugin.ProcessorPerformanceLevel ToPerf(PerfLevel level) => level switch
    {
        PerfLevel.PowerSavings => OVRPlugin.ProcessorPerformanceLevel.PowerSavings,
        PerfLevel.SustainedLow => OVRPlugin.ProcessorPerformanceLevel.SustainedLow,
        PerfLevel.Boost => OVRPlugin.ProcessorPerformanceLevel.Boost,
        _ => OVRPlugin.ProcessorPerformanceLevel.SustainedHigh,
    };

    // Static SDK events — a subscription leaked past destruction throws, so
    // the OnDisable unsubscribe is required, not tidiness.
    private void OnEnable()
    {
        OVRManager.HMDMounted += HandleHmdMounted;
        OVRManager.DisplayRefreshRateChanged += HandleRefreshRateChanged;
    }

    private void OnDisable()
    {
        OVRManager.HMDMounted -= HandleHmdMounted;
        OVRManager.DisplayRefreshRateChanged -= HandleRefreshRateChanged;
    }

    // Start, not Awake: OVRManager.display is constructed in OVRManager.Awake,
    // the same ordering constraint ObstaclePlacementController documents for
    // RecenteredPose. It is still null-guarded for editor scenes with no rig.
    private void Start() => Apply("boot");

    /// <summary>
    /// Push the current settings to the device and log the outcome.
    /// <paramref name="reason"/> lands in the session_event detail blob so the
    /// analyst can tell a boot-time apply from a mid-session change.
    /// </summary>
    public void Apply(string reason)
    {
        // Perf hints first — cheap, unconditional. Note these are write-only in
        // practice: on the OpenXR path the getter just echoes a cached static,
        // so reading back would NOT prove the runtime honoured the hint. The
        // only real confirmation is the XR Performance Settings feature being
        // enabled for Android in OpenXRPackageSettings.
        OVRPlugin.suggestedCpuPerfLevel = ToPerf(cpuLevel);
        OVRPlugin.suggestedGpuPerfLevel = ToPerf(gpuLevel);

        float requested = ToHz(targetHz);

        var available = OVRManager.display != null
            ? OVRManager.display.displayFrequenciesAvailable
            : null;

        bool supported = available != null
            && available.Any(f => Mathf.Approximately(f, requested));

        float before = OVRManager.display != null ? OVRManager.display.displayFrequency : 0f;

        if (supported)
        {
            OVRManager.display.displayFrequency = requested;
        }

        string availableStr = available != null
            ? string.Join(",", available.Select(f => f.ToString("0")))
            : "(unknown)";

        Debug.Log($"[XRDisplayConfigurator] requesting target={requested}Hz supported={supported} " +
                  $"before={before}Hz available=[{availableStr}] " +
                  $"cpuLevel={cpuLevel} gpuLevel={gpuLevel} reason={reason}");

        // ovrp_SetSystemDisplayFrequency is fire-and-forget — OVRPlugin discards
        // its Result, and the runtime signals completion asynchronously through
        // PollEvent -> DisplayRefreshRateChanged a frame or more later. Reading
        // the frequency back on this line would report the OLD value even on a
        // fully successful request, so the confirm is deferred. Every
        // `applied=` written before this was introduced is therefore unverified.
        if (isActiveAndEnabled)
        {
            StartCoroutine(ConfirmAndLog(reason, requested, before, supported, availableStr));
        }
        else
        {
            LogApply(reason, requested, before, before, supported, availableStr);
        }
    }

    private IEnumerator ConfirmAndLog(string reason, float requested, float before,
                                      bool supported, string availableStr)
    {
        yield return new WaitForSecondsRealtime(confirmDelaySeconds);

        float after = OVRManager.display != null ? OVRManager.display.displayFrequency : 0f;
        AppliedHz = after;
        LogApply(reason, requested, before, after, supported, availableStr);
    }

    private void LogApply(string reason, float requested, float before, float after,
                          bool supported, string availableStr)
    {
        bool honored = !supported || Mathf.Approximately(after, requested);
        if (!honored)
        {
            Debug.LogWarning($"[XRDisplayConfigurator] request NOT honored: asked {requested}Hz, " +
                             $"device reports {after}Hz. The headset is overriding the app.");
        }
        else
        {
            Debug.Log($"[XRDisplayConfigurator] confirmed applied={after}Hz (requested {requested}Hz, " +
                      $"reason={reason})");
        }

        if (SessionLogger.Instance != null)
        {
            var detail = string.Format(CultureInfo.InvariantCulture,
                "requested={0:0.#};applied={1:0.#};supported={2};before={3:0.#};" +
                "available=[{4}];cpu_level={5};gpu_level={6};reason={7}",
                requested, after, supported ? 1 : 0, before,
                availableStr, cpuLevel, gpuLevel, reason);
            SessionLogger.Instance.Enqueue(LogEvent.SessionEvent("display_frequency", detail));
        }
    }

    /// <summary>Web-console / context-menu knob: 72 -> 80 -> 90 -> 120 -> 72.</summary>
    public DisplayHz CycleHz(string reason = "console")
    {
        targetHz = targetHz switch
        {
            DisplayHz.Hz72 => DisplayHz.Hz80,
            DisplayHz.Hz80 => DisplayHz.Hz90,
            DisplayHz.Hz90 => DisplayHz.Hz120,
            _ => DisplayHz.Hz72,
        };
        _reassertAttempts = 0;
        Apply(reason);
        return targetHz;
    }

    // Doff/don suspends the app and can reset the rate; re-assert on remount.
    private void HandleHmdMounted()
    {
        _reassertAttempts = 0;
        Apply("hmd_mounted");
    }

    // Fires for our own writes too, so "matches target" is the exit condition
    // rather than a timing guard — the SDK raises this from its own update,
    // not synchronously inside the setter.
    private void HandleRefreshRateChanged(float from, float to)
    {
        AppliedHz = to;

        float want = ToHz(targetHz);
        if (Mathf.Approximately(to, want))
        {
            _reassertAttempts = 0;
            return;
        }

        if (!reassertOnExternalChange)
        {
            Debug.LogWarning($"[XRDisplayConfigurator] rate changed {from}Hz -> {to}Hz, " +
                             $"target is {want}Hz (reassert disabled).");
            return;
        }

        if (_reassertAttempts >= MaxReassertAttempts)
        {
            Debug.LogWarning($"[XRDisplayConfigurator] rate changed {from}Hz -> {to}Hz and " +
                             $"{MaxReassertAttempts} re-assert attempts failed to hold {want}Hz. " +
                             "Giving up — the system is pinning the rate.");
            return;
        }

        _reassertAttempts++;
        Apply("external_change");
    }

    [ContextMenu("Apply Now")]
    private void ApplyFromContextMenu() => Apply("context_menu");

    [ContextMenu("Set 72 Hz")]
    private void Set72Hz() => SetFromContextMenu(DisplayHz.Hz72);

    [ContextMenu("Set 90 Hz")]
    private void Set90Hz() => SetFromContextMenu(DisplayHz.Hz90);

    [ContextMenu("Cycle Hz")]
    private void CycleFromContextMenu() => CycleHz("context_menu");

    private void SetFromContextMenu(DisplayHz hz)
    {
        targetHz = hz;
        _reassertAttempts = 0;
        Apply("context_menu");
    }
}
