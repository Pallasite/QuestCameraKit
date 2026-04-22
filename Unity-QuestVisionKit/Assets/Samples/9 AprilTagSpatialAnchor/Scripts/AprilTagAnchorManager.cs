using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Commits an OVRSpatialAnchor at the detected pose of each allowed AprilTag.
///
/// Ported from the Meta XR SDK "KinesResearch" AnchorManager pattern
/// (AddComponent&lt;OVRSpatialAnchor&gt;() on a GameObject pre-positioned at the
/// desired world pose). Extended with a two-stage gate:
///
///   1. Distance gate: only commit when the camera is within
///      MaxAnchorCommitDistanceMeters of the tag. AprilTag pose error scales
///      ~linearly with distance; 1.0 m gives roughly sub-1 cm precision for the
///      default tag size / camera resolution here.
///
///   2. Stability gate: buffer the last N per-tag detections and require the
///      position / rotation spread to be tight before committing. Averages the
///      buffered poses once at commit time; does NOT keep updating the anchor.
///
/// Detection and visualization are NOT owned by this component. It subscribes
/// to AprilTagDisplayManager.OnTagsDetected, which continues to drive the
/// wireframe visualizer in parallel at every range — so the user keeps seeing
/// live per-frame detections even after anchors are committed.
///
/// Phase 1 scope:
///   - No persistence across app sessions (no Save/Load of anchor UUIDs).
///   - One-shot commit per tag by default; use ResetTag() or RemoveAnchor() to
///     recapture.
///   - No drift correction. The existing (disabled) SpatialAnchorDriftCorrector
///     in sample 8 is the intended Phase 2 layer on top of this one.
/// </summary>
public class AprilTagAnchorManager : MonoBehaviour
{
    public enum TagState
    {
        Idle,      // no recent observations
        TooFar,    // observed but beyond the distance gate
        Gating,    // within distance, accumulating buffer
        Anchored,  // anchor committed
    }

    [SerializeField] private AprilTagDisplayManager displayManager;
    [SerializeField] private GameObject anchoredContentPrefab;

    [Tooltip("Camera anchor used to measure distance-to-tag. If null, falls back to Camera.main.")]
    [SerializeField] private Transform cameraAnchor;

    [Header("Which tags to anchor")]
    [Tooltip("Empty = any detected tag is anchored. Non-empty = only listed tag IDs.")]
    [SerializeField] private int[] allowedTagIds = Array.Empty<int>();

    [Header("Distance gate (sub-1cm precision)")]
    [Tooltip("Only commit an anchor when the camera is within this distance of the tag. " +
             "AprilTag pose error is approximately 1% of distance for the default tag size " +
             "and camera resolution, so 1.0 m yields a ~1 cm precision ceiling.")]
    [SerializeField] private float maxAnchorCommitDistanceMeters = 1.0f;

    [Header("Stability gate")]
    [SerializeField] private int windowSize = 10;

    [Tooltip("Max pose jitter across the window. 0.005 m (5 mm) gives sub-1 cm precision headroom.")]
    [SerializeField] private float maxPositionSpreadMeters = 0.005f;

    [SerializeField] private float maxRotationSpreadDegrees = 2f;
    [SerializeField] private float maxObservationAgeSeconds = 0.5f;

    [Header("Behavior")]
    [Tooltip("Once committed, ignore further detections of the same tag ID.")]
    [SerializeField] private bool oneShotPerTag = true;

    private readonly Dictionary<int, AprilTagPoseStabilityGate> _gates = new();
    private readonly Dictionary<int, OVRSpatialAnchor> _anchors = new();
    private readonly Dictionary<int, float> _lastDistance = new();
    private readonly Dictionary<int, float> _lastObservationTime = new();

    public IReadOnlyDictionary<int, OVRSpatialAnchor> ActiveAnchors => _anchors;

    public event Action<int, OVRSpatialAnchor> OnAnchorCommitted;
    public event Action<int> OnAnchorRemoved;

    public bool HasAnchor(int tagId) => _anchors.ContainsKey(tagId);

    public bool TryGetGateState(int tagId,
                                out int samples,
                                out int targetWindowSize,
                                out float posSpreadMeters,
                                out float rotSpreadDegrees,
                                out float distanceToTagMeters,
                                out TagState state)
    {
        targetWindowSize = windowSize;
        samples = 0;
        posSpreadMeters = 0f;
        rotSpreadDegrees = 0f;
        distanceToTagMeters = 0f;
        state = TagState.Idle;

        if (_anchors.ContainsKey(tagId))
        {
            state = TagState.Anchored;
            if (_lastDistance.TryGetValue(tagId, out var dd)) distanceToTagMeters = dd;
            return true;
        }

        if (!_gates.TryGetValue(tagId, out var gate)) return false;

        samples = gate.SampleCount;
        posSpreadMeters = gate.LastPositionSpread;
        rotSpreadDegrees = gate.LastRotationSpread;
        _lastDistance.TryGetValue(tagId, out distanceToTagMeters);

        if (_lastObservationTime.TryGetValue(tagId, out var lastT)
            && Time.time - lastT > maxObservationAgeSeconds)
        {
            state = TagState.Idle;
        }
        else if (distanceToTagMeters > maxAnchorCommitDistanceMeters)
        {
            state = TagState.TooFar;
        }
        else
        {
            state = TagState.Gating;
        }
        return true;
    }

