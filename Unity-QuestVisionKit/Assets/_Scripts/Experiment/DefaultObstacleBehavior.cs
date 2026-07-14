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
        // Perturb along the obstacle's HORIZONTAL forward axis (the placement
        // yaw = the walking direction). The raw local forward previously
        // inherited the AprilTag's full 3D rotation, so a tag lying flat sent
        // the obstacle straight up — or down through the floor.
        Vector3 axis = obstacle.forward;
        axis.y = 0f;
        if (axis.sqrMagnitude < 1e-6f)
        {
            // Degenerate (obstacle facing straight up/down): fall back to the
            // obstacle-to-player line, which is the walkway by definition.
            axis = player.position - obstacle.position;
            axis.y = 0f;
        }
        if (axis.sqrMagnitude < 1e-6f) return;   // no horizontal axis: not moving beats moving vertically
        axis.Normalize();

        // Sign computed along the SAME axis the obstacle moves on. The old
        // code compared world-Z positions while moving along local forward;
        // whenever those disagreed, "towards" and "away" were wrong.
        Vector3 toPlayer = player.position - obstacle.position;
        toPlayer.y = 0f;
        float towardPlayerSign = Mathf.Sign(Vector3.Dot(toPlayer, axis));
        float direction = towardsUser ? towardPlayerSign : -towardPlayerSign;

        obstacle.position += axis * (distance * direction);
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
