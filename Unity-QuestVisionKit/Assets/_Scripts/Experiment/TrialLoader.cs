using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Loads and parses trial condition data from a CSV file.
///
/// File resolution order:
///   1. <c>Application.persistentDataPath/trial_conditions.csv</c> (runtime — 
///      pushed per-participant via <c>adb push</c>).
///   2. <c>StreamingAssets/trial_conditions_template.csv</c> (bundled default —
///      copied to persistentDataPath on first run).
///
/// On Android/Quest, <c>StreamingAssets</c> lives inside the APK jar and
/// requires <see cref="UnityWebRequest"/> to read.
///
/// CSV format (no header row):
///   <c>trialNumber, isActive, moveTowardsUser, triggerDistance, perturbationDistance</c>
///
/// Fires <see cref="OnDataLoaded"/> after successful parse,
/// <see cref="OnDataError"/> on failure.
/// </summary>
public class TrialLoader : MonoBehaviour
{
    // ---- public API ----

    /// <summary>All parsed trial conditions keyed by trial number.</summary>
    public Dictionary<int, TrialCondition> TrialConditions { get; private set; }
        = new Dictionary<int, TrialCondition>();

    /// <summary>Number of trials successfully parsed.</summary>
    public int TrialCount => TrialConditions.Count;

    /// <summary>Lowest trial number in the CSV (0 when none loaded). The HUD's
    /// "Trial N/M" position is computed against this, so 0- and 1-based files
    /// both display naturally.</summary>
    public int MinTrialNumber { get; private set; }

    /// <summary>Highest trial number in the CSV (0 when none loaded).</summary>
    public int MaxTrialNumber { get; private set; }

    /// <summary>Non-null when the CSV loaded but with structural issues
    /// (duplicate trial numbers, numbering gaps, unparseable lines). Shown on
    /// the Setup HUD — these used to surface only in logcat, presenting to the
    /// operator as "placement isn't working" or a session that ends early.</summary>
    public string DataWarning { get; private set; }

    /// <summary>True if the CSV file could not be found or parsed.</summary>
    public bool MissingData { get; private set; } = true;

    /// <summary>Fired after CSV is successfully parsed.</summary>
    public event Action OnDataLoaded;

    /// <summary>Fired when CSV loading fails, with error description.</summary>
    public event Action<string> OnDataError;

    // ---- constants ----
    private const string RuntimeFileName = "trial_conditions.csv";
    private const string StreamingAssetsFileName = "trial_conditions_template.csv";

    // ---- lifecycle ----

    private void Start()
    {
        StartCoroutine(LoadCSVCoroutine());
    }

    /// <summary>
    /// Force a reload of the CSV data. Useful after an <c>adb push</c> of
    /// a new file mid-session.
    /// </summary>
    public void Reload()
    {
        TrialConditions.Clear();
        MissingData = true;
        StartCoroutine(LoadCSVCoroutine());
    }

    // ---- loading pipeline ----

    private IEnumerator LoadCSVCoroutine()
    {
        string runtimePath = Path.Combine(Application.persistentDataPath, RuntimeFileName);

        // 1. Try runtime CSV (experimenter-pushed via adb)
        if (File.Exists(runtimePath))
        {
            string csvData = File.ReadAllText(runtimePath);
            ParseCSV(csvData, source: "runtime");
            Debug.Log($"[TrialLoader] Loaded {TrialCount} trials from: {runtimePath}");
            yield break;
        }

        // 2. Fall back to StreamingAssets template and copy to persistentDataPath
        Debug.Log("[TrialLoader] Runtime CSV not found. Copying template from StreamingAssets...");
        string streamingPath = Path.Combine(Application.streamingAssetsPath, StreamingAssetsFileName);

        // On Android, StreamingAssets is inside the APK jar → use UnityWebRequest
        string csvContent = null;

#if UNITY_ANDROID && !UNITY_EDITOR
        using (var request = UnityWebRequest.Get(streamingPath))
        {
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                string error = $"Failed to load StreamingAssets template: {request.error}";
                Debug.LogError($"[TrialLoader] {error}");
                MissingData = true;
                OnDataError?.Invoke(error);
                yield break;
            }
            csvContent = request.downloadHandler.text;
        }
#else
        if (File.Exists(streamingPath))
        {
            csvContent = File.ReadAllText(streamingPath);
        }
        else
        {
            string error = $"No CSV found at runtime path ({runtimePath}) or StreamingAssets ({streamingPath}).";
            Debug.LogError($"[TrialLoader] {error}");
            MissingData = true;
            OnDataError?.Invoke(error);
            yield break;
        }
#endif

