using UnityEngine;

/// <summary>
/// Single-tag placement: the obstacle base pose is the detected world pose of one
/// AprilTag, gated for distance (AprilTag pose error is ~1% of range, so a 1 m
/// commit distance gives roughly sub-1 cm precision) and stability (a tight pose
/// spread over a sliding window). Reuses <see cref="AprilTagPoseStabilityGate"/> —
/// the same gate <see cref="AprilTagAnchorManager"/> uses for one-shot anchor commits.
/// </summary>
public sealed class SingleTagSolver : ITagPlacementSolver
{
    public string SourceLabel => "apriltag_single";
    public int MinTags => 1;

    private readonly int _tagId;               // -1 = accept the nearest detected tag
    private readonly float _maxDistanceMeters;
    private readonly AprilTagPoseStabilityGate _gate;
    private Transform _cameraRef;

    public SingleTagSolver(int tagId, float maxDistanceMeters,
                           AprilTagPoseStabilityGate gate, Transform cameraRef = null)
    {
        _tagId = tagId;
        _maxDistanceMeters = Mathf.Max(0.01f, maxDistanceMeters);
        _gate = gate;
        _cameraRef = cameraRef;
    }

    public void Reset() => _gate.Clear();

    public bool TryGetPose(AprilTagDisplayManager.TagWorldPose[] detections, float now, out Pose proposedPose)
    {
        proposedPose = default;
        if (detections == null || detections.Length == 0) return false;

        if (!TryPickDetection(detections, out var det)) return false;

        // Feed the gate regardless of distance so the spread builds while the
        // experimenter approaches the tag, but only commit when within range.
        _gate.AddObservation(det.Position, det.Rotation, now);

        var camPos = CameraPosition();
        if (camPos.HasValue && Vector3.Distance(camPos.Value, det.Position) > _maxDistanceMeters)
            return false;

        return _gate.IsStable(out proposedPose);
    }

    private bool TryPickDetection(AprilTagDisplayManager.TagWorldPose[] detections,
                                  out AprilTagDisplayManager.TagWorldPose picked)
    {
        picked = default;

        if (_tagId >= 0)
        {
            foreach (var d in detections)
                if (d.TagId == _tagId) { picked = d; return true; }
            return false;
        }

        // tagId < 0: nearest detection to the camera (or the first one if no camera).
        var camPos = CameraPosition();
        bool found = false;
        float best = float.MaxValue;
        foreach (var d in detections)
        {
            if (!camPos.HasValue) { picked = d; return true; }
            float dist = Vector3.Distance(camPos.Value, d.Position);
            if (!found || dist < best) { best = dist; picked = d; found = true; }
        }
        return found;
    }

    private Vector3? CameraPosition()
    {
        if (!_cameraRef && Camera.main) _cameraRef = Camera.main.transform;
        return _cameraRef ? _cameraRef.position : (Vector3?)null;
    }
}
