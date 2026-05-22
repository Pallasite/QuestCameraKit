using System;
using System.IO;
using System.Linq;
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
        SessionsIndex _sessionsIndex;
        LastPull _lastPull;
        Vector2 _scroll;

        // Phase 5 deploy tracking: only deploy when last-build.timestampLocal advances.
        // _forceDeployNextBuild is a one-shot set by the "Build + Deploy" button.
        string _lastDeployedTimestamp;
        bool _forceDeployNextBuild;

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

        // Called ~10x/sec even when unfocused — keeps Last build / Sessions panels current.
        void OnInspectorUpdate()
        {
            ReloadReport();
            LoadSessionsIndex();
            LoadLastPull();
            MaybeAutoDeploy();
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

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Session logs", EditorStyles.miniBoldLabel);
            _settings.mirrorSessionLogs = EditorGUILayout.Toggle(
                new GUIContent("Mirror to cloud folder",
                    "Copy pulled session logs alongside the APK in the cloud-mirror folder."),
                _settings.mirrorSessionLogs);
            _settings.pullCleanupDevice = EditorGUILayout.Toggle(
                new GUIContent("Cleanup device after pull",
                    "Delete session files from the headset after a successful Pull-Sessions run."),
                _settings.pullCleanupDevice);
            var maxSessions = EditorGUILayout.IntField(
                new GUIContent("Max sessions kept on device",
                    "SessionLogger trims older logs beyond this count at launch."),
                _settings.maxSessionsRetainedOnDevice);
            _settings.maxSessionsRetainedOnDevice = Mathf.Max(1, maxSessions);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Deploy", EditorStyles.miniBoldLabel);
            _settings.autoDeployOnBuildSuccess = EditorGUILayout.Toggle(
                new GUIContent("Auto-deploy after Build APK Now",
                    "When on, a successful Build APK Now press triggers Deploy-Latest.ps1 (pre-pull + install) if a device is connected. The 'Build + Deploy' button always deploys regardless of this toggle."),
                _settings.autoDeployOnBuildSuccess);
            _settings.launchAfterDeploy = EditorGUILayout.Toggle(
                new GUIContent("Auto-launch on headset after install",
                    "When on, Deploy-Latest.ps1 launches the app on the headset after install (adb shell monkey)."),
                _settings.launchAfterDeploy);

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
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Build + Deploy", GUILayout.Height(28)))
                    {
                        _settings.Save();
                        // One-shot: the OnInspectorUpdate hook deploys when this build's
                        // succeeded report lands, regardless of the auto-deploy toggle.
                        _forceDeployNextBuild = true;
                        QuestBuilder.BuildAPK();
                    }
                    if (GUILayout.Button("Deploy Latest APK", GUILayout.Height(28)))
                    {
                        // Skip the build entirely - push whatever .apk is newest in outputFolder.
                        bool launch = _settings != null && _settings.launchAfterDeploy;
                        RunToolScript("Deploy-Latest.ps1", launch ? "-Launch" : "");
                    }
                }
            }
            if (EditorApplication.isCompiling)
                EditorGUILayout.HelpBox("Editor is compiling — wait before building.", MessageType.Info);

            EditorGUILayout.Space();
            DrawLastBuild();

            EditorGUILayout.Space();
            DrawSessions();

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

        // ---- Sessions (Phase 2) -----------------------------------------------------

        void LoadSessionsIndex()
        {
            _sessionsIndex = null;
            var dir = SessionsDir();
            if (dir == null) return;
            var indexPath = Path.Combine(dir, "sessions-index.json");
            if (!File.Exists(indexPath)) return;
            try { _sessionsIndex = JsonUtility.FromJson<SessionsIndex>(File.ReadAllText(indexPath)); }
            catch { _sessionsIndex = null; }
        }

        void LoadLastPull()
        {
            _lastPull = null;
            try
            {
                var path = QuestBuildSettings.ProjectRelative("UserSettings", "last-pull.json");
                if (File.Exists(path))
                    _lastPull = JsonUtility.FromJson<LastPull>(File.ReadAllText(path));
            }
            catch { _lastPull = null; }
        }

        string SessionsDir()
        {
            if (_lastBuild == null
                || string.IsNullOrEmpty(_lastBuild.apkPath)
                || string.IsNullOrEmpty(_lastBuild.apkFileName))
                return null;
            var dir = Path.GetDirectoryName(_lastBuild.apkPath);
            var baseName = Path.GetFileNameWithoutExtension(_lastBuild.apkFileName);
            return Path.Combine(dir, baseName + ".sessions");
        }

        void DrawSessions()
        {
            EditorGUILayout.LabelField("Sessions", EditorStyles.boldLabel);

            if (_lastBuild == null)
            {
                EditorGUILayout.HelpBox(
                    "No build recorded yet — sessions appear here once you've built and pulled logs from the device.",
                    MessageType.None);
                return;
            }

            int sessionCount = _sessionsIndex?.sessions?.Length ?? 0;
            int totalErrors = 0, totalExceptions = 0;
            string lastSessionUtc = null;
            if (_sessionsIndex?.sessions != null)
            {
                foreach (var s in _sessionsIndex.sessions)
                {
                    totalErrors += s.errorCount;
                    totalExceptions += s.exceptionCount;
                    if (lastSessionUtc == null
                        || string.Compare(s.sessionStartUtc, lastSessionUtc, StringComparison.Ordinal) > 0)
                        lastSessionUtc = s.sessionStartUtc;
                }
            }

            EditorGUILayout.HelpBox(
                $"Sessions for this APK: {sessionCount}\n" +
                $"Last session:  {Display(lastSessionUtc)}\n" +
                $"Totals — errors: {totalErrors}   exceptions: {totalExceptions}",
                totalExceptions > 0 ? MessageType.Warning : MessageType.None);

            EditorGUILayout.LabelField(BuildLastPullLine(), EditorStyles.miniLabel);

            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Pull Sessions from Device"))
                    RunToolScript("Pull-Sessions.ps1");
                if (GUILayout.Button("Start Live Capture"))
                    RunToolScript("Start-LiveSession.ps1");
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                var sessionsDir = SessionsDir();
                using (new EditorGUI.DisabledScope(sessionsDir == null || !Directory.Exists(sessionsDir)))
                {
                    if (GUILayout.Button("Open Sessions Folder"))
                        EditorUtility.RevealInFinder(sessionsDir);
                }
                var latest = LatestSessionLogPath();
                using (new EditorGUI.DisabledScope(latest == null))
                {
                    if (GUILayout.Button("Reveal Latest Session Log"))
                        EditorUtility.RevealInFinder(latest);
                }
            }
        }

        string BuildLastPullLine()
        {
            if (_lastPull == null || string.IsNullOrEmpty(_lastPull.timestampUtc))
                return "No device pull recorded yet.";
            if (DateTime.TryParse(_lastPull.timestampUtc, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var pulledAt))
            {
                var age = DateTime.UtcNow - pulledAt.ToUniversalTime();
                var human = age.TotalMinutes < 60
                    ? $"{Math.Max(0, (int)age.TotalMinutes)} min ago"
                    : age.TotalHours < 24
                        ? $"{(int)age.TotalHours} h ago"
                        : $"{(int)age.TotalDays} d ago";
                var line = $"Last device pull: {human}  (matched {_lastPull.pulled}, unmatched {_lastPull.unmatched})";
                if (age.TotalHours > 24) line += "  — consider pulling fresh sessions.";
                return line;
            }
            return $"Last device pull: {_lastPull.timestampUtc}";
        }

        string LatestSessionLogPath()
        {
            var dir = SessionsDir();
            if (dir == null || !Directory.Exists(dir)) return null;
            try
            {
                // Search recursively so we catch both the new layout
                // (<sessionId>/session.log) and legacy flat <id>.log files.
                var info = new DirectoryInfo(dir);
                var newest = info.GetFiles("*.log", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                return newest?.FullName;
            }
            catch { return null; }
        }

        static void RunToolScript(string scriptName, string extraArgs = "")
        {
            try
            {
                var repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
                var script = Path.Combine(repoRoot, "Tools", scriptName);
                if (!File.Exists(script))
                {
                    EditorUtility.DisplayDialog("Quest Build",
                        $"Tool script not found:\n{script}", "OK");
                    return;
                }
                var argString = $"-NoExit -ExecutionPolicy Bypass -File \"{script}\"";
                if (!string.IsNullOrEmpty(extraArgs)) argString += " " + extraArgs;
                var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", argString)
                {
                    WorkingDirectory = repoRoot,
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Quest Build",
                    $"Could not start PowerShell:\n{e.Message}", "OK");
            }
        }

        // Phase 5 — auto-deploy hook.
        // Watches last-build.json's timestampLocal for a new succeeded build, then spawns
        // Deploy-Latest.ps1 when either the autoDeployOnBuildSuccess setting is on or the
        // one-shot "Build + Deploy" force flag was set this click. Seeds the watermark on
        // first observation so we don't surprise-deploy an existing report.
        void MaybeAutoDeploy()
        {
            if (_lastBuild == null) return;
            if (string.IsNullOrEmpty(_lastBuild.timestampLocal)) return;

            bool isFirstObservation = _lastDeployedTimestamp == null;
            bool isNew = !isFirstObservation
                && string.Compare(_lastBuild.timestampLocal, _lastDeployedTimestamp, StringComparison.Ordinal) > 0;

            // Seed-only path: first observation with no force-deploy intent → just record
            // the watermark and do nothing. Avoids re-deploying historical builds when the
            // window opens.
            if (isFirstObservation && !_forceDeployNextBuild)
            {
                _lastDeployedTimestamp = _lastBuild.timestampLocal;
                return;
            }

            if (!isFirstObservation && !isNew) return;

            bool isSuccess = _lastBuild.status == "succeeded";
            bool autoDeploy = _settings != null && _settings.autoDeployOnBuildSuccess;
            bool shouldDeploy = isSuccess && (_forceDeployNextBuild || autoDeploy);

            // Always advance the watermark and consume the force flag, even on failed builds
            // or when we decide not to deploy. Prevents a stale force flag from biting later.
            _lastDeployedTimestamp = _lastBuild.timestampLocal;
            _forceDeployNextBuild = false;

            if (!shouldDeploy) return;

            bool launch = _settings != null && _settings.launchAfterDeploy;
            RunToolScript("Deploy-Latest.ps1", launch ? "-Launch" : "");
        }

        [Serializable]
        class SessionsIndex
        {
            public SessionSummary[] sessions = Array.Empty<SessionSummary>();
            public string lastUpdatedUtc = "";
        }

        [Serializable]
        class SessionSummary
        {
            public string sessionId = "";
            public string sessionStartUtc = "";
            public string sessionEndUtc = "";
            public bool cleanExit;
            public double durationSec;
            public int lineCount;
            public int warningCount;
            public int errorCount;
            public int exceptionCount;
        }

        [Serializable]
        class LastPull
        {
            public string timestampUtc = "";
            public int pulled;
            public int unmatched;
            public string devicePath = "";
        }
    }
}
