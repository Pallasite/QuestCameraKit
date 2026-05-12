using System.Collections;
using UnityEngine;
using TMPro;

/// <summary>
/// Diagnostic HUD that polls <see cref="TrialSequencer"/> and
/// <see cref="TrialLoader"/> state every 250ms and updates TMP text fields.
///
/// Display: "Trial {current}/{total}"
/// Color: Green = trial sequence active, Red = inactive, Magenta = missing CSV data.
/// </summary>
public class TrialDiagnosticsHUD : MonoBehaviour
{
    [Header("Data Sources")]
    [SerializeField] private TrialSequencer trialSequencer;
    [SerializeField] private TrialLoader trialLoader;
    [SerializeField] private ObstacleController obstacleController;

    [Header("Display")]
    [Tooltip("TMP text components to update. Wire via Inspector.")]
    [SerializeField] private TMP_Text[] trialDisplayTexts;

    [Tooltip("Poll interval in seconds.")]
    [SerializeField] private float pollInterval = 0.25f;

    private void Start()
    {
        StartCoroutine(UpdateLoop());
    }

    private IEnumerator UpdateLoop()
    {
        var wait = new WaitForSeconds(pollInterval);

        while (true)
        {
            UpdateDisplay();
            yield return wait;
        }
    }

    private void UpdateDisplay()
    {
        if (trialDisplayTexts == null || trialDisplayTexts.Length == 0) return;

        // Build display text
        string displayText;
        Color displayColor;

        if (trialLoader == null || trialLoader.MissingData)
        {
            displayText = "No CSV";
            displayColor = Color.magenta;
        }
        else if (trialSequencer != null)
        {
            int current = trialSequencer.CurrentTrialIndex;
            int total = trialLoader.TrialCount;
            displayText = $"Trial {current}/{total}";

            if (obstacleController != null && obstacleController.TrialSequenceActive)
            {
                displayColor = Color.green;
            }
            else
            {
                displayColor = Color.red;
            }
        }
        else
        {
            displayText = "No Sequencer";
            displayColor = Color.yellow;
        }

        // Apply to all text components
        foreach (var text in trialDisplayTexts)
        {
            if (text != null)
            {
                text.text = displayText;
                text.color = displayColor;
            }
        }
    }
}
