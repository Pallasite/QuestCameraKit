using UnityEngine;

/// <summary>
/// Scaffold for the N-tag constellation rung of the tag-geometry ladder.
/// Out of scope for the single/double-tag scene — this pass only wires the
/// interface so the constellation solver can be dropped in later (adapting the
/// existing <see cref="ConstellationDriftCorrector"/> RANSAC + Kabsch pipeline)
/// without touching <see cref="ObstaclePlacementController"/>.
///
/// Currently a no-op: it never reports a pose, and warns once if selected.
/// </summary>
public sealed class ConstellationSolver : ITagPlacementSolver
{
    public string SourceLabel => "apriltag";
    public int MinTags => 3;
    public string GateStatus => "Constellation solver not implemented";

    private bool _warned;

    public void Reset() { _warned = false; }

    public bool TryGetPose(AprilTagDisplayManager.TagWorldPose[] detections, float now, out Pose proposedPose)
    {
        proposedPose = default;
        if (!_warned)
        {
            _warned = true;
            Debug.LogWarning("[ConstellationSolver] Not implemented in this scene (scaffold). " +
                             "Use SingleTag or TwoTagLine; the constellation rung returns later.");
        }
        return false;
    }
}
