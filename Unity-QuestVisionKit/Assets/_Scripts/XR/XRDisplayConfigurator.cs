using System.Linq;
using UnityEngine;

/// <summary>
/// Configures Quest display refresh and CPU/GPU performance levels at app
/// start. The project ships with no display-frequency setting anywhere, so
/// Quest 3 runs the app at its 72Hz default. Add this component to a scene
/// GameObject (e.g. <c>Drift Correction System</c>) and set
/// <see cref="targetDisplayHz"/> to opt into a higher refresh.
///
/// Quest 3 supports {72, 80, 90, 120}. If the requested value isn't in
/// <c>OVRManager.display.displayFrequenciesAvailable</c> the request is
/// logged and ignored (the previous frequency is retained).
///
/// CPU/GPU performance levels (0..4) buy thermal/clock headroom for the
/// renderer. Level 2 is the SDK default; raising helps if the app is
/// CPU- or GPU-bound, at battery cost.
///
/// Logged via <see cref="SessionLogger"/> as a <c>session_event</c> with
/// <c>subtype=display_frequency</c> so the analyst can confirm what
/// frequency a given session actually ran at.
/// </summary>
[DisallowMultipleComponent]
public sealed class XRDisplayConfigurator : MonoBehaviour
{
    [Header("Display frequency")]
    [Tooltip("Desired display refresh in Hz. Quest 3 supports {72, 80, 90, 120}; " +
             "if the value isn't in OVRManager.display.displayFrequenciesAvailable the " +
             "request is logged and ignored (frequency is left unchanged).")]
    [SerializeField] private float targetDisplayHz = 90f;

    [Header("Performance levels (thermal headroom)")]
    [Tooltip("CPU performance level 0..4 (higher = more headroom, more battery). " +
             "Level 2 is the SDK default.")]
    [SerializeField, Range(0, 4)] private int cpuLevel = 2;

    [Tooltip("GPU performance level 0..4.")]
    [SerializeField, Range(0, 4)] private int gpuLevel = 2;

    private void Start()
    {
        // CPU/GPU levels first — cheap, unconditional.
        OVRPlugin.cpuLevel = cpuLevel;
        OVRPlugin.gpuLevel = gpuLevel;

        var available = OVRManager.display != null
            ? OVRManager.display.displayFrequenciesAvailable
            : null;

        bool supported = available != null
            && available.Any(f => Mathf.Approximately(f, targetDisplayHz));

        float before = OVRManager.display != null ? OVRManager.display.displayFrequency : 0f;

        if (supported)
        {
            OVRManager.display.displayFrequency = targetDisplayHz;
        }

        float after = OVRManager.display != null ? OVRManager.display.displayFrequency : 0f;

        string availableStr = available != null
            ? string.Join(",", available.Select(f => f.ToString("0")))
            : "(unknown)";

        Debug.Log($"[XRDisplayConfigurator] target={targetDisplayHz}Hz supported={supported} " +
                  $"before={before}Hz after={after}Hz available=[{availableStr}] " +
                  $"cpuLevel={cpuLevel} gpuLevel={gpuLevel}");

        if (SessionLogger.Instance != null)
        {
            var detail = $"requested={targetDisplayHz:0.#};applied={after:0.#};" +
                         $"supported={(supported ? 1 : 0)};before={before:0.#};" +
                         $"available=[{availableStr}];cpu_level={cpuLevel};gpu_level={gpuLevel}";
            SessionLogger.Instance.Enqueue(LogEvent.SessionEvent("display_frequency", detail));
        }
    }
}
