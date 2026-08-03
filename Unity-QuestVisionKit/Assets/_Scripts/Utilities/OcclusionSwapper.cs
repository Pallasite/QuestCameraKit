using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Automatically swaps between occlusion and non-occlusion materials based on
/// player distance. The depth occlusion material shows visual errors when the
/// player is too far away due to depth map inaccuracy at range. This component
/// handles swapping automatically.
///
/// Attach to each Renderer that needs distance-based depth occlusion material
/// swapping (typically on each visual variant's mesh renderer).
///
/// Use static methods (e.g., <see cref="SetAllOcclusionDistance"/>) to control
/// all instances globally.
/// </summary>
public class OcclusionSwapper : MonoBehaviour
{
    // ==================== STATIC REGISTRY ====================
    // All active OcclusionSwapper instances register here for bulk control

    private static HashSet<OcclusionSwapper> _allInstances = new HashSet<OcclusionSwapper>();

    /// <summary>
    /// Get all active OcclusionSwapper instances in the scene.
    /// </summary>
    public static IReadOnlyCollection<OcclusionSwapper> AllInstances => _allInstances;

    /// <summary>
    /// Set the occlusion distance for ALL OcclusionSwappers in the scene.
    /// </summary>
    public static void SetAllOcclusionDistance(float distance)
    {
        foreach (var swapper in _allInstances)
        {
            swapper.occlusionDistance = distance;
        }
    }

    /// <summary>
    /// Enable or disable auto-swap for ALL OcclusionSwappers in the scene.
    /// </summary>
    public static void SetAllAutoSwapEnabled(bool enabled)
    {
        foreach (var swapper in _allInstances)
        {
            swapper.autoSwapEnabled = enabled;
        }
    }

    /// <summary>
    /// Force all OcclusionSwappers to a specific occlusion state.
    /// </summary>
    public static void SetAllOcclusion(bool occlude)
    {
        foreach (var swapper in _allInstances)
        {
            swapper.SetObstacleOcclusion(occlude);
        }
    }

    // ==================== INSTANCE FIELDS ====================

    [Header("Materials")]
    [Tooltip("Material with depth occlusion enabled - used when player is close")]
    public Material depthOcclusionMaterial;

    [Tooltip("Material without depth occlusion - used when player is far")]
    public Material noOcclusionMaterial;

    [Header("Distance Settings")]
    [Tooltip("Distance at which to swap to occlusion material (player closer than this = use occlusion)")]
    public float occlusionDistance = 1f;

    [Header("Runtime Control")]
    [Tooltip("Enable/disable automatic distance-based swapping")]
    public bool autoSwapEnabled = true;

    // Cached references for performance
    private Transform _player;
    private Renderer _renderer;
    private bool _isOccluding = false;

    /// <summary>Whether this renderer currently uses the depth-occlusion material.</summary>
    public bool IsOccluding => _isOccluding;

    // ==================== LIFECYCLE ====================

    private void Awake()
    {
        // Register this instance
        _allInstances.Add(this);

        _renderer = GetComponent<Renderer>();
        if (_renderer == null)
        {
            Debug.LogError("[OcclusionSwapper] No Renderer found on this GameObject.");
        }
    }

    private void OnDestroy()
    {
        // Unregister when destroyed
        _allInstances.Remove(this);
    }

    private void Start()
    {
        // Cache player reference (main camera = player head)
        if (Camera.main != null)
        {
            _player = Camera.main.transform;
        }
        else
        {
            Debug.LogWarning("[OcclusionSwapper] No main camera found. Auto-swap will not work.");
        }

        WarnIfDepthStackMissing();

        // Start with non-occlusion material (player assumed far away initially)
        SetObstacleOcclusion(false);
    }

    // The occlusion material is not self-contained: Meta's occlusion subgraph
    // hard-codes occlusion = 1.0 (fully visible) unless an active
    // EnvironmentDepthManager globally enables HARD_/SOFT_OCCLUSION and binds
    // the depth texture. Without one, material swaps run and report success
    // while rendering identically — a completely silent failure that survived
    // to a device pass once already (Scratch scene, 2026-08).
    private void WarnIfDepthStackMissing()
    {
        if (!Meta.XR.EnvironmentDepth.EnvironmentDepthManager.IsSupported)
        {
            Debug.Log("[OcclusionSwapper] Environment depth not supported here (editor/PC) — " +
                      "occlusion swaps will have no visual effect in this session.");
            return;
        }

        var depthManager = FindAnyObjectByType<Meta.XR.EnvironmentDepth.EnvironmentDepthManager>();
        if (depthManager == null)
        {
            Debug.LogWarning("[OcclusionSwapper] No EnvironmentDepthManager in the scene — occlusion " +
                             "material swaps will be visually INERT (the occlusion shader defaults to " +
                             "fully visible without it). Add the '[BuildingBlock] Occlusion Dependencies' " +
                             "object with an EnvironmentDepthManager.");
        }
        else if (depthManager.OcclusionShadersMode == Meta.XR.EnvironmentDepth.OcclusionShadersMode.None)
        {
            Debug.LogWarning("[OcclusionSwapper] EnvironmentDepthManager.OcclusionShadersMode is None — " +
                             "occlusion material swaps will be visually INERT. Set it to SoftOcclusion " +
                             "(or HardOcclusion for lower GPU cost).");
        }
    }

    private void Update()
    {
        if (!autoSwapEnabled || _player == null || _renderer == null) return;

        // Calculate XZ distance (ignore height difference)
        Vector3 playerXZ = new Vector3(_player.position.x, transform.position.y, _player.position.z);
        float distance = Vector3.Distance(playerXZ, transform.position);

        // Swap material based on distance
        bool shouldOcclude = distance <= occlusionDistance;

        // Only swap if state changed (avoid setting material every frame)
        if (shouldOcclude != _isOccluding)
        {
            SetObstacleOcclusion(shouldOcclude);
        }
    }

    // ==================== PUBLIC INSTANCE METHODS ====================

    /// <summary>
    /// Manually set the occlusion state. Also updates internal tracking.
    /// </summary>
    /// <param name="occlude">True to use depth occlusion material, false for no occlusion.</param>
    public void SetObstacleOcclusion(bool occlude)
    {
        if (_renderer == null) return;

        _isOccluding = occlude;
        _renderer.material = occlude ? depthOcclusionMaterial : noOcclusionMaterial;
    }

    /// <summary>
    /// Enable or disable automatic distance-based material swapping.
    /// </summary>
    public void SetAutoSwapEnabled(bool enabled)
    {
        autoSwapEnabled = enabled;
    }
}
