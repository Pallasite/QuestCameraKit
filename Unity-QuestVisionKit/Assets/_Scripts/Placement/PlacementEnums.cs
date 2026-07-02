/// <summary>
/// Shared enums for the simplified single/double-AprilTag obstacle placement.
/// These are plain serialized choices on <see cref="ObstaclePlacementController"/>
/// so the experiment variants can be switched in the Inspector per scene or build.
/// </summary>

/// <summary>How the obstacle's base anchor is held in the world.</summary>
public enum TrackingVariant
{
    /// <summary>Obstacle base is parented under a runtime OVRSpatialAnchor (OS world-lock — best stability).</summary>
    Anchored,

    /// <summary>Obstacle base is parented under a plain tracking-space root (SLAM-only — the backup path).</summary>
    WorldRoot,
}

/// <summary>How tag-derived corrections reach the visible obstacle.</summary>
public enum VisualUpdatePolicy
{
    /// <summary>
    /// Measure during the walk; apply the correction only between trials (after the
    /// participant has passed the obstacle). The visual never moves mid-walk. Default.
    /// </summary>
    Deferred,

    /// <summary>Lerp/low-pass the obstacle toward the tag pose live (reduced jitter).</summary>
    SmoothedLive,

    /// <summary>Snap the obstacle to the tag pose every detection (jitters; debug baseline only).</summary>
    RawLive,
}

/// <summary>Which tag-geometry solver is active (the tag-count ladder).</summary>
public enum TagSolverMode
{
    /// <summary>Single tag drives the obstacle pose directly.</summary>
    SingleTag,

    /// <summary>Two tags: obstacle on the midpoint of the connecting line, facing perpendicular.</summary>
    TwoTagLine,

    /// <summary>N-tag constellation (scaffold only this pass — see <see cref="ConstellationSolver"/>).</summary>
    Constellation,
}

/// <summary>When the obstacle gets placed from the tag.</summary>
public enum PlacementTrigger
{
    /// <summary>Place only on an explicit <c>CapturePlacement()</c> ("place now"). Default — best capture quality.</summary>
    Manual,

    /// <summary>Place automatically as soon as a stable tag pose is seen.</summary>
    AutoOnFirstStable,
}
