using System;
using System.IO;
using UnityEngine;

namespace QuestBuild
{
    /// <summary>
    /// Per-machine build configuration. Persisted to UserSettings/QuestBuildSettings.json,
    /// which Unity's .gitignore already excludes — so each machine keeps its own paths.
    /// </summary>
    [Serializable]
    public class QuestBuildSettings
    {
        public string outputFolder = "";
        public string cloudMirrorFolder = "";
        public bool developmentBuild = true;
        public bool connectProfiler = false;
        public string fileNamePrefix = "";

        public static string SettingsPath => ProjectRelative("UserSettings", "QuestBuildSettings.json");

        public static string ProjectRelative(params string[] parts)
        {
            // Application.dataPath is <project>/Assets; the project root is its parent.
            var full = new string[parts.Length + 2];
            full[0] = Application.dataPath;
            full[1] = "..";
            Array.Copy(parts, 0, full, 2, parts.Length);
            return Path.GetFullPath(Path.Combine(full));
        }

        public static QuestBuildSettings LoadOrCreate()
        {
            var path = SettingsPath;
            if (File.Exists(path))
            {
                try
                {
                    var loaded = JsonUtility.FromJson<QuestBuildSettings>(File.ReadAllText(path));
                    if (loaded != null) return loaded;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[QuestBuild] Could not parse {path} ({e.Message}). Using defaults.");
                }
            }

            var settings = new QuestBuildSettings();
            settings.ApplyProjectDefaults();
            settings.Save();
            Debug.Log($"[QuestBuild] Created build settings at {path}");
            return settings;
        }

        // Sensible, project-agnostic defaults — applied only when the settings file is
        // first created, so the pipeline works out of the box in any Unity project.
        void ApplyProjectDefaults()
        {
            if (string.IsNullOrEmpty(outputFolder))
                outputFolder = ProjectRelative("Builds");
            if (string.IsNullOrEmpty(fileNamePrefix))
                fileNamePrefix = Application.productName;
        }

        public void Save()
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(this, true));
        }
    }

    /// <summary>Result of a build, written to UserSettings/last-build.json and an APK sidecar.</summary>
    [Serializable]
    public class QuestBuildReport
    {
        public string status = "unknown"; // succeeded | failed | unknown
        public string timestampLocal = "";
        public double durationSeconds;
        public string apkFileName = "";
        public string apkPath = "";
        public string cloudPath = "";
        public double sizeMB;
        public string gitCommit = "";
        public string gitBranch = "";
        public bool gitDirty;
        public bool developmentBuild;
        public string[] scenes = Array.Empty<string>();
        public string unityVersion = "";
        public int totalErrors;
        public int totalWarnings;
        public string errorSummary = "";
    }
}
