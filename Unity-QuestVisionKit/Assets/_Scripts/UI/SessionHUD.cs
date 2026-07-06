using System.Text;
using TMPro;
using UnityEngine;

/// <summary>
/// Multi-zone world-space HUD for the single/double-tag experiment. Replaces
/// <see cref="PipelineStatusHUD"/> in the new scenes (which was wired to the
/// stripped constellation system and permanently showed dead instructions).
///
/// Zones (each its own TMP text, stacked on the canvas):
///   1. Status bar   — phase · trial N/M · condition (preset/solver/policy/variant)
///   2. Guidance     — per-phase "do this next", with live capture progress
///   3. Transients   — action confirmations (IHudTransientSink-compatible)
///   4. Diagnostics  — toggleable: tag last-seen, anchor state, last correction,
///                     logger heartbeat. Toggle also forces the wireframe visible.
///
/// Audience-aware: during Running the participant wears the headset, so the HUD
/// hides entirely (nothing to read mid-walk; the experimenter gets haptics).
/// It returns on Paused ("PAUSED") and Complete ("remove the headset").
///
/// All data sources are auto-resolved — this component lives in a prefab, and
/// prefab-serialized scene references are impossible (the old prefab shipped
/// with permanently-null refs; that failure mode is designed out here).
/// </summary>
[DisallowMultipleComponent]
public sealed class SessionHUD : MonoBehaviour, IHudTransientSink
{
    [Header("Zones (prefab-internal references)")]
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_Text guidanceText;
    [SerializeField] private TMP_Text transientText;
    [SerializeField] private TMP_Text diagnosticsText;

    [Header("Display")]
    [SerializeField] private float refreshInterval = 0.15f;
    [SerializeField] private float defaultTransientSeconds = 3f;

    [Tooltip("Hide the whole HUD while trials are Running (the participant wears the headset " +
             "mid-walk and must not be distracted). Diagnostics toggle overrides.")]
    [SerializeField] private bool hideWhileRunning = true;

    // ---- auto-resolved data sources ----
    private SessionFlowController _flow;
    private ObstaclePlacementController _placement;
    private TrialSequencer _sequencer;
    private TrialLoader _loader;
    private AprilTagWireframeVisualizer _wireframe;
    private Canvas _canvas;

    private readonly StringBuilder _sb = new();
    private float _nextRefresh;
    private bool _sourcesResolved;

    // Transient state
    private string _transientMessage;
    private float _transientExpiry;

    // Hold-to-confirm progress (refreshed every frame by the controls while holding)
    private string _holdLabel;
    private float _holdProgress;
    private float _holdExpiry;

    /// <summary>Diagnostics zone visibility (also forces the tag wireframe visible).</summary>
    public bool DiagnosticsVisible { get; private set; }

    private void Awake()
    {
        _canvas = GetComponentInChildren<Canvas>(true);
        if (_canvas == null) _canvas = GetComponent<Canvas>();
    }

    private void Start()
    {
        ResolveSources();
    }

    private void ResolveSources()
    {
        if (_flow == null) _flow = FindAnyObjectByType<SessionFlowController>();
        if (_placement == null) _placement = FindAnyObjectByType<ObstaclePlacementController>();
        if (_sequencer == null) _sequencer = FindAnyObjectByType<TrialSequencer>();
        if (_loader == null) _loader = FindAnyObjectByType<TrialLoader>();
        if (_wireframe == null) _wireframe = FindAnyObjectByType<AprilTagWireframeVisualizer>();
        _sourcesResolved = _flow != null && _placement != null;
    }

    // ---- public surface ----

    /// <summary>IHudTransientSink entry point (signature-compatible with PipelineStatusHUD).</summary>
    public void ShowTransient(string message, float durationSeconds = -1f)
    {
        _transientMessage = message;
        _transientExpiry = Time.time + (durationSeconds > 0f ? durationSeconds : defaultTransientSeconds);
    }

    /// <summary>Hold-to-confirm progress; call every frame during a hold. Expires fast when not refreshed.</summary>
    public void ShowHoldProgress(string label, float t01)
    {
        _holdLabel = label;
        _holdProgress = Mathf.Clamp01(t01);
        _holdExpiry = Time.time + 0.25f;
    }

    /// <summary>Toggle the diagnostics zone (and force the tag wireframe visible while on).</summary>
    public void ToggleDiagnostics()
    {
        DiagnosticsVisible = !DiagnosticsVisible;
        if (_wireframe == null) _wireframe = FindAnyObjectByType<AprilTagWireframeVisualizer>();
        if (_wireframe != null) _wireframe.ForceVisible = DiagnosticsVisible;
        ShowTransient(DiagnosticsVisible ? "Diagnostics ON" : "Diagnostics off");
    }

    // ---- refresh loop ----

    private void Update()
    {
        if (Time.time < _nextRefresh) return;
        _nextRefresh = Time.time + refreshInterval;
        if (!_sourcesResolved) ResolveSources();
        Refresh();
    }

    private void Refresh()
    {
        var phase = _flow != null ? _flow.Phase : SessionPhase.Setup;

        // Audience rule: hide mid-walk (participant wears the headset).
        bool hidden = hideWhileRunning && phase == SessionPhase.Running && !DiagnosticsVisible
                      && (_flow == null || !_flow.WaitingForRedoClearance);
        if (_canvas != null && _canvas.gameObject.activeSelf == hidden)
            _canvas.gameObject.SetActive(!hidden);
        if (hidden) return;

        if (statusText != null) statusText.text = BuildStatus(phase);
        if (guidanceText != null) guidanceText.text = BuildGuidance(phase);
        if (transientText != null) transientText.text = BuildTransient();
        if (diagnosticsText != null)
        {
            bool show = DiagnosticsVisible;
            if (diagnosticsText.gameObject.activeSelf != show) diagnosticsText.gameObject.SetActive(show);
            if (show) diagnosticsText.text = BuildDiagnostics();
        }
    }

