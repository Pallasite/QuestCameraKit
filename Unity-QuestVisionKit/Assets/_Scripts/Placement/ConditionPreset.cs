using System;

/// <summary>
/// A named experimental condition: one {solver, policy, variant} bundle the
/// experimenter cycles as a single action (and the CSV logs by name), instead
/// of juggling three independent enums mid-session.
///
/// The preset list lives on <see cref="ObstaclePlacementController"/> as a
/// serialized array — add entries in the Inspector (see OperatorQuickstart.md).
/// The first entry is the boot default.
/// </summary>
[Serializable]
public struct ConditionPreset
{
    public string name;
    public TagSolverMode solver;
    public VisualUpdatePolicy policy;
    public TrackingVariant variant;

    public override string ToString() => $"{name} ({solver}/{policy}/{variant})";
}
