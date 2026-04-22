using TMPro;
using UnityEngine;

/// <summary>
/// Attached to the anchored content prefab. Labels the anchor with its tag ID
/// and optionally billboards the label toward the main camera.
/// </summary>
public class AnchoredContentController : MonoBehaviour
{
    [SerializeField] private TextMeshPro label;

    [Tooltip("Keep the label facing the user. Applies only to the label transform.")]
    [SerializeField] private bool billboardLabel = true;

    private Transform _cam;

    public void SetTagId(int tagId)
    {
        if (label) label.text = $"Tag {tagId}";
    }

    private void LateUpdate()
    {
        if (!billboardLabel || !label) return;
        if (!_cam)
        {
            if (!Camera.main) return;
            _cam = Camera.main.transform;
        }

        var forward = label.transform.position - _cam.position;
        if (forward.sqrMagnitude < 1e-6f) return;
        label.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
    }
}
