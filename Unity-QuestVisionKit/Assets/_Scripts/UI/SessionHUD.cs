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
///   5. Walk popup   — Running-only: a large "Walk n" takeover on trial
///                     advance/skip, delayed a few seconds so the participant
///                     is off the gait mat first, then hidden again.
///
/// Audience-aware: during Running the participant wears the headset, so the HUD
/// hides entirely (nothing to read mid-walk; the experimenter gets haptics).
/// A trial advance briefly pops the large walk readout for the participant.
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
    [SerializeField] private TMP_Text walkPopupText;

    [Header("Display")]
    [SerializeField] private float refreshInterval = 0.15f;
    [SerializeField] private float defaultTransientSeconds = 3f;

    [Tooltip("How long the large \"Walk n\" readout shows after a mid-run trial advance/skip (s).")]
    [SerializeField] private float walkPopupSeconds = 2f;

    [Tooltip("Delay before the walk readout appears after advance — the participant must step " +
             "off the gait mat before any visual stimulus (s).")]
    [SerializeField] private float walkPopupDelaySeconds = 3f;

    [Tooltip("Hide the whole HUD while trials are Running (the participant wears the headset " +
             "mid-walk and must not be distracted). Diagnostics toggle overrides.")]
    [SerializeField] private bool hideWhileRunning = true;

    // ---- auto-resolved data sources ----
    private SessionFlowController _flow;
    private ObstaclePlacementController _placement;
    private TrialSequencer _sequencer;
    private TrialLoader _loader;
    private AprilTagWireframeVisualizer _wireframe;
    private StereoAprilTagScanner _stereoScanner;
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
    private string _pickerText;
    private float _pickerExpiry;

    // Walk popup ("Walk n") — delayed canvas takeover while Running-hidden.
    private string _walkPopupMessage;
    private float _walkPopupShowAt;
    private float _walkPopupExpiry;

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

    private void OnEnable()
    {
        if (_sequencer != null) _sequencer.OnTrialLoaded += HandleTrialLoaded;
    }

    private void OnDisable()
    {
        if (_sequencer != null) _sequencer.OnTrialLoaded -= HandleTrialLoaded;
    }

    private void ResolveSources()
    {
        if (_flow == null) _flow = FindAnyObjectByType<SessionFlowController>();
        if (_placement == null) _placement = FindAnyObjectByType<ObstaclePlacementController>();
        if (_sequencer == null)
        {
            _sequencer = FindAnyObjectByType<TrialSequencer>();
            // Subscribe at acquisition: the null guard means this fires at most
            // once per found sequencer, and acquisition implies we are enabled
            // (only Update calls this), so OnEnable/OnDisable stay balanced.
            if (_sequencer != null) _sequencer.OnTrialLoaded += HandleTrialLoaded;
        }
        if (_loader == null) _loader = FindAnyObjectByType<TrialLoader>();
        if (_wireframe == null) _wireframe = FindAnyObjectByType<AprilTagWireframeVisualizer>();
        if (_stereoScanner == null) _stereoScanner = FindAnyObjectByType<StereoAprilTagScanner>();
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

    /// <summary>Trial-picker candidate readout; call every frame while the picker
    /// is open, like ShowHoldProgress. Self-expires when the picker stops pushing.
    /// Ranks BELOW hold progress (the commit bar carries the candidate in its own
    /// label) and ABOVE plain transients - so the picker must compose its own
    /// messages into this string rather than calling ShowTransient, which it
    /// would mask anyway.</summary>
    public void ShowTrialPicker(string text)
    {
        _pickerText = text;
        _pickerExpiry = Time.time + 0.25f;
    }

    /// <summary>Toggle the diagnostics zone (and force the tag wireframe visible while on).</summary>
    public void ToggleDiagnostics()
    {
        DiagnosticsVisible = !DiagnosticsVisible;
        if (_wireframe == null) _wireframe = FindAnyObjectByType<AprilTagWireframeVisualizer>();
        if (_wireframe != null) _wireframe.ForceVisible = DiagnosticsVisible;
        ShowTransient(DiagnosticsVisible ? "Diagnostics ON" : "Diagnostics off");
    }

    /// <summary>Trial advanced/skipped mid-run: arm the walk popup. Record only —
    /// the display decision lives in Refresh, AFTER the flow controller has
    /// settled clearance flags for this load (OnTrialLoaded fires synchronously
    /// from LoadTrial, before JumpBy sets WaitingForRedoClearance).</summary>
    private void HandleTrialLoaded(TrialCondition condition)
    {
        if (_flow == null || _flow.Phase != SessionPhase.Running) return;
        var reason = _sequencer != null ? _sequencer.LastLoadReason : TrialLoadReason.Initial;
        // Advance/Jump only. Initial is pre-run; Redo repeats the number the
        // participant just walked (its own haptics + clearance HUD already signal).
        if (reason != TrialLoadReason.Advance && reason != TrialLoadReason.Jump) return;

        // Raw CSV trial index (0-based in the lab's files) — the same number the
        // status bar, logs, and web console lead with. Shown after a delay so the
        // participant has stepped off the gait mat before any visual stimulus.
        _walkPopupMessage = $"Walk {_sequencer.CurrentTrialIndex}";
        _walkPopupShowAt = Time.time + walkPopupDelaySeconds;
        _walkPopupExpiry = _walkPopupShowAt + walkPopupSeconds;
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

        // Walk popup: evaluated BEFORE the hidden computation — expiry must tick
        // even on refreshes that end in the hidden early-return. Running-only:
        // any other phase cancels it (pause/complete mid-popup must not leave a
        // resumable timer behind). A pending message (armed, still inside the
        // gait-mat delay) survives until show time — clear on expiry only.
        if (phase != SessionPhase.Running) _walkPopupExpiry = 0f;
        if (Time.time >= _walkPopupExpiry) _walkPopupMessage = null;
        bool popupLive = _walkPopupMessage != null && Time.time >= _walkPopupShowAt;

        // Audience rule: hide mid-walk (participant wears the headset).
        // Disable the Canvas COMPONENT, not its GameObject — the canvas lives on
        // this same GameObject, and SetActive(false) would kill our own Update
        // loop and never come back.
        bool hidden = hideWhileRunning && phase == SessionPhase.Running && !DiagnosticsVisible
                      && (_flow == null || !_flow.WaitingForRedoClearance);

        // Popup borrows the canvas only while the HUD would otherwise be hidden;
        // a visible HUD (diagnostics/clearance) already carries the trial position.
        bool popupOnly = popupLive && hidden && walkPopupText != null;
        if (popupOnly) hidden = false;
        if (walkPopupText != null && walkPopupText.gameObject.activeSelf != popupOnly)
            walkPopupText.gameObject.SetActive(popupOnly);

        if (_canvas != null && _canvas.enabled == hidden)
            _canvas.enabled = !hidden;
        if (hidden) return;

        if (popupOnly)
        {
            walkPopupText.text = _walkPopupMessage;
            // Blank the packed zones — the canvas re-enabled with whatever text
            // the last fully-visible phase left behind.
            if (statusText != null) statusText.text = string.Empty;
            if (guidanceText != null) guidanceText.text = string.Empty;
            if (transientText != null) transientText.text = string.Empty;
            // Diagnostics toggled OFF during Running leaves its GO active behind
            // the disabled canvas — it must not render behind the popup.
            if (diagnosticsText != null && diagnosticsText.gameObject.activeSelf)
                diagnosticsText.gameObject.SetActive(false);
            return;
        }

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
            // Identity FIRST, progress in parens. The raw CSV trial number is
            // what every other surface shows (walk_event rows, trial_skip/redo
            // details, the flow transients, console, heartbeat) — a 1-based
            // "position" here once made the HUD the only off-by-one surface,
            // which is exactly how a lab-notebook trial note ends up pointing
            // at the wrong CSV row. PositionOf is gap-correct.
            int raw = _sequencer.CurrentTrialIndex;
            _sb.Append("  ·  Trial ").Append(raw)
               .Append(" (").Append(Mathf.Clamp(_loader.PositionOf(raw), 1, _loader.TrialCount))
               .Append('/').Append(_loader.TrialCount).Append(')');
        }

        _sb.Append("  ·  ").Append(FormatElapsed(Time.realtimeSinceStartup));

        if (_flow != null && (_flow.RedoCount > 0 || _flow.SkipCount > 0))
        {
            _sb.Append("  ·  redo ").Append(_flow.RedoCount)
               .Append(" · skip ").Append(_flow.SkipCount);
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

    private static string FormatElapsed(float seconds)
    {
        int m = Mathf.FloorToInt(seconds / 60f);
        return m < 60 ? $"{m}m" : $"{m / 60}h{m % 60:00}m";
    }

    private string BuildGuidance(SessionPhase phase)
    {
        _sb.Clear();
        switch (phase)
        {
            case SessionPhase.Setup:
                // Trial-CSV state first: a bad file used to present as
                // "placement isn't working" (Ready never comes without data).
                if (_loader != null && _loader.MissingData)
                {
                    _sb.Append("<color=").Append(ExperimentPalette.BadHex)
                       .Append("><b>TRIAL CSV MISSING/INVALID</b></color>\n")
                       .Append("Push trial_conditions.csv (see OperatorQuickstart) — trials cannot start without it.\n\n");
                }
                else if (_loader != null && _loader.DataWarning != null)
                {
                    _sb.Append("<color=").Append(ExperimentPalette.BadHex).Append('>')
                       .Append(_loader.DataWarning).Append("</color>\n");
                }
                if (_placement == null)
                {
                    _sb.Append("Placement system missing.");
                }
                else if (_placement.IsCaptureRequested)
                {
                    // Lead with WHY the capture isn't committing (too far /
                    // moving too fast / still collecting) — field test: the
                    // distance+stability gating was invisible and confusing.
                    if (_placement.SecondsSinceLastTag > 1.5f)
                    {
                        _sb.Append("<color=").Append(ExperimentPalette.MidHex)
                           .Append(">Tag lost — look back at the tag.</color>");
                    }
                    else
                    {
                        _sb.Append("<b>").Append(_placement.PlacementGateStatus).Append("</b>");
                    }
                    _sb.Append('\n')
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
                    // Tag in view but pre-hold: surface a distance warning early
                    // so the operator repositions BEFORE starting the hold.
                    string gate = _placement.PlacementGateStatus;
                    if (gate.StartsWith("Too far"))
                    {
                        _sb.Append("<color=").Append(ExperimentPalette.MidHex).Append('>')
                           .Append(gate).Append("</color>\nThen HOLD the LEFT trigger to place the obstacle.");
                    }
                    else
                    {
                        _sb.Append("Tag visible ✓\n<b>HOLD the LEFT trigger</b> to place the obstacle.");
                    }
                }
                break;

            case SessionPhase.Ready:
                _sb.Append("Placed ✓  Fine-tune with the thumbsticks (L grip = mm steps).\n")
                   .Append("<b>HOLD BOTH triggers</b> to start trials.\n")
                   .Append("<size=80%>R-grip + L trigger: re-place · R-grip + Start: change condition · R-grip + X: pick start trial</size>");
                break;

            case SessionPhase.Running:
                // Only visible when diagnostics forced the HUD on, or waiting for clearance.
                _sb.Append(_flow != null && _flow.WaitingForRedoClearance
                    ? "Redo pending — walk clear of the obstacle."
                    : "Trials running.");
                break;

            case SessionPhase.Paused:
                _sb.Append("<b>PAUSED</b>\nPress Start to resume · HOLD the RIGHT trigger to redo this trial.\n")
                   .Append("<size=80%>R-grip + X: jump to another trial</size>");
                break;

            case SessionPhase.Complete:
                _sb.Append("<b>All trials complete.</b>");
                if (_loader != null && !_loader.MissingData)
                {
                    _sb.Append("  ").Append(_loader.TrialCount).Append(" trials");
                    if (_flow != null && (_flow.RedoCount > 0 || _flow.SkipCount > 0))
                        _sb.Append(" · ").Append(_flow.RedoCount).Append(" redos · ")
                           .Append(_flow.SkipCount).Append(" skips");
                    _sb.Append('.');
                }
                _sb.Append("\nPlease remove the headset.\n")
                   .Append("<size=80%>Ended early or need another walk? HOLD BOTH triggers to reopen (paused).</size>");
                break;
        }

        // Participant identity stays visible until trials start — a stale
        // participant.txt mis-attributes an entire session unrecoverably, and
        // this line is the only place the operator can catch it.
        if ((phase == SessionPhase.Setup || phase == SessionPhase.Ready) && SessionLogger.Instance != null)
        {
            _sb.Append("\n<size=80%>Participant: <b>").Append(SessionLogger.Instance.ParticipantId)
               .Append("</b> (from ").Append(SessionLogger.Instance.ParticipantSource).Append(")</size>");
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

        // Picker readout sits between hold progress and transients: visible while
        // the picker is open, but the commit hold still wins (its label carries
        // the candidate).
        if (_pickerText != null && Time.time < _pickerExpiry)
        {
            _sb.Append(_pickerText);
            return _sb.ToString();
        }
        _pickerText = null;

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
        if (_stereoScanner != null)
        {
            // "rot solver", not "solver" — the status line's Solver is the
            // placement TagSolverMode, a different axis.
            _sb.Append("\nrot solver: ").Append(_stereoScanner.Solver)
               .Append(" · tag ").Append(_stereoScanner.TagSizeMeters.ToString("F3")).Append(" m");
        }
        if (SessionLogger.Instance != null)
        {
            var log = SessionLogger.Instance;
            // WriterHealthy, not IsRunning: IsRunning goes true before the
            // writer touches the disk, so it can read "running" all session
            // while nothing is written.
            string state = !log.IsRunning
                ? "<color=" + ExperimentPalette.BadHex + ">STOPPED</color>"
                : log.WriterHealthy
                    ? "running"
                    : "<color=" + ExperimentPalette.BadHex + ">WRITE FAILING</color>";
            _sb.Append("\nlog: ").Append(state)
               .Append(" · ").Append(log.WrittenCount).Append('/')
               .Append(log.EnqueuedCount).Append(" rows");
        }
        return _sb.ToString();
    }
}
