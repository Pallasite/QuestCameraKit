using UnityEngine;

/// <summary>
/// Interface for custom obstacle movement and reset behaviors.
/// Implement this on any obstacle that needs non-standard perturbation
/// (e.g., animated dog, physics-driven object).
///
/// The <paramref name="obstacle"/> transform passed to both methods is the
/// <c>perturbationPivot</c> child — NOT the obstacle root. This ensures
/// the experimenter's finesse offset (<see cref="ObstacleFinesseController"/>)
/// is never overwritten by trial resets.
/// </summary>
public interface IObstacleBehavior
{
    /// <summary>
    /// Move the obstacle based on trial parameters.
    /// </summary>
    /// <param name="obstacle">The perturbationPivot transform to move.</param>
    /// <param name="player">The player/camera transform for direction calculation.</param>
    /// <param name="distance">Distance to move in meters (from trial CSV).</param>
    /// <param name="towardsUser">If true, move towards player; if false, move away.</param>
    void Move(Transform obstacle, Transform player, float distance, bool towardsUser);

    /// <summary>
    /// Reset the obstacle to its starting position (localPosition = zero).
    /// </summary>
    /// <param name="obstacle">The perturbationPivot transform to reset.</param>
    void Reset(Transform obstacle);
}
