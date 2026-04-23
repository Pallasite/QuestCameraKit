using System;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR;

/// <summary>
/// Manages the 3D visualization of detected AprilTags by converting
/// camera-space poses to world-space using the camera's world pose at
/// time of capture.
///
/// Mirrors QrCodeDisplayManager's architecture but uses the AprilTag
/// library's direct pose estimation (position + rotation in camera space)
/// rather than UV-corner raycasting, providing sub-centimeter accuracy.
///
/// Supports two placement modes:
/// - Direct: Uses the AprilTag library's pose directly (faster, works without MRUK)
/// - EnvironmentRaycast: Refines placement by raycasting onto the scene mesh (more stable)
/// </summary>
public class AprilTagDisplayManager : MonoBehaviour
{
    public enum PlacementMode
    {
        Direct,
        EnvironmentRaycast
    }

    /// <summary>
    /// Holds a detected tag's world-space pose after camera transform.
    /// </summary>
    public struct TagWorldPose
    {
        public int TagId;
        public Vector3 Position;
        public Quaternion Rotation;
    }

    [SerializeField] private PlacementMode placementMode = PlacementMode.Direct;

    [Tooltip("Visual scale multiplier for marker display (does not affect pose accuracy).")]
    [SerializeField] private float markerDisplayScale = 1f;

    /// <summary>
    /// Fired each frame that tags are detected, after world poses are computed.
    /// Subscribe from additional visualizers (e.g. AprilTagWireframeVisualizer)
    /// to receive results without running a second scan.
    /// </summary>
    public event Action<TagWorldPose[]> OnTagsDetected;

    private IAprilTagScanner _scanner;
    private EnvironmentRaycastManager _envRaycastManager;
    private readonly Dictionary<int, MarkerController> _activeMarkers = new();
    private bool _scanInProgress;

    private void Awake()
    {
        // Prefer a stereo scanner if present (it triangulates and avoids the
        // single-camera depth bias that misaligns one eye); fall back to mono.
        // Use Unity's overloaded != null (not ??) so destroyed components are treated as null.
        var stereo = GetComponent<StereoAprilTagScanner>();
        _scanner = stereo != null ? (IAprilTagScanner)stereo : GetComponent<AprilTagScanner>();
        _envRaycastManager = GetComponent<EnvironmentRaycastManager>();
    }

    private void Update()
    {
        if (!_scanInProgress) RefreshMarkers();
    }

    private async void RefreshMarkers()
    {
        if (_scanner == null) return;
        _scanInProgress = true;

        AprilTagResult[] results;
        try
        {
            results = await _scanner.ScanFrameAsync();
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[AprilTagDisplayManager] Scan error: {ex.Message}");
            _scanInProgress = false;
            return;
        }

        _scanInProgress = false;

        if (results == null || results.Length == 0)
        {
            CleanupInactiveMarkers();
            return;
        }

        // Build world poses and collect them for the event
        var worldPoses = new List<TagWorldPose>(results.Length);

        foreach (var result in results)
        {
            if (!TryBuildWorldPose(result, out var worldPos, out var worldRot))
            {
                continue;
            }

            worldPoses.Add(new TagWorldPose
            {
                TagId = result.tagId,
                Position = worldPos,
                Rotation = worldRot
            });

            var marker = GetOrCreateMarker(result.tagId);
            if (!marker) continue;

            var scale = new Vector3(markerDisplayScale, markerDisplayScale, 1f);
            marker.UpdateMarker(worldPos, worldRot, scale, $"Tag {result.tagId}");
        }

        // Notify any additional visualizers
        if (worldPoses.Count > 0)
        {
            OnTagsDetected?.Invoke(worldPoses.ToArray());
        }

        CleanupInactiveMarkers();
    }

    /// <summary>
    /// Converts the AprilTag's camera-space pose to world-space using
    /// the camera's world pose captured at detection time.
    /// </summary>
    private bool TryBuildWorldPose(AprilTagResult result, out Vector3 worldPos, out Quaternion worldRot)
    {
        worldPos = default;
        worldRot = default;

        var camPose = result.cameraPose;

        if (result.worldPoseOverride.HasValue)
        {
            var wp = result.worldPoseOverride.Value;
            worldPos = wp.position;
            worldRot = wp.rotation;
        }
        else
        {
            worldPos = camPose.position + camPose.rotation * result.localPosition;
            worldRot = camPose.rotation * result.localRotation;
        }

        if (placementMode == PlacementMode.EnvironmentRaycast && _envRaycastManager)
        {
            // Refine placement by raycasting toward the detected position
            var ray = new Ray(camPose.position, (worldPos - camPose.position).normalized);
            if (_envRaycastManager.Raycast(ray, out var hit))
            {
                worldPos = hit.point;
                // Keep the rotation from AprilTag pose (more accurate than surface normal alone)
                // but align the forward axis with the hit normal for stability
                var up = worldRot * Vector3.up;
                var forward = -hit.normal;
                if (Vector3.Dot(forward, worldRot * Vector3.forward) < 0)
                {
                    forward = hit.normal;
                }
                worldRot = Quaternion.LookRotation(forward, up);
            }
        }

        return true;
    }

    private MarkerController GetOrCreateMarker(int tagId)
    {
        if (_activeMarkers.TryGetValue(tagId, out var marker))
        {
            return marker;
        }

        var markerGo = MarkerPool.Instance ? MarkerPool.Instance.GetMarker() : null;
        if (!markerGo) return null;

        marker = markerGo.GetComponent<MarkerController>();
        if (!marker) return null;

        _activeMarkers[tagId] = marker;
        return marker;
    }

    private void CleanupInactiveMarkers()
    {
        var keysToRemove = new List<int>();
        foreach (var kvp in _activeMarkers)
        {
            if (!kvp.Value || !kvp.Value.gameObject.activeSelf)
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        foreach (var key in keysToRemove)
        {
            _activeMarkers.Remove(key);
        }
    }
}
