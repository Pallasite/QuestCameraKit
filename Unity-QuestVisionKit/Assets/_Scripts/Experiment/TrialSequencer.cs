using System;
using UnityEngine;

/// <summary>Why the most recent trial load happened. Loggers key on this:
/// only an Advance means the previous walk was actually completed.</summary>
public enum TrialLoadReason { Initial, Advance, Redo, Jump }

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

    /// <summary>Why the most recent <see cref="LoadTrial"/> happened. Read by
    /// <see cref="OnTrialLoaded"/> subscribers — walk-row logging used to infer
    /// completion from index arithmetic, which stamped a manually skipped
    /// (abandoned) walk as a completed one.</summary>
    public TrialLoadReason LastLoadReason { get; private set; } = TrialLoadReason.Initial;

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
        // Load the FIRST trial in the CSV, whatever its number — a 1-based
        // file used to miss index 0 and fire sequence-complete at boot.
        LastLoadReason = TrialLoadReason.Initial;
        LoadTrial(trialLoader != null && !trialLoader.MissingData ? trialLoader.MinTrialNumber : 0);
    }

    private void HandleTrialCompleted()
    {
        AdvanceTrial();
    }

    // ---- public API ----

    /// <summary>Advance to the next trial (the previous walk completed).
    /// Steps to the next EXISTING trial number — a numbering hole in a
    /// hand-authored CSV used to fire sequence-complete mid-session and end
    /// the study early. Sequence-complete now fires only past the last row.</summary>
    public void AdvanceTrial()
    {
        LastLoadReason = TrialLoadReason.Advance;
        LoadTrial(TryGetAdjacentTrial(CurrentTrialIndex, +1, out int next)
            ? next
            : CurrentTrialIndex + 1);   // past the end → OnSequenceComplete
    }

    /// <summary>Go back to the previous trial.</summary>
    public void PreviousTrial()
    {
        LastLoadReason = TrialLoadReason.Jump;
        LoadTrial(CurrentTrialIndex - 1);
    }

    /// <summary>
    /// Re-run the current trial: reset the obstacle to base (no advance) and reload the
    /// same condition. Used by the experimenter to redo a fouled walk.
    /// </summary>
    public void RedoCurrentTrial()
    {
        LastLoadReason = TrialLoadReason.Redo;
        if (obstacleController != null) obstacleController.ResetForRedo();
        LoadTrial(CurrentTrialIndex);
    }

    /// <summary>
    /// Jump directly to a specific trial WITHOUT completing the current one:
    /// resets the obstacle to base (no correction, no advance) and loads the
    /// target condition. Returns false — changing nothing — when the index has
    /// no row in the CSV. Unlike <see cref="LoadTrial"/>, a miss here must not
    /// fire <see cref="OnSequenceComplete"/>: stepping back from the first
    /// trial (or forward past the last) must not end the session.
    /// </summary>
    public bool JumpToTrial(int index)
    {
        if (trialLoader == null || trialLoader.MissingData) return false;
        if (!trialLoader.TrialConditions.ContainsKey(index)) return false;

        LastLoadReason = TrialLoadReason.Jump;
        if (obstacleController != null) obstacleController.ResetForRedo();
        LoadTrial(index);
        return true;
    }

    /// <summary>
    /// Nearest trial number that exists in the CSV strictly beyond
    /// <paramref name="fromIndex"/> in <paramref name="direction"/> (+1/-1).
    /// Lets manual navigation step OVER numbering gaps instead of getting
    /// stuck at a hole. False when none exists in that direction.
    /// </summary>
    public bool TryGetAdjacentTrial(int fromIndex, int direction, out int index)
    {
        index = 0;
        if (trialLoader == null || trialLoader.MissingData) return false;

        bool found = false;
        foreach (int key in trialLoader.TrialConditions.Keys)
        {
            if (direction > 0 ? key <= fromIndex : key >= fromIndex) continue;
            if (!found || (direction > 0 ? key < index : key > index))
            {
                index = key;
                found = true;
            }
        }
        return found;
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
