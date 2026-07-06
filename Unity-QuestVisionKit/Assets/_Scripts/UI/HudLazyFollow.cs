using UnityEngine;

/// <summary>
/// Lazy head-follow for the world-space session HUD: stays put while roughly in
/// view, and glides to a comfortable spot in front of the user only when it
/// drifts too far off-gaze or out of range. Yaw-only billboard (no head-roll).
///
/// Deliberately NOT a rigid head-lock — a hard-locked panel is nauseating and
/// occludes the scene; lazy-follow keeps the HUD findable without being glued
/// to the view.
/// </summary>
[DisallowMultipleComponent]
public sealed class HudLazyFollow : MonoBehaviour
{
    [Tooltip("Comfortable viewing distance the HUD glides to (m).")]
    [SerializeField] private float followDistanceMeters = 1.4f;

    [Tooltip("Reposition when the HUD is more than this many degrees off the gaze direction.")]
    [SerializeField] private float maxOffGazeDegrees = 30f;

    [Tooltip("Reposition when the HUD is closer than this (m).")]
    [SerializeField] private float minDistanceMeters = 0.8f;

    [Tooltip("Reposition when the HUD is farther than this (m).")]
    [SerializeField] private float maxDistanceMeters = 2.5f;

    [Tooltip("Glide speed (fraction per second toward the target pose).")]
    [SerializeField, Range(0.5f, 10f)] private float glideRatePerSecond = 3f;

    private Transform _head;
    private bool _gliding;

    private void Update()
    {
        if (_head == null)
        {
            if (Camera.main == null) return;
            _head = Camera.main.transform;
            SnapToTarget();   // first frame: appear in view immediately
            return;
        }

        Vector3 toHud = transform.position - _head.position;
        float dist = toHud.magnitude;
        float offGaze = dist > 1e-4f ? Vector3.Angle(_head.forward, toHud) : 0f;

        if (!_gliding && (offGaze > maxOffGazeDegrees || dist < minDistanceMeters || dist > maxDistanceMeters))
        {
            _gliding = true;
        }

        if (_gliding)
        {
            Vector3 target = TargetPosition();
            float t = 1f - Mathf.Exp(-glideRatePerSecond * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, target, t);
            if ((transform.position - target).sqrMagnitude < 0.01f * 0.01f) _gliding = false;
        }

        // Yaw-only billboard: face the user without inheriting head roll/pitch.
        Vector3 face = transform.position - _head.position;
        face.y = 0f;
        if (face.sqrMagnitude > 1e-6f)
            transform.rotation = Quaternion.LookRotation(face.normalized, Vector3.up);
    }

    private Vector3 TargetPosition()
    {
        // In front of the head at eye height, flattened so the HUD doesn't dive
        // toward the floor when the user looks down.
        Vector3 fwd = _head.forward;
        fwd.y = 0f;
        fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;
        return _head.position + fwd * followDistanceMeters;
    }

    private void SnapToTarget()
    {
        transform.position = TargetPosition();
        Vector3 face = transform.position - _head.position;
        face.y = 0f;
        if (face.sqrMagnitude > 1e-6f)
            transform.rotation = Quaternion.LookRotation(face.normalized, Vector3.up);
    }
}
