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
    public string GateStatus { get; private set; } = "Waiting for tag";

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
        if (detections == null || detections.Length == 0)
        {
            GateStatus = "Waiting for tag";
            return false;
        }

        if (!TryPickDetection(detections, out var det))
        {
            GateStatus = _tagId >= 0 ? $"Tag {_tagId} not seen" : "Waiting for tag";
            return false;
        }

        // Feed the gate regardless of distance so the spread builds while the
        // experimenter approaches the tag, but only commit when within range.
        // The rotation is flattened to yaw-only BEFORE gating: the obstacle must
        // stay upright and its forward axis horizontal (the perturbation axis is
        // built from it), and the gate should measure yaw spread, not the tag's
        // pitch/roll noise. Matches TwoTagLineSolver, which is yaw-only by
        // construction.
        _gate.AddObservation(det.Position, FlattenToYaw(det.Rotation), now);

        var camPos = CameraPosition();
        if (camPos.HasValue)
        {
            float dist = Vector3.Distance(camPos.Value, det.Position);
            if (dist > _maxDistanceMeters)
            {
                GateStatus = $"Too far — step within {_maxDistanceMeters:0.0} m of the tag (now {dist:0.0} m)";
                return false;
            }
        }

        if (_gate.IsStable(out proposedPose))
        {
            GateStatus = "Ready";
            return true;
        }

        GateStatus = DescribeGateMiss();
        return false;
    }

    // Why didn't the full buffer pass? Position spread = the head (and with it
    // the triangulation) is translating; rotation spread = turning. Both read
    // to the operator as "moving too fast".
    private string DescribeGateMiss()
    {
        if (_gate.SampleCount < _gate.WindowSize)
            return $"Capturing {_gate.SampleCount}/{_gate.WindowSize} — hold steady";
        if (_gate.LastPositionSpread > _gate.MaxPositionSpreadMeters)
            return $"Moving too fast — hold still ({_gate.LastPositionSpread * 1000f:0.0} mm spread)";
        if (_gate.LastRotationSpread > _gate.MaxRotationSpreadDegrees)
            return $"Turning too fast — hold still ({_gate.LastRotationSpread:0.0}° spread)";
        return "Stabilizing…";
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

    /// <summary>
    /// Reduces a detected tag rotation to a yaw-only (upright) rotation.
    ///
    /// Mounting convention: the tag lies FLAT on the ground with its printed
    /// "top" pointing along the intended walking direction. Tag basis (see
    /// StereoAprilTagScanner corner ordering): +X = right edge, +Y = printed
    /// top, +Z = face normal. Flat tag → the normal is vertical, so the
    /// walking direction is the in-plane top axis (+Y). Wall-mounted tag →
    /// the normal itself faces the walkway, so use +Z.
    /// </summary>
    internal static Quaternion FlattenToYaw(Quaternion tagRotation)
    {
        var normal = tagRotation * Vector3.forward;

        // Tag lying flat (normal within ~30 degrees of vertical): forward is
        // the printed-top axis. Otherwise (wall-mounted): forward is the
        // normal. Both projected onto the horizontal plane.
        var forward = Mathf.Abs(normal.y) > 0.866f
            ? tagRotation * Vector3.up
            : normal;
        forward.y = 0f;

        if (forward.sqrMagnitude < 1e-6f) return Quaternion.identity;
        return Quaternion.LookRotation(forward.normalized, Vector3.up);
    }
}
