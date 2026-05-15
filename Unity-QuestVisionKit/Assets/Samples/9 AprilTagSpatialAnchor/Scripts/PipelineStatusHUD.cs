using System.Text;
using TMPro;
using UnityEngine;

/// <summary>
/// Lightweight world-space HUD that displays the current state of the
/// AprilTag pipeline: calibration status, drift correction activity,
/// and the ObstacleFinesseController's fine/coarse mode.
///
/// Subscribes to events from ConstellationDriftCorrector and polls
/// ObstacleFinesseController each refresh. Attach a TextMeshProUGUI
/// (e.g. on a world-space Canvas near the user's hand) and wire the
/// references in the Inspector.
/// </summary>
public class PipelineStatusHUD : MonoBehaviour
{
    [Header("Wiring")]
    [SerializeField] private ConstellationDriftCorrector corrector;
    [SerializeField] private ObstacleFinesseController finesseController;
    [SerializeField] private TextMeshProUGUI statusText;

    [Header("Display")]
    [Tooltip("How often the HUD refreshes (seconds). Doesn't need to match frame rate.")]
    [SerializeField] private float refreshInterval = 0.15f;

    [Tooltip("How long transient messages (calibration result, correction events) stay visible.")]
    [SerializeField] private float transientDurationSeconds = 3f;

    private float _nextRefresh;
    private readonly StringBuilder _sb = new();

    // Transient message state
    private string _transientMessage;
    private float _transientExpiry;

    // Calibration progress state (written from event, read from Refresh)
    private int _calProgressFrame;
    private int _calProgressTotal;
    private int _calProgressTags;
    private bool _calInProgress;

    // Streaming progress state (written from event, read from Refresh).
    // IsStreamingCalibration on the corrector is the source of truth for whether
    // a sweep is active; these fields just cache the most recent progress numbers.
    private int _streamingTotalObs;
    private int _streamingUniqueTags;

    private void Awake()
    {
        if (!corrector) corrector = FindAnyObjectByType<ConstellationDriftCorrector>(FindObjectsInactive.Include);
        if (!finesseController) finesseController = FindAnyObjectByType<ObstacleFinesseController>(FindObjectsInactive.Include);
    }

    private void OnEnable()
    {
        if (corrector)
        {
            corrector.OnCalibrationProgress += HandleCalibrationProgress;
            corrector.OnStreamingCalibrationProgress += HandleStreamingProgress;
            corrector.OnConstellationCalibrated += HandleCalibrated;
            corrector.OnCalibrationFailed += HandleCalibrationFailed;
            corrector.OnCorrectionTriggered += HandleCorrectionTriggered;
            corrector.OnCorrectionCompleted += HandleCorrectionCompleted;
            corrector.OnCorrectionRejected += HandleCorrectionRejected;
        }
    }

    private void OnDisable()
    {
        if (corrector)
        {
            corrector.OnCalibrationProgress -= HandleCalibrationProgress;
            corrector.OnStreamingCalibrationProgress -= HandleStreamingProgress;
            corrector.OnConstellationCalibrated -= HandleCalibrated;
            corrector.OnCalibrationFailed -= HandleCalibrationFailed;
            corrector.OnCorrectionTriggered -= HandleCorrectionTriggered;
            corrector.OnCorrectionCompleted -= HandleCorrectionCompleted;
            corrector.OnCorrectionRejected -= HandleCorrectionRejected;
        }
    }

    private void Update()
    {
        if (Time.time < _nextRefresh) return;
        _nextRefresh = Time.time + refreshInterval;
        Refresh();
    }

