using UnityEngine;

/// <summary>
/// Secondary visualizer that draws keijiro-style wireframe cubes on
/// detected AprilTags. Subscribes to AprilTagDisplayManager.OnTagsDetected
/// so it reuses the same scan results — no duplicate camera processing.
///
/// Setup:
/// 1. Add this component to the same GameObject as AprilTagDisplayManager.
/// 2. Assign a wireframe material (an unlit single-color material works well;
///    "Unlit/Color" with a bright green or cyan is a good default).
/// 3. The wireframe cube, crosshair, and forward axis will draw on every
///    detected tag at its world-space pose, scaled to the physical tag size.
///
/// The wireframe shows:
/// - A square on the tag face (the detected tag plane)
/// - A cube extruding forward from the tag surface
/// - A crosshair at the tag center
/// - A forward axis line extending beyond the cube
/// </summary>
public class AprilTagWireframeVisualizer : MonoBehaviour
{
    [Tooltip("Material for the wireframe lines. Use an unlit color material " +
             "(e.g. Unlit/Color set to bright green) for best visibility.")]
    [SerializeField] private Material wireframeMaterial;

    [Tooltip("Physical tag size in meters. Must match the AprilTagScanner's tagSizeMeters " +
             "for the wireframe to align with the detected pose.")]
    [SerializeField] private float tagSizeMeters = 0.1f;

    private AprilTagDisplayManager _displayManager;
    private AprilTagWireframeDrawer _drawer;

    // Store the latest poses so we can draw them in LateUpdate
    // (Graphics.DrawMesh should be called from LateUpdate for correct rendering order)
    private AprilTagDisplayManager.TagWorldPose[] _latestPoses;
    private int _lastUpdateFrame = -1;

    private void OnEnable()
    {
        _displayManager = GetComponent<AprilTagDisplayManager>();
        if (!_displayManager)
        {
            Debug.LogError("[AprilTagWireframeVisualizer] No AprilTagDisplayManager found " +
                           "on this GameObject. Add one or place this component alongside it.");
            enabled = false;
            return;
        }

        if (!wireframeMaterial)
        {
            Debug.LogError("[AprilTagWireframeVisualizer] No wireframe material assigned.");
            enabled = false;
            return;
        }

        _drawer = new AprilTagWireframeDrawer(wireframeMaterial);
        _displayManager.OnTagsDetected += HandleTagsDetected;
    }

    private void OnDisable()
    {
        if (_displayManager)
        {
            _displayManager.OnTagsDetected -= HandleTagsDetected;
        }

        _drawer?.Dispose();
        _drawer = null;
        _latestPoses = null;
    }

    private void HandleTagsDetected(AprilTagDisplayManager.TagWorldPose[] poses)
    {
        _latestPoses = poses;
        _lastUpdateFrame = Time.frameCount;
    }

    private void LateUpdate()
    {
        if (_drawer == null || _latestPoses == null) return;

        // Only draw if we received poses this frame (avoids stale draws)
        if (Time.frameCount != _lastUpdateFrame) return;

        foreach (var pose in _latestPoses)
        {
            _drawer.Draw(pose.Position, pose.Rotation, tagSizeMeters);
        }
    }
}
