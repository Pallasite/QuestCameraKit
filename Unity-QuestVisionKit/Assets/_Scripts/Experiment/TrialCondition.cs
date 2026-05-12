using System;

/// <summary>
/// Serializable data class representing a single trial's parameters.
/// Loaded from CSV by <see cref="TrialLoader"/> and consumed by
/// <see cref="ObstacleController"/> to configure per-trial obstacle behavior.
/// </summary>
[Serializable]
public class TrialCondition
{
    /// <summary>Row index from the CSV (column 0).</summary>
    public int TrialNumber;

    /// <summary>Does the obstacle perturb during this trial?</summary>
    public bool IsActive;

    /// <summary>Direction of perturbation: true = toward user, false = away.</summary>
    public bool MoveTowardsUser;

    /// <summary>Proximity trigger radius in meters (XZ plane).</summary>
    public float TriggerDistance;

    /// <summary>Distance the obstacle moves on trigger, in meters.</summary>
    public float PerturbationDistance;

    public override string ToString()
    {
        return $"Trial {TrialNumber}: active={IsActive}, towards={MoveTowardsUser}, " +
               $"trigger={TriggerDistance:F2}m, perturbation={PerturbationDistance:F2}m";
    }
}
