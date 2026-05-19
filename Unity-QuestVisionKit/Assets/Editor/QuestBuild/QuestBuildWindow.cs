using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace QuestBuild
{
    /// <summary>
    /// "Build Panel" — the editing surface for QuestBuildSettings and the place to
    /// kick off a build. Open via Quest Build ▸ Build Panel.
    /// </summary>
    public class QuestBuildWindow : EditorWindow
    {
        QuestBuildSettings _settings;
        QuestBuildReport _lastBuild;
        Vector2 _scroll;

        public static void ShowWindow()
        {
            var window = GetWindow<QuestBuildWindow>("Quest Build");
            window.minSize = new Vector2(440, 460);
            window.Show();
        }

        void OnEnable()
        {
            _settings = QuestBuildSettings.LoadOrCreate();
            ReloadReport();
        }

        // Called ~10x/sec even when unfocused — keeps the Last build panel current.
        void OnInspectorUpdate()
        {
            ReloadReport();
            Repaint();
        }

        void ReloadReport()
        {
            try
            {
                var path = QuestBuildSettings.ProjectRelative("UserSettings", "last-build.json");
                _lastBuild = File.Exists(path)
                    ? JsonUtility.FromJson<QuestBuildReport>(File.ReadAllText(path))
                    : null;
            }
            catch
            {
                _lastBuild = null;
            }
        }

        void OnGUI()
        {
            if (_settings == null) _settings = QuestBuildSettings.LoadOrCreate();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Quest APK Build", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("Output folder", EditorStyles.miniBoldLabel);
            DrawFolderField(ref _settings.outputFolder, "Select APK output folder");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Cloud mirror folder (optional)", EditorStyles.miniBoldLabel);
            DrawFolderField(ref _settings.cloudMirrorFolder, "Select cloud-synced folder");

            EditorGUILayout.Space();
            _settings.fileNamePrefix = EditorGUILayout.TextField("File name prefix", _settings.fileNamePrefix);
            _settings.developmentBuild = EditorGUILayout.Toggle("Development build", _settings.developmentBuild);
            using (new EditorGUI.DisabledScope(!_settings.developmentBuild))
                _settings.connectProfiler = EditorGUILayout.Toggle("Connect profiler", _settings.connectProfiler);

            if (EditorGUI.EndChangeCheck())
                _settings.Save();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Saved to UserSettings/QuestBuildSettings.json — per-machine, not committed to git.",
                MessageType.None);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(EditorApplication.isCompiling || Application.isPlaying))
            {
                if (GUILayout.Button("Build APK Now", GUILayout.Height(38)))
                {
                    _settings.Save();
                    QuestBuilder.BuildAPK();
                }
            }
            if (EditorApplication.isCompiling)
                EditorGUILayout.HelpBox("Editor is compiling — wait before building.", MessageType.Info);

            EditorGUILayout.Space();
            DrawLastBuild();

            EditorGUILayout.EndScrollView();
        }

        void DrawFolderField(ref string value, string title)
        {
            EditorGUILayout.BeginHorizontal();
            value = EditorGUILayout.TextField(value ?? "");
            if (GUILayout.Button("Browse…", GUILayout.Width(72)))
            {
                var start = !string.IsNullOrEmpty(value) && Directory.Exists(value) ? value : "";
                var picked = EditorUtility.OpenFolderPanel(title, start, "");
                if (!string.IsNullOrEmpty(picked))
                {
                    value = picked.Replace('/', Path.DirectorySeparatorChar);
                    GUI.changed = true;
                }
            }
            EditorGUILayout.EndHorizontal();

            if (string.IsNullOrWhiteSpace(value))
                EditorGUILayout.HelpBox("Not set.", MessageType.Warning);
            else if (!Directory.Exists(value))
                EditorGUILayout.HelpBox("Folder does not exist yet — it will be created on build.",
                    MessageType.Info);
        }

        void DrawLastBuild()
        {
            EditorGUILayout.LabelField("Last build", EditorStyles.boldLabel);
            if (_lastBuild == null)
            {
                EditorGUILayout.HelpBox("No build recorded yet.", MessageType.None);
                return;
            }

            var ok = _lastBuild.status == "succeeded";
            EditorGUILayout.HelpBox(
                $"Status:  {_lastBuild.status.ToUpperInvariant()}\n" +
                $"When:    {_lastBuild.timestampLocal}  ({_lastBuild.durationSeconds}s)\n" +
                $"APK:     {_lastBuild.apkFileName}\n" +
                $"Size:    {_lastBuild.sizeMB} MB\n" +
                $"Commit:  {Display(_lastBuild.gitCommit)}{(_lastBuild.gitDirty ? " (dirty)" : "")}" +
                $"  [{Display(_lastBuild.gitBranch)}]\n" +
                $"Dev build: {_lastBuild.developmentBuild}\n" +
                $"Errors: {_lastBuild.totalErrors}   Warnings: {_lastBuild.totalWarnings}",
                ok ? MessageType.Info : MessageType.Error);

            if (!string.IsNullOrEmpty(_lastBuild.errorSummary))
                EditorGUILayout.HelpBox(_lastBuild.errorSummary, ok ? MessageType.Warning : MessageType.Error);

            if (!string.IsNullOrEmpty(_lastBuild.cloudPath))
                EditorGUILayout.LabelField("Mirrored to cloud-synced folder.", EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            var apkExists = !string.IsNullOrEmpty(_lastBuild.apkPath) && File.Exists(_lastBuild.apkPath);
            using (new EditorGUI.DisabledScope(!apkExists))
            {
                if (GUILayout.Button("Reveal APK in Explorer"))
                    EditorUtility.RevealInFinder(_lastBuild.apkPath);
            }
            if (GUILayout.Button("Refresh"))
                ReloadReport();
            EditorGUILayout.EndHorizontal();
        }

        static string Display(string s) => string.IsNullOrEmpty(s) ? "—" : s;
    }
}
