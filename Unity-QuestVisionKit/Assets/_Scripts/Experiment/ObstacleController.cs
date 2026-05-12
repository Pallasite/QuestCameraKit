using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Core obstacle trial controller. Manages the proximity trigger loop,
/// move/reset state machine, and IObstacleBehavior delegation.
///
/// Resolves the obstacle via <see cref="ConstellationDriftCorrector.Obstacle"/>
/// (same pattern as ObstacleFinesseController.Target). Creates a
/// <c>perturbationPivot</c> child transform at startup so that trial
/// movements write to a separate transform surface from the experimenter's
/// finesse offset.
///
/// Transform hierarchy:
///   obstacle (finesse writes here)
///     └── perturbationPivot (this controller writes here)
///           ├── Visual Variant A
///           ├── Visual Variant B
///           └── ...
/// </summary>
public class ObstacleController : MonoBehaviour
{
    [Header("Wiring")]
    [SerializeField] private ConstellationDriftCorrector corrector;

    [Tooltip("Manual override. If set, this transform is used directly.")]
    [SerializeField] private Transform manualTarget;

    [Header("Reset Behavior")]
    [Tooltip("Distance from obstacle origin beyond which auto-reset triggers (meters).")]
    [SerializeField] private float resetDistance = 3f;

    [Tooltip("Seconds after move before auto-reset distance check begins.")]
    [SerializeField] private float timeBuffer = 2f;

    // ---- public state ----

    /// <summary>Whether the obstacle is armed for proximity triggering.</summary>
    public bool IsArmed { get; set; }

    /// <summary>Whether the obstacle has moved during the current trial.</summary>
    public bool HasMoved { get; private set; }

    /// <summary>Whether the obstacle auto-resets after move + buffer.</summary>
    public bool AutoReset { get; set; }

    /// <summary>Whether the trial sequence is actively advancing.</summary>
    public bool TrialSequenceActive { get; set; }

    // ---- events ----

    public event Action OnObstacleMoved;
    public event Action OnObstacleReset;
    public event Action OnTrialCompleted;

    // ---- runtime state ----

    private Transform _player;
    private Transform _obstacleRoot;       // The obstacle GO from corrector
    private Transform _perturbationPivot;  // Our child transform for trial movement

    private IObstacleBehavior _obstacleBehavior;
    private DefaultObstacleBehavior _defaultBehavior;

    // Current trial parameters
    private bool _activeInTrial;
    private bool _moveTowardsUser;
    private float _triggerDistance;
    private float _perturbationDistance;

    private Vector3 _obstacleOrigin;  // World position at trial start
    private float _timeSinceMove;

    // ---- public API ----

    /// <summary>
    /// Inject trial parameters from the sequencer.
    /// </summary>
    public void SetTrialData(TrialCondition condition)
    {
        if (condition == null) return;

        _activeInTrial = condition.IsActive;
        _moveTowardsUser = condition.MoveTowardsUser;
        _triggerDistance = condition.TriggerDistance;
        _perturbationDistance = condition.PerturbationDistance;

        // Re-arm for the new trial
        HasMoved = false;
        _timeSinceMove = 0f;

        // Store current obstacle origin for reset distance calculation
        if (_perturbationPivot != null)
        {
            _obstacleOrigin = _perturbationPivot.position;
        }

        Debug.Log($"[ObstacleController] Trial data set: {condition}");
    }

    public void ArmObstacle()
    {
        IsArmed = true;
    }

    public void DisarmObstacle()
    {
        IsArmed = false;
    }

    /// <summary>The perturbationPivot transform (available after setup).</summary>
    public Transform PerturbationPivot => _perturbationPivot;

    // ---- lifecycle ----

    private void Start()
    {
        _player = Camera.main != null ? Camera.main.transform : null;

        // Create default behavior
        _defaultBehavior = gameObject.AddComponent<DefaultObstacleBehavior>();

        // Try to set up obstacle immediately if available
        TrySetupObstacle();

        // Listen for calibration events
        if (corrector != null)
        {
            corrector.OnConstellationCalibrated += HandleCalibrated;
        }
    }

    private void OnDestroy()
    {
        if (corrector != null)
        {
            corrector.OnConstellationCalibrated -= HandleCalibrated;
        }
    }