    public IEnumerable<int> KnownTagIds
    {
        get
        {
            var seen = new HashSet<int>(_anchors.Keys);
            seen.UnionWith(_gates.Keys);
            return seen;
        }
    }

    public void RemoveAnchor(int tagId)
    {
        if (!_anchors.TryGetValue(tagId, out var anchor)) return;
        _anchors.Remove(tagId);

        if (anchor != null)
        {
            Destroy(anchor.gameObject);
        }
        OnAnchorRemoved?.Invoke(tagId);
    }

    public void RemoveAllAnchors()
    {
        var ids = new List<int>(_anchors.Keys);
        foreach (var id in ids) RemoveAnchor(id);
    }

    public void ResetTag(int tagId)
    {
        RemoveAnchor(tagId);
        if (_gates.TryGetValue(tagId, out var gate)) gate.Clear();
        _lastDistance.Remove(tagId);
        _lastObservationTime.Remove(tagId);
    }

    public void ResetAllGates()
    {
        foreach (var g in _gates.Values) g.Clear();
        _lastDistance.Clear();
        _lastObservationTime.Clear();
    }

    public float MaxAnchorCommitDistanceMeters => maxAnchorCommitDistanceMeters;
    public float MaxPositionSpreadMeters => maxPositionSpreadMeters;
    public float MaxRotationSpreadDegrees => maxRotationSpreadDegrees;

    private void Awake()
    {
        if (!displayManager)
        {
            displayManager = GetComponent<AprilTagDisplayManager>();
        }
    }

    private void OnEnable()
    {
        if (!displayManager)
        {
            Debug.LogError("[AprilTagAnchorManager] No AprilTagDisplayManager assigned.");
            enabled = false;
            return;
        }
        if (!anchoredContentPrefab)
        {
            Debug.LogError("[AprilTagAnchorManager] No anchored-content prefab assigned.");
            enabled = false;
            return;
        }

        displayManager.OnTagsDetected += HandleTagsDetected;
    }

    private void OnDisable()
    {
        if (displayManager)
        {
            displayManager.OnTagsDetected -= HandleTagsDetected;
        }
    }

    private void HandleTagsDetected(AprilTagDisplayManager.TagWorldPose[] poses)
    {
        if (poses == null || poses.Length == 0) return;

        var camTf = cameraAnchor ? cameraAnchor : (Camera.main ? Camera.main.transform : null);
        if (!camTf)
        {
            Debug.LogWarning("[AprilTagAnchorManager] No camera reference; skipping.");
            return;
        }
        var camPos = camTf.position;
        var now = Time.time;

        foreach (var p in poses)
        {
            if (allowedTagIds != null && allowedTagIds.Length > 0
                && Array.IndexOf(allowedTagIds, p.TagId) < 0)
            {
                continue;
            }

            if (oneShotPerTag && _anchors.ContainsKey(p.TagId)) continue;

            if (!_gates.TryGetValue(p.TagId, out var gate))
            {
                gate = new AprilTagPoseStabilityGate
                {
                    WindowSize = windowSize,
                    MaxPositionSpreadMeters = maxPositionSpreadMeters,
                    MaxRotationSpreadDegrees = maxRotationSpreadDegrees,
                    MaxObservationAgeSeconds = maxObservationAgeSeconds
                };
                _gates[p.TagId] = gate;
            }

            var distance = Vector3.Distance(camPos, p.Position);
            _lastDistance[p.TagId] = distance;
            _lastObservationTime[p.TagId] = now;

            // Feed the buffer regardless of distance so the debug UI shows
            // gating progress at long range; commit is still blocked below.
            gate.AddObservation(p.Position, p.Rotation, now);

            if (distance > maxAnchorCommitDistanceMeters) continue;

            if (gate.IsStable(out var stablePose))
            {
                CommitAnchor(p.TagId, stablePose);
            }
        }
    }

    private void CommitAnchor(int tagId, Pose pose)
    {
        var go = Instantiate(anchoredContentPrefab, pose.position, pose.rotation);
        go.name = $"Anchor_Tag{tagId}";

        var anchor = go.AddComponent<OVRSpatialAnchor>();
        _anchors[tagId] = anchor;

        if (go.TryGetComponent<AnchoredContentController>(out var controller))
        {
            controller.SetTagId(tagId);
        }

        Debug.Log($"[AprilTagAnchorManager] Committed anchor for tag {tagId} at {pose.position}");
        OnAnchorCommitted?.Invoke(tagId, anchor);
    }
}
