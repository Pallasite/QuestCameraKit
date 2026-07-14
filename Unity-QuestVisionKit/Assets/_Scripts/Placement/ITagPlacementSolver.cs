using UnityEngine;

/// <summary>
/// A swappable tag-geometry solver: turns the current frame's world-space
/// AprilTag detections into a proposed obstacle base pose. Implementations form
/// the tag-geometry ladder (single tag -> two-tag line -> constellation), and are
/// selected by <see cref="ObstaclePlacementController"/> via <see cref="TagSolverMode"/>.
///
/// Solvers are plain C# classes (not MonoBehaviours): one is constructed per run
/// from the controller's serialized config, so the variant is Inspector-driven
/// while the geometry stays cleanly separated and unit-testable.
/// </summary>
public interface ITagPlacementSolver
{
    /// <summary>
    /// The <c>correction_source</c> label written to the session log
    /// (e.g. "apriltag_single", "apriltag_pair", "apriltag").
    /// </summary>
    string SourceLabel { get; }

    /// <summary>Minimum simultaneous tags needed to produce a pose.</summary>
    int MinTags { get; }

    /// <summary>
    /// Feed this frame's world-space detections. Returns true and a proposed
    /// obstacle base pose when the solver has a confident (stability-gated)
    /// result this frame; false otherwise.
    /// </summary>
    bool TryGetPose(AprilTagDisplayManager.TagWorldPose[] detections, float now, out Pose proposedPose);

    /// <summary>Clear internal buffers (e.g. on recapture / variant switch).</summary>
    void Reset();

    /// <summary>
    /// One-line, human-readable reason the last <see cref="TryGetPose"/> call
    /// returned false — or "Ready" when it produced a pose. Surfaced on the HUD
    /// guidance zone during Setup so the experimenter can see WHY a capture
    /// isn't committing (too far / moving too fast / still collecting).
    /// </summary>
    string GateStatus { get; }
}