    private void HandleCalibrated()
    {
        // Obstacle may have just become available after calibration
        TrySetupObstacle();
    }

    private Transform ResolveObstacle()
    {
        if (manualTarget) return manualTarget;
        if (corrector && corrector.Obstacle) return corrector.Obstacle.transform;
        return null;
    }

    /// <summary>
    /// Creates the perturbationPivot and reparents existing children under it.
    /// </summary>
    private void TrySetupObstacle()
    {
        var obstacle = ResolveObstacle();
        if (obstacle == null || obstacle == _obstacleRoot) return;

        _obstacleRoot = obstacle;

        // Check if perturbationPivot already exists (e.g., after recalibration)
        var existingPivot = obstacle.Find("PerturbationPivot");
        if (existingPivot != null)
        {
            _perturbationPivot = existingPivot;
        }
        else
        {
            // Create perturbation pivot as intermediate child
            var pivotGO = new GameObject("PerturbationPivot");
            pivotGO.transform.SetParent(obstacle, worldPositionStays: false);
            pivotGO.transform.localPosition = Vector3.zero;
            pivotGO.transform.localRotation = Quaternion.identity;

            // Reparent all existing children of obstacle under the pivot
            var children = new List<Transform>();
            for (int i = 0; i < obstacle.childCount; i++)
            {
                var child = obstacle.GetChild(i);
                if (child != pivotGO.transform) children.Add(child);
            }
            foreach (var child in children)
            {
                child.SetParent(pivotGO.transform, worldPositionStays: true);
            }

            _perturbationPivot = pivotGO.transform;
        }

        // Refresh behavior — check for custom IObstacleBehavior on obstacle
        RefreshObstacleBehavior();

        // Store origin
        _obstacleOrigin = _perturbationPivot.position;

        Debug.Log("[ObstacleController] Obstacle setup complete with perturbationPivot.");
    }

    private void RefreshObstacleBehavior()
    {
        if (_obstacleRoot != null)
        {
            _obstacleBehavior = _obstacleRoot.GetComponent<IObstacleBehavior>() ?? _defaultBehavior;
        }
        else
        {
            _obstacleBehavior = _defaultBehavior;
        }
    }

    // ---- update loop ----

    private void Update()
    {
        if (_player == null || _perturbationPivot == null) return;

        // Project player position onto XZ plane at obstacle height
        Vector3 playerXZ = new Vector3(
            _player.position.x,
            _perturbationPivot.position.y,
            _player.position.z
        );

        if (IsArmed && !HasMoved)
        {
            // Proximity trigger check
            float distToPlayer = Vector3.Distance(playerXZ, _perturbationPivot.position);

            if (distToPlayer <= _triggerDistance)
            {
                if (_activeInTrial)
                {
                    MoveObstacle();
                }
                else
                {
                    // Inactive trial: mark as moved so auto-reset/advance works
                    HasMoved = true;
                    _timeSinceMove = 0f;
                    _obstacleOrigin = _perturbationPivot.position;
                }
            }
        }
        else if (IsArmed && HasMoved && AutoReset)
        {
            // Auto-reset: wait for time buffer, then check reset distance
            _timeSinceMove += Time.deltaTime;

            if (_timeSinceMove > timeBuffer)
            {
                float distFromOrigin = Vector3.Distance(_player.position, _obstacleOrigin);

                if (distFromOrigin >= resetDistance)
                {
                    ResetObstacle();
                }
            }
        }
    }

    // ---- move / reset ----

    private void MoveObstacle()
    {
        _obstacleBehavior.Move(
            _perturbationPivot,
            _player,
            _perturbationDistance,
            _moveTowardsUser
        );

        HasMoved = true;
        _timeSinceMove = 0f;
        _obstacleOrigin = _perturbationPivot.position;

        OnObstacleMoved?.Invoke();
    }

    private void ResetObstacle()
    {
        _obstacleBehavior.Reset(_perturbationPivot);

        HasMoved = false;
        _timeSinceMove = 0f;

        OnObstacleReset?.Invoke();

        if (TrialSequenceActive)
        {
            OnTrialCompleted?.Invoke();
        }
    }
}