    private void Refresh()
    {
        if (!statusText) return;

        _sb.Clear();

        // --- Calibration state ---
        // Order: streaming sweep wins (it's the most user-visible mode),
        // then batch in-progress, then steady calibrated, else uncalibrated hint.
        if (corrector && corrector.IsStreamingCalibration)
        {
            _sb.AppendFormat("<b>Sweeping...</b> {0} tags / {1} obs ({2:F1}s)",
                _streamingUniqueTags, _streamingTotalObs, corrector.StreamingElapsedSeconds);
            _sb.Append("\ngrip+B commit \u00b7 grip+stick-click cancel\n");
        }
        else if (_calInProgress)
        {
            _sb.AppendFormat("<b>Calibrating...</b> {0}/{1} frames", _calProgressFrame, _calProgressTotal);
            if (_calProgressTags > 0) _sb.AppendFormat(" ({0} tags)", _calProgressTags);
            _sb.Append('\n');
        }
        else if (corrector && corrector.IsCalibrated)
        {
            _sb.AppendFormat("<b>Calibrated</b> ({0} tags)", corrector.ReferenceConstellation.Count);
            var rms = corrector.LastResidualRmsMeters;
            if (rms > 0f) _sb.AppendFormat(" RMS {0:F1}mm", rms * 1000f);
            _sb.Append('\n');
        }
        else
        {
            _sb.Append("<b>Uncalibrated</b> \u2014 grip+A batch \u00b7 grip+B sweep\n");
        }

        // --- Drift correction state ---
        if (corrector && corrector.IsCalibrated)
        {
            var correction = corrector.AppliedCorrection;
            var posMag = correction.position.magnitude * 1000f;
            var rotMag = Quaternion.Angle(Quaternion.identity, correction.rotation);
            if (posMag > 0.01f || rotMag > 0.01f)
            {
                _sb.AppendFormat("Correction: {0:F1}mm / {1:F2}\u00b0\n", posMag, rotMag);
            }
        }

        // --- Finesse mode ---
        if (finesseController && finesseController.enabled)
        {
            _sb.AppendFormat("Mode: <b>{0}</b>\n",
                finesseController.FineMode ? "FINE (mm/0.1\u00b0)" : "COARSE (cm/1\u00b0)");
        }

        // --- Transient message ---
        if (_transientMessage != null && Time.time < _transientExpiry)
        {
            _sb.Append(_transientMessage);
            _sb.Append('\n');
        }
        else
        {
            _transientMessage = null;
        }

        statusText.text = _sb.ToString();
    }

    private void SetTransient(string msg)
    {
        _transientMessage = msg;
        _transientExpiry = Time.time + transientDurationSeconds;
    }

    /// <summary>
    /// Public entry point for other components to push a transient message to
    /// the HUD. Pass <paramref name="durationSeconds"/> &gt; 0 to override the
    /// default transient lifetime (useful for longer, multi-line messages).
    /// </summary>
    public void ShowTransient(string message, float durationSeconds = -1f)
    {
        _transientMessage = message;
        _transientExpiry = Time.time + (durationSeconds > 0f ? durationSeconds : transientDurationSeconds);
    }

    // ---- Event handlers ----

    private void HandleCalibrationProgress(int captured, int total, int tags)
    {
        _calInProgress = true;
        _calProgressFrame = captured;
        _calProgressTotal = total;
        _calProgressTags = tags;
    }

    private void HandleStreamingProgress(int totalObs, int uniqueTags)
    {
        _streamingTotalObs = totalObs;
        _streamingUniqueTags = uniqueTags;
    }

    private void HandleCalibrated()
    {
        _calInProgress = false;
        SetTransient("<color=#88FF88>\u2713 Calibration complete</color>");
    }

    private void HandleCalibrationFailed(string reason)
    {
        _calInProgress = false;
        SetTransient($"<color=#FF8888>\u2717 Calibration failed: {reason}</color>");
    }

    private void HandleCorrectionTriggered(Pose correction)
    {
        var mm = correction.position.magnitude * 1000f;
        SetTransient($"<color=#FFFF88>Correcting drift: {mm:F1}mm</color>");
    }

    private void HandleCorrectionCompleted()
    {
        SetTransient("<color=#88FF88>\u2713 Correction applied</color>");
    }

    private void HandleCorrectionRejected(string reason)
    {
        // Only show rejections briefly — they're frequent and expected.
        _transientMessage = $"<color=#888888>Rejected: {reason}</color>";
        _transientExpiry = Time.time + 1f;
    }
}