        if (string.IsNullOrEmpty(csvContent))
        {
            string error = "StreamingAssets template was empty.";
            Debug.LogError($"[TrialLoader] {error}");
            MissingData = true;
            OnDataError?.Invoke(error);
            yield break;
        }

        // Copy template to persistentDataPath for future runs
        try
        {
            File.WriteAllText(runtimePath, csvContent);
            Debug.Log($"[TrialLoader] Template copied to: {runtimePath}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[TrialLoader] Could not copy template to persistentDataPath: {e.Message}");
            // Non-fatal — we can still parse the content we already loaded
        }

        ParseCSV(csvContent, source: "template");
        Debug.Log($"[TrialLoader] Loaded {TrialCount} trials from StreamingAssets template.");
    }

    // ---- parsing ----

    private void ParseCSV(string csvData, string source)
    {
        TrialConditions.Clear();

        string[] lines = csvData.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        int parsed = 0;
        int duplicates = 0;
        int invalidLines = 0;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Skip comment lines
            if (trimmed.StartsWith("#") || trimmed.StartsWith("//")) continue;

            string[] values = trimmed.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length >= 5)
            {
                // Invariant culture: a device set to a comma-decimal locale
                // would otherwise silently misread "1.5" (or reject it), and
                // the failure looks like a bad CSV rather than a locale bug.
                if (int.TryParse(values[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int trialNumber) &&
                    bool.TryParse(values[1].Trim(), out bool isActive) &&
                    bool.TryParse(values[2].Trim(), out bool moveTowardsUser) &&
                    float.TryParse(values[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float triggerDistance) &&
                    float.TryParse(values[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float perturbationDistance))
                {
                    if (TrialConditions.ContainsKey(trialNumber))
                    {
                        duplicates++;
                        Debug.LogWarning($"[TrialLoader] Duplicate trial number {trialNumber} — later row wins: {trimmed}");
                    }
                    TrialConditions[trialNumber] = new TrialCondition
                    {
                        TrialNumber = trialNumber,
                        IsActive = isActive,
                        MoveTowardsUser = moveTowardsUser,
                        TriggerDistance = triggerDistance,
                        PerturbationDistance = perturbationDistance
                    };
                    parsed++;
                }
                else
                {
                    invalidLines++;
                    Debug.LogWarning($"[TrialLoader] Invalid data on line: {trimmed}");
                }
            }
            else
            {
                invalidLines++;
                Debug.LogWarning($"[TrialLoader] Incomplete data on line ({values.Length} columns): {trimmed}");
            }
        }

        if (parsed > 0)
        {
            MinTrialNumber = int.MaxValue;
            MaxTrialNumber = int.MinValue;
            foreach (int key in TrialConditions.Keys)
            {
                if (key < MinTrialNumber) MinTrialNumber = key;
                if (key > MaxTrialNumber) MaxTrialNumber = key;
            }

            // Structural validation. A duplicate silently overwrites (creating
            // a hole where its first occurrence sat), and a hole used to end
            // the session at that index. Loud on the HUD + one CSV row.
            int gaps = (MaxTrialNumber - MinTrialNumber + 1) - TrialConditions.Count;
            DataWarning = (duplicates > 0 || gaps > 0 || invalidLines > 0)
                ? $"trial CSV: {duplicates} duplicate(s), {gaps} gap(s), {invalidLines} bad line(s)"
                : null;
            if (DataWarning != null) Debug.LogWarning($"[TrialLoader] {DataWarning}");

            SessionLogger.Instance?.Enqueue(LogEvent.SessionEvent(
                "trial_csv",
                $"source={source};rows={TrialConditions.Count};min={MinTrialNumber};max={MaxTrialNumber};duplicates={duplicates};gaps={gaps};invalid_lines={invalidLines}"));

            MissingData = false;
            OnDataLoaded?.Invoke();
        }
        else
        {
            string error = "CSV parsed but no valid trial rows found.";
            Debug.LogError($"[TrialLoader] {error}");
            MissingData = true;
            OnDataError?.Invoke(error);
        }
    }
}
