using UnityEngine;

/// <summary>
/// Two-tag placement: the obstacle base sits at the midpoint of the line
/// connecting two AprilTags, with the obstacle's local +Z pointing perpendicular
/// to that baseline in the floor plane (i.e. along the walking direction, not the
/// tag line). Geometry mirrors <see cref="ControllerObstaclePlacer"/>'s
/// midpoint/perpendicular math, but driven by two tags instead of two controllers.
/// The computed midpoint pose is stability-gated before it is reported.
/// </summary>
public sealed class TwoTagLineSolver : ITagPlacementSolver
{
    public string SourceLabel => "apriltag_pair";
    public int MinTags => 2;

    private readonly int _tagIdA;
    private readonly int _tagIdB;
    private readonly float _verticalOffsetMeters;
    private readonly Quaternion _rotationOffset;
    private readonly AprilTagPoseStabilityGate _gate;

    public TwoTagLineSolver(int tagIdA, int tagIdB, float verticalOffsetMeters,
                            Vector3 rotationOffsetEuler, AprilTagPoseStabilityGate gate)
    {
        _tagIdA = tagIdA;
        _tagIdB = tagIdB;
        _verticalOffsetMeters = verticalOffsetMeters;
        _rotationOffset = Quaternion.Euler(rotationOffsetEuler);
        _gate = gate;
    }

    public void Reset() => _gate.Clear();

    public bool TryGetPose(AprilTagDisplayManager.TagWorldPose[] detections, float now, out Pose proposedPose)
    {
        proposedPose = default;
        if (detections == null || detections.Length < 2) return false;

        if (!TryFind(detections, _tagIdA, out var a) || !TryFind(detections, _tagIdB, out var b))
            return false;

        Vector3 midpoint = (a.Position + b.Position) * 0.5f;
        midpoint.y += _verticalOffsetMeters;

        // Cross(baseline, up) lands in the floor plane regardless of any tilt in
        // the baseline's Y, so a tilted tag pair doesn't tilt the obstacle.
        Vector3 baseline = b.Position - a.Position;
        Vector3 forward = Vector3.Cross(baseline, Vector3.up);
        Quaternion rot = forward.sqrMagnitude < 1e-6f
            ? Quaternion.identity     // tags stacked vertically — degenerate baseline
            : Quaternion.LookRotation(forward, Vector3.up);
        rot *= _rotationOffset;

        _gate.AddObservation(midpoint, rot, now);
        return _gate.IsStable(out proposedPose);
    }

    private static bool TryFind(AprilTagDisplayManager.TagWorldPose[] dets, int id,
                                out AprilTagDisplayManager.TagWorldPose hit)
    {
        foreach (var d in dets)
            if (d.TagId == id) { hit = d; return true; }
        hit = default;
        return false;
    }
}
