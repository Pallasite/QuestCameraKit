using System;
using UnityEngine;

/// <summary>
/// Owns trial index state. Subscribes to <see cref="TrialLoader.OnDataLoaded"/>
/// to load the first trial, then advances on
/// <see cref="ObstacleController.OnTrialCompleted"/>.
///
/// Fires <see cref="OnTrialLoaded"/> with the current condition so that
/// <see cref="ObstacleController"/> can configure itself for each trial.
/// </summary>
public class TrialSequencer : MonoBehaviour
{
    [SerializeField] private TrialLoader trialLoader;
    [SerializeField] private ObstacleController obstacleController;

    // ---- public state ----

    public int CurrentTrialIndex { get; private set; } = 0;
    public TrialCondition CurrentTrial { get; private set; }

    // ---- events ----

    /// <summary>Fired when a new trial is loaded, with its condition data.</summary>
    public event Action<TrialCondition> OnTrialLoaded;

    /// <summary>Fired when all trials have been completed.</summary>
    public event Action OnSequenceComplete;

    // ---- lifecycle ----

    private void OnEnable()
    {
        if (trialLoader != null)
        {
            trialLoader.OnDataLoaded += HandleDataLoaded;
        }
        if (obstacleController != null)
        {
            obstacleController.OnTrialCompleted += HandleTrialCompleted;
        }
    }

    private void OnDisable()
    {
        if (trialLoader != null)
        {
            trialLoader.OnDataLoaded -= HandleDataLoaded;
        }
        if (obstacleController != null)
        {
            obstacleController.OnTrialCompleted -= HandleTrialCompleted;
        }
    }

    private void HandleDataLoaded()
    {
        // Load first trial when CSV becomes available
        LoadTrial(0);
    }

    private void HandleTrialCompleted()
    {
        AdvanceTrial();
    }

    // ---- public API ----

    /// <summary>Advance to the next trial.</summary>
    public void AdvanceTrial()
    {
        LoadTrial(CurrentTrialIndex + 1);
    }

    /// <summary>Go back to the previous trial.</summary>
    public void PreviousTrial()
    {
        LoadTrial(CurrentTrialIndex - 1);
    }

    /// <summary>
    /// Re-run the current trial: reset the obstacle to base (no advance) and reload the
    /// same condition. Used by the experimenter to redo a fouled walk.
    /// </summary>
    public void RedoCurrentTrial()
    {
        if (obstacleController != null) obstacleController.ResetForRedo();
        LoadTrial(CurrentTrialIndex);
    }

    /// <summary>
    /// Load a specific trial by index. If the index is out of range,
    /// fires <see cref="OnSequenceComplete"/>.
    /// </summary>
    public void LoadTrial(int index)
    {
        if (trialLoader == null || trialLoader.MissingData)
        {
            Debug.LogWarning("[TrialSequencer] No trial data available.");
            return;
        }

        CurrentTrialIndex = index;

        if (trialLoader.TrialConditions.TryGetValue(index, out TrialCondition condition))
        {
            CurrentTrial = condition;

            // Push data to obstacle controller
            if (obstacleController != null)
            {
                obstacleController.SetTrialData(condition);
            }

            OnTrialLoaded?.Invoke(condition);
            Debug.Log($"[TrialSequencer] Loaded trial {index}: {condition}");
        }
        else
        {
            // Beyond available trials
            CurrentTrial = null;
            Debug.Log($"[TrialSequencer] No trial data for index {index}. Sequence complete.");
            OnSequenceComplete?.Invoke();
        }
    }
}
