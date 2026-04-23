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

    // Silent-failure tracking
    private float _spawnTime;
    private bool _stateInit;
    private bool _prevCreated;
    private bool _prevLocalized;
    private bool _prevTracked;
    private bool _hadAnchor;
    private bool _warnedUnlocalized;
    private bool _warnedAnchorDestroyed;
    private bool _warnedNotCreated;

    [Tooltip("If the anchor hasn't localized within this many seconds, log a warning.")]
    [SerializeField] private float localizationTimeoutSeconds = 5f;

    [Tooltip("If the anchor hasn't reported Created=true within this many seconds, log a warning.")]
    [SerializeField] private float creationTimeoutSeconds = 2f;

    /// <summary>
    /// The AprilTag ID this anchor was committed for. -1 until SetTagId has run.
    /// </summary>
    public int TagId => _tagId;

    /// <summary>
    /// The OVRSpatialAnchor locking this content to the world.
    /// Use <c>Anchor.Uuid</c> to key persistence or correlate across sessions.
    /// Searches parents too, so the controller can sit on any GameObject in
    /// the anchored prefab's hierarchy.
    /// </summary>
    public OVRSpatialAnchor Anchor
    {
        get
        {
            if (!_anchor) _anchor = GetComponentInParent<OVRSpatialAnchor>(true);
            return _anchor;
        }
    }

    public void SetTagId(int tagId)
    {
        _tagId = tagId;
        if (label)
        {
            label.text = $"Tag {tagId}";
            if (verboseDiagnostics)
            {
                Debug.Log($"[AnchoredContent] SetTagId({tagId}) -> label.text='{label.text}' (label={label.GetType().Name} on '{label.gameObject.name}')");
            }
        }
        else if (verboseDiagnostics)
        {
            Debug.LogWarning($"[AnchoredContent] SetTagId({tagId}) but label field is null — wire a TMP_Text into the Label slot on the prefab's AnchoredContentController.");
        }
    }

    private void Awake()
    {
        _anchor = GetComponentInParent<OVRSpatialAnchor>(true);
        _hadAnchor = _anchor != null;
        _spawnTime = Time.time;
    }

    private void OnEnable()
    {
        if (verboseDiagnostics)
        {
            Debug.Log($"[AnchoredContent] OnEnable tag={_tagId} pos={transform.position} anchorPresent={_hadAnchor}");
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
        if (!verboseDiagnostics) return;

        if (!_anchor) _anchor = GetComponentInParent<OVRSpatialAnchor>(true);
        var anchorPresent = _anchor != null;
        var created = anchorPresent && _anchor.Created;
        var localized = anchorPresent && _anchor.Localized;
        var tracked = anchorPresent && _anchor.IsTracked;

        // State-transition logs (one line per edge, not per tick)
        if (!_stateInit)
        {
            _stateInit = true;
            _prevCreated = created;
            _prevLocalized = localized;
            _prevTracked = tracked;
        }
        else
        {
            if (created != _prevCreated)
            {
                Debug.Log($"[AnchoredContent] tag={_tagId} Created: {_prevCreated} -> {created} at t+{Time.time - _spawnTime:F2}s");
                _prevCreated = created;
            }
            if (localized != _prevLocalized)
            {
                Debug.Log($"[AnchoredContent] tag={_tagId} Localized: {_prevLocalized} -> {localized} at t+{Time.time - _spawnTime:F2}s");
                _prevLocalized = localized;
            }
            if (tracked != _prevTracked)
            {
                Debug.Log($"[AnchoredContent] tag={_tagId} IsTracked: {_prevTracked} -> {tracked} at t+{Time.time - _spawnTime:F2}s");
                _prevTracked = tracked;
            }
        }

        // Silent-failure warnings (one-shot)
        var elapsed = Time.time - _spawnTime;

        if (!_warnedAnchorDestroyed && _hadAnchor && !anchorPresent)
        {
            Debug.LogWarning($"[AnchoredContent] tag={_tagId} OVRSpatialAnchor component vanished after t+{elapsed:F2}s. Likely CreateSpatialAnchor failed and Meta SDK called Destroy(this) on the component.");
            _warnedAnchorDestroyed = true;
        }

        if (!_warnedNotCreated && anchorPresent && !created && elapsed > creationTimeoutSeconds)
        {
            Debug.LogWarning($"[AnchoredContent] tag={_tagId} OVRSpatialAnchor present but Created=false after {creationTimeoutSeconds:F1}s. Native CreateSpatialAnchor may be stuck or failing.");
            _warnedNotCreated = true;
        }

        if (!_warnedUnlocalized && created && !localized && elapsed > localizationTimeoutSeconds)
        {
            Debug.LogWarning($"[AnchoredContent] tag={_tagId} anchor Created but NOT Localized after {localizationTimeoutSeconds:F1}s. SLAM map may lack features at this location; transform will stay at its instantiated pose.");
            _warnedUnlocalized = true;
        }

        // Periodic full tick (unchanged)
        if (Time.time >= _nextDiag)
        {
            _nextDiag = Time.time + diagnosticsInterval;
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
