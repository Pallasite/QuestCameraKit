using System.Collections.Generic;
using UnityEngine;

public class MarkerPool : MonoBehaviour
{
    public static MarkerPool Instance { get; private set; }

    [Tooltip("Prefab for a QR code marker.")]
    [SerializeField] private GameObject markerPrefab;

    [Tooltip("Number of markers to pre-instantiate.")]
    [SerializeField] private int poolSize = 4;

    private List<GameObject> _pool;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Destroy(gameObject);
            return;
        }

        _pool = new List<GameObject>(poolSize);

        // A marker prefab without a MarkerController is a mis-wiring, not a
        // marker: consumers (AprilTagDisplayManager.GetOrCreateMarker) reject
        // the object AFTER the pool activates it, leaking a visible orphan at
        // world origin. This exact accident shipped once with the obstacle
        // prefab wired here — refuse to pre-warm instead.
        if (markerPrefab == null || markerPrefab.GetComponentInChildren<MarkerController>(true) == null)
        {
            Debug.LogError($"[MarkerPool] markerPrefab '{(markerPrefab ? markerPrefab.name : "null")}' has no " +
                           "MarkerController — pool disabled. Assign a marker prefab or remove this component.");
            return;
        }

        for (var i = 0; i < poolSize; i++)
        {
            var marker = Instantiate(markerPrefab, transform);
            marker.SetActive(false);
            _pool.Add(marker);
        }
    }

    public GameObject GetMarker()
    {
        foreach (var marker in _pool)
        {
            if (marker.activeSelf) continue;
            marker.SetActive(true);
            return marker;
        }
        Debug.LogWarning("No available marker in pool!");
        return null;
    }
}