    private string BuildStatus(SessionPhase phase)
    {
        _sb.Clear();
        string phaseColor = phase switch
        {
            SessionPhase.Setup => ExperimentPalette.MidHex,
            SessionPhase.Ready => ExperimentPalette.GoodHex,
            SessionPhase.Running => ExperimentPalette.GoodHex,
            SessionPhase.Paused => ExperimentPalette.MidHex,
            SessionPhase.Complete => ExperimentPalette.GoodHex,
            _ => ExperimentPalette.MidHex,
        };
        _sb.Append("<color=").Append(phaseColor).Append("><b>").Append(phase.ToString().ToUpperInvariant()).Append("</b></color>");

        if (_sequencer != null && _loader != null && !_loader.MissingData)
        {
            _sb.Append("  ·  Trial ").Append(_sequencer.CurrentTrialIndex);
        }

        if (_placement != null)
        {
            _sb.Append("  ·  ").Append(_placement.CurrentPresetName)
               .Append(" (").Append(_placement.Solver)
               .Append('/').Append(_placement.Policy)
               .Append('/').Append(_placement.Variant).Append(')');
        }

        if (_flow != null && _flow.WaitingForRedoClearance)
            _sb.Append("  ·  <color=").Append(ExperimentPalette.MidHex).Append(">clearing…</color>");

        return _sb.ToString();
    }

    private string BuildGuidance(SessionPhase phase)
    {
        _sb.Clear();
        switch (phase)
        {
            case SessionPhase.Setup:
                if (_placement == null)
                {
                    _sb.Append("Placement system missing.");
                }
                else if (_placement.IsCaptureRequested)
                {
                    _sb.Append("<b>Hold steady on the tag…</b>\n")
                       .Append(_placement.CaptureSampleCount).Append('/').Append(_placement.CaptureWindowSize)
                       .Append(" samples · spread ")
                       .Append((_placement.CapturePositionSpreadMeters * 1000f).ToString("F1")).Append(" mm");
                }
                else if (_placement.SecondsSinceLastTag > 1.5f)
                {
                    _sb.Append("<color=").Append(ExperimentPalette.MidHex)
                       .Append(">Look at the tag from within 1 m.</color>\nThen HOLD the LEFT trigger to place the obstacle.");
                }
                else
                {
                    _sb.Append("Tag visible ✓\n<b>HOLD the LEFT trigger</b> to place the obstacle.");
                }
                break;

            case SessionPhase.Ready:
                _sb.Append("Placed ✓  Fine-tune with the thumbsticks (L grip = mm steps).\n")
                   .Append("<b>HOLD BOTH triggers</b> to start trials.\n")
                   .Append("<size=80%>R-grip + L trigger: re-place · R-grip + Start: change condition</size>");
                break;

            case SessionPhase.Running:
                // Only visible when diagnostics forced the HUD on, or waiting for clearance.
                _sb.Append(_flow != null && _flow.WaitingForRedoClearance
                    ? "Redo pending — walk clear of the obstacle."
                    : "Trials running.");
                break;

            case SessionPhase.Paused:
                _sb.Append("<b>PAUSED</b>\nPress Start to resume · HOLD the RIGHT trigger to redo this trial.");
                break;

            case SessionPhase.Complete:
                _sb.Append("<b>All trials complete.</b>\nPlease remove the headset.");
                break;
        }
        return _sb.ToString();
    }

    private string BuildTransient()
    {
        _sb.Clear();

        // Hold progress takes the transient slot while active (it is the
        // feedback for the in-progress action).
        if (_holdLabel != null && Time.time < _holdExpiry)
        {
            int filled = Mathf.RoundToInt(_holdProgress * 10f);
            _sb.Append("<b>").Append(_holdLabel).Append("</b>  <color=").Append(ExperimentPalette.GoodHex).Append('>');
            for (int i = 0; i < 10; i++) _sb.Append(i < filled ? '█' : '░');
            _sb.Append("</color>");
            return _sb.ToString();
        }
        _holdLabel = null;

        if (_transientMessage != null && Time.time < _transientExpiry)
        {
            _sb.Append(_transientMessage);
        }
        else
        {
            _transientMessage = null;
        }
        return _sb.ToString();
    }

    private string BuildDiagnostics()
    {
        _sb.Clear();
        if (_placement != null)
        {
            float tagAge = _placement.SecondsSinceLastTag;
            string tagColor = tagAge < 1f ? ExperimentPalette.GoodHex : ExperimentPalette.BadHex;
            _sb.Append("tag seen: <color=").Append(tagColor).Append('>')
               .Append(float.IsInfinity(tagAge) ? "never" : tagAge.ToString("F1") + "s ago").Append("</color>");
            _sb.Append("  ·  anchor: ").Append(_placement.AnchorStatus);
            if (_placement.LastCorrectionMm >= 0f)
                _sb.Append("  ·  last corr: ").Append(_placement.LastCorrectionMm.ToString("F1")).Append(" mm");
        }
        if (SessionLogger.Instance != null)
        {
            _sb.Append("\nlog: ").Append(SessionLogger.Instance.IsRunning ? "running" : "<color=" + ExperimentPalette.BadHex + ">STOPPED</color>")
               .Append(" · ").Append(SessionLogger.Instance.WrittenCount).Append('/')
               .Append(SessionLogger.Instance.EnqueuedCount).Append(" rows");
        }
        return _sb.ToString();
    }
}
