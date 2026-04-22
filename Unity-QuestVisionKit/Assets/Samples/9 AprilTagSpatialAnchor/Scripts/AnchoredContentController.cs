using TMPro;
using UnityEngine;

/// <summary>
/// Attached to the anchored content prefab. Labels the anchor with its tag ID
/// and optionally billboards the label toward the main camera.
/// </summary>
public class AnchoredContentController : MonoBehaviour
{
    [SerializeField] private TMP_Text label;

    [Tooltip("Keep the label facing the user. Applies only to the label transform.")]
    [SerializeField] private bool billboardLabel = true;

    [Tooltip("Log OnEnable/OnDisable/OnDestroy + periodic position and anchor state. " +
             "Leave on until the 'vanishing anchor' bug is understood.")]
    [SerializeField] private bool verboseDiagnostics = true;

    [Tooltip("How often to log the anchor's world pose and tracking state (seconds).")]
    [SerializeField] private float diagnosticsInterval = 0.5f;

    private Transform _cam;
    private int _tagId = -1;
    private OVRSpatialAnchor _anchor;
    private float _nextDiag;

    public void SetTagId(int tagId)
    {
        _tagId = tagId;
        if (label) label.text = $"Tag {tagId}";
    }

    private void Awake()
    {
        _anchor = GetComponent<OVRSpatialAnchor>();
    }

    private void OnEnable()
    {
        if (verboseDiagnostics)
        {
            Debug.Log($"[AnchoredContent] OnEnable tag={_tagId} pos={transform.position}");
        }
    }

    private void OnDisable()
    {
        if (verboseDiagnostics)
        {
            Debug.Log($"[AnchoredContent] OnDisable tag={_tagId} pos={transform.position}\n{System.Environment.StackTrace}");
        }
    }

    private void OnDestroy()
    {
        if (verboseDiagnostics)
        {
            Debug.Log($"[AnchoredContent] OnDestroy tag={_tagId} pos={transform.position}\n{System.Environment.StackTrace}");
        }
    }

    private void Update()
    {
        if (verboseDiagnostics && Time.time >= _nextDiag)
        {
            _nextDiag = Time.time + diagnosticsInterval;
            if (!_anchor) _anchor = GetComponent<OVRSpatialAnchor>();
            var created = _anchor ? _anchor.Created : false;
            var localized = _anchor ? _anchor.Localized : false;
            var tracked = _anchor ? _anchor.IsTracked : false;
            Debug.Log($"[AnchoredContent] tick tag={_tagId} pos={transform.position} created={created} localized={localized} tracked={tracked} rendererCount={GetComponentsInChildren<Renderer>(true).Length}");
        }
    }

    private void LateUpdate()
    {
        if (!billboardLabel || !label) return;
        if (!_cam)
        {
            if (!Camera.main) return;
            _cam = Camera.main.transform;
        }

        var forward = label.transform.position - _cam.position;
        if (forward.sqrMagnitude < 1e-6f) return;
        label.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
    }
}
