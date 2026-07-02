using UnityEngine;

/// <summary>
/// Turns the participant trial loop ON.
///
/// <see cref="ObstacleController"/> ships with <c>IsArmed</c> / <c>AutoReset</c> /
/// <c>TrialSequenceActive</c> defaulting to false, and nothing in the project ever
/// flips them — so a loaded trial never perturbs, resets, or advances. (That is the
/// loop that "never completed in the field": a walk loads but no move/reset/end ever
/// fires.) This component flips them true so the loop runs end-to-end:
/// trigger -> perturb -> recede -> auto-reset -> advance.
///
/// Optionally gates activation on <see cref="ObstaclePlacementController.IsPlaced"/>,
/// so participant trials don't begin until the obstacle has been calibrated to the tag.
/// </summary>
[DisallowMultipleComponent]
public sealed class TrialLoopActivator : MonoBehaviour
{
    [Header("Wiring (auto-resolved if empty)")]
    [SerializeField] private ObstacleController obstacleController;
    [SerializeField] private TrialSequencer trialSequencer;

    [Tooltip("Optional. If set, the loop is armed only once the obstacle is placed " +
             "(calibrated to the tag). Leave null to arm immediately on Start.")]
    [SerializeField] private ObstaclePlacementController placement;

    [Header("Behavior")]
    [Tooltip("Enable the obstacle's auto-reset-on-recede behavior.")]
    [SerializeField] private bool autoReset = true;

    [Tooltip("Advance to the next trial automatically when one completes.")]
    [SerializeField] private bool advanceTrials = true;

    private bool _activated;
    private bool _paused;

    /// <summary>True while the loop is paused (obstacle disarmed; no trigger/reset/advance).</summary>
    public bool IsPaused => _paused;

    private void Awake()
    {
        if (!obstacleController) obstacleController = FindAnyObjectByType<ObstacleController>();
        if (!trialSequencer) trialSequencer = FindAnyObjectByType<TrialSequencer>();
        if (!placement) placement = FindAnyObjectByType<ObstaclePlacementController>();
    }

    private void OnEnable()
    {
        if (trialSequencer) trialSequencer.OnTrialLoaded += HandleTrialLoaded;
    }

    private void OnDisable()
    {
        if (trialSequencer) trialSequencer.OnTrialLoaded -= HandleTrialLoaded;
    }

    private void Update()
    {
        if (_activated) return;
        if (placement && !placement.IsPlaced) return;   // wait for calibration
        Activate();
    }

    private void Activate()
    {
        if (_activated || obstacleController == null) return;
        _activated = true;
        obstacleController.AutoReset = autoReset;
        obstacleController.TrialSequenceActive = advanceTrials;
        obstacleController.ArmObstacle();
        Debug.Log("[TrialLoopActivator] Trial loop activated (armed; autoReset=" +
                  $"{autoReset}; advanceTrials={advanceTrials}).");
    }

    /// <summary>Pause the loop: disarm the obstacle so no trigger / reset / advance occurs.</summary>
    public void Pause()
    {
        if (!_activated || _paused || obstacleController == null) return;
        _paused = true;
        obstacleController.DisarmObstacle();
        Debug.Log("[TrialLoopActivator] Paused.");
    }

    /// <summary>Resume the loop: re-arm the obstacle.</summary>
    public void Resume()
    {
        if (!_paused || obstacleController == null) return;
        _paused = false;
        obstacleController.ArmObstacle();
        Debug.Log("[TrialLoopActivator] Resumed.");
    }

    private void HandleTrialLoaded(TrialCondition condition)
    {
        // Re-arm at the start of each trial. SetTrialData already cleared HasMoved;
        // this guards against anything having disarmed the obstacle between trials.
        if (_activated && !_paused && obstacleController != null) obstacleController.ArmObstacle();
    }
}
