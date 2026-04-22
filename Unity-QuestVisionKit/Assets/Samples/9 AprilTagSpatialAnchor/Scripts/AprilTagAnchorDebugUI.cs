using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// World-space debug panel for AprilTagAnchorManager. Shows per-tag distance,
/// buffer fill, pose spreads, and state (Idle/TooFar/Gating/Anchored) plus
/// threshold readouts and reset buttons.
///
/// Without this UI the gate thresholds are near-impossible to tune on device:
/// a non-commit can be "too far", "too shaky", or "detection lost" and the
/// user has no way to tell which.
/// </summary>
public class AprilTagAnchorDebugUI : MonoBehaviour
{
    [SerializeField] private AprilTagAnchorManager anchorManager;
    [SerializeField] private TextMeshProUGUI statusText;
    [SerializeField] private Button removeAllAnchorsButton;
    [SerializeField] private Button resetGatesButton;

    [Tooltip("Update cadence in seconds. UI refresh does not need to match frame rate.")]
    [SerializeField] private float refreshInterval = 0.1f;

    private float _nextRefresh;
    private readonly StringBuilder _sb = new();

    private void Awake()
    {
        if (!anchorManager) anchorManager = FindAnyObjectByType<AprilTagAnchorManager>(FindObjectsInactive.Include);
    }

    private void OnEnable()
    {
        if (removeAllAnchorsButton) removeAllAnchorsButton.onClick.AddListener(OnRemoveAllClicked);
        if (resetGatesButton) resetGatesButton.onClick.AddListener(OnResetGatesClicked);
    }

    private void OnDisable()
    {
        if (removeAllAnchorsButton) removeAllAnchorsButton.onClick.RemoveListener(OnRemoveAllClicked);
        if (resetGatesButton) resetGatesButton.onClick.RemoveListener(OnResetGatesClicked);
    }

    private void Update()
    {
        if (Time.time < _nextRefresh) return;
        _nextRefresh = Time.time + refreshInterval;
        Refresh();
    }

    private void Refresh()
    {
        if (!statusText || !anchorManager) return;

        _sb.Clear();
        _sb.Append("<b>AprilTag Spatial Anchors</b>\n");
        _sb.AppendFormat(
            "Thresholds: ≤{0:0.00} m · ≤{1:0} mm spread · ≤{2:0.0}° spread\n",
            anchorManager.MaxAnchorCommitDistanceMeters,
            anchorManager.MaxPositionSpreadMeters * 1000f,
            anchorManager.MaxRotationSpreadDegrees);
        _sb.AppendFormat("Active anchors: {0}\n\n", anchorManager.ActiveAnchors.Count);

        var any = false;
        foreach (var id in anchorManager.KnownTagIds)
        {
            any = true;
            if (!anchorManager.TryGetGateState(
                    id, out var samples, out var target,
                    out var posSpread, out var rotSpread,
                    out var distance, out var state))
            {
                continue;
            }

            _sb.AppendFormat(
                "Tag {0}: {1:0.00} m · {2}/{3} · pos {4:0.0} mm · rot {5:0.0}° · [{6}]\n",
                id, distance, samples, target,
                posSpread * 1000f, rotSpread, state);
        }

        if (!any) _sb.Append("(no tags seen yet)\n");

        statusText.text = _sb.ToString();
    }

    private void OnRemoveAllClicked()
    {
        if (anchorManager) anchorManager.RemoveAllAnchors();
    }

    private void OnResetGatesClicked()
    {
        if (anchorManager) anchorManager.ResetAllGates();
    }
}
