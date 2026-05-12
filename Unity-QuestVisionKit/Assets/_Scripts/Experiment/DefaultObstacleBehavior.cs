using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Default obstacle behavior: instant position change along the obstacle's
/// forward axis. Used when no custom <see cref="IObstacleBehavior"/> is
/// attached to the obstacle.
///
/// This operates on the <c>perturbationPivot</c> transform, NOT the
/// obstacle root, so the experimenter's finesse offset is preserved
/// across trial resets.
/// </summary>
public class DefaultObstacleBehavior : MonoBehaviour, IObstacleBehavior
{
    public void Move(Transform obstacle, Transform player, float distance, bool towardsUser)
    {
        // Determine if the player is in front or behind the object on the z-axis
        float direction = (player.position.z > obstacle.position.z) ? -1f : 1f;

        // Default is to move away from user; invert if towardsUser is true
        if (towardsUser)
        {
            direction = -direction;
        }

        // Move along the obstacle's forward direction
        Vector3 forward = obstacle.TransformDirection(Vector3.forward);
        obstacle.position += forward * (distance * direction);
    }

    public void Reset(Transform obstacle)
    {
        // Stop any physics on child rigidbodies
        List<Rigidbody> childPhysics = new List<Rigidbody>(obstacle.GetComponentsInChildren<Rigidbody>());
        foreach (var rb in childPhysics)
        {
            rb.angularVelocity = Vector3.zero;
            rb.linearVelocity = Vector3.zero;
        }

        // Reset to local origin (perturbationPivot → zero offset from obstacle root)
        obstacle.localPosition = Vector3.zero;
    }
}
