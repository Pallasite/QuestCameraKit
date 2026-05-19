using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace QuestBuild
{
    /// <summary>
    /// Quest APK build pipeline. Entry point is <see cref="BuildAPK"/>, callable from the
    /// Unity menu, from the Build Panel window, from Claude via MCP (reflection-method-call),
    /// or from a batchmode runner via -executeMethod QuestBuild.QuestBuilder.BuildAPK.
    /// </summary>
    public static class QuestBuilder
    {
        const string MenuRoot = "Quest Build/";

        [MenuItem(MenuRoot + "Build Panel", false, 0)]
        public static void OpenBuildPanel() => QuestBuildWindow.ShowWindow();

        [MenuItem(MenuRoot + "Build APK Now", false, 20)]
        public static void BuildAPK()
        {
            if (Application.isBatchMode)
            {
                // Runner / CLI: build synchronously and exit with a status code.
                RunBuild();
            }
            else
            {
                // Interactive (menu / MCP): defer so the triggering call returns promptly.
                // Result is observable via UserSettings/last-build.json.
                Debug.Log("[QuestBuild] Build queued — starting on next editor tick.");
                EditorApplication.delayCall += RunBuild;
            }
        }

        [MenuItem(MenuRoot + "Open Output Folder", false, 21)]
        public static void OpenOutputFolder()
        {
            var folder = QuestBuildSettings.LoadOrCreate().outputFolder;
            if (string.IsNullOrWhiteSpace(folder)) return;
            Directory.CreateDirectory(folder);
            EditorUtility.RevealInFinder(folder);
        }

        static void RunBuild()
        {
            var settings = QuestBuildSettings.LoadOrCreate();
            var report = new QuestBuildReport
            {
                developmentBuild = settings.developmentBuild,
                unityVersion = Application.unityVersion,
            };
            var startedAt = DateTime.Now;
            report.timestampLocal = startedAt.ToString("yyyy-MM-dd HH:mm:ss");
            var stopwatch = Stopwatch.StartNew();

            try
            {
                if (string.IsNullOrWhiteSpace(settings.outputFolder))
                    throw new Exception("Output folder is not set. Open Quest Build ▸ Build Panel.");

                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                {
                    Debug.Log("[QuestBuild] Active build target is not Android — switching.");
                    EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
                }

                var scenes = EditorBuildSettings.scenes
                    .Where(s => s.enabled && !string.IsNullOrEmpty(s.path))
                    .Select(s => s.path)
                    .ToArray();
                if (scenes.Length == 0)
                    throw new Exception("No enabled scenes in File ▸ Build Settings.");
                report.scenes = scenes;

                var git = GitInfo.Resolve();
                report.gitCommit = git.shortSha;
                report.gitBranch = git.branch;
                report.gitDirty = git.dirty;

                var fileName = BuildFileName(settings, git, startedAt);
                report.apkFileName = fileName;

                Directory.CreateDirectory(settings.outputFolder);
                var apkPath = Path.Combine(settings.outputFolder, fileName);
                report.apkPath = apkPath;

                EditorUserBuildSettings.buildAppBundle = false; // APK, not AAB

                var options = BuildOptions.None;
                if (settings.developmentBuild)
                {
                    options |= BuildOptions.Development | BuildOptions.AllowDebugging;
                    if (settings.connectProfiler)
                        options |= BuildOptions.ConnectWithProfiler;
                }

                Debug.Log($"[QuestBuild] Building {fileName} — {scenes.Length} scene(s), dev={settings.developmentBuild}");

                var buildPlayerOptions = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = apkPath,
                    target = BuildTarget.Android,
                    targetGroup = BuildTargetGroup.Android,
                    options = options,
                };

                var buildReport = BuildPipeline.BuildPlayer(buildPlayerOptions);
                stopwatch.Stop();

                var summary = buildReport.summary;
                report.durationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
                report.totalErrors = summary.totalErrors;
                report.totalWarnings = summary.totalWarnings;

                if (summary.result == BuildResult.Succeeded && File.Exists(apkPath))
                {
                    report.status = "succeeded";
                    report.sizeMB = Math.Round(new FileInfo(apkPath).Length / 1048576.0, 1);
                    MirrorToCloud(settings, apkPath, fileName, report);
                    Debug.Log($"[QuestBuild] BUILD SUCCEEDED — {apkPath} " +
                              $"({report.sizeMB} MB in {report.durationSeconds}s)");
                }
                else
                {
                    report.status = "failed";
                    report.errorSummary = SummariseErrors(buildReport);
                    Debug.LogError($"[QuestBuild] BUILD FAILED — {report.errorSummary}");
                }
            }
            catch (Exception e)
            {
                stopwatch.Stop();
                report.status = "failed";
                report.durationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
                report.errorSummary = e.Message;
                Debug.LogError($"[QuestBuild] BUILD FAILED — {e.Message}\n{e.StackTrace}");
            }

            WriteReport(report);

            if (Application.isBatchMode)
                EditorApplication.Exit(report.status == "succeeded" ? 0 : 1);
        }

        static string BuildFileName(QuestBuildSettings settings, GitInfo git, DateTime startedAt)
        {
            var prefix = string.IsNullOrWhiteSpace(settings.fileNamePrefix)
                ? Application.productName
                : settings.fileNamePrefix;

            var sb = new StringBuilder();
            sb.Append(Sanitize(prefix));
            sb.Append('_').Append(Sanitize(PlayerSettings.bundleVersion));
            sb.Append('_').Append(startedAt.ToString("yyyy-MM-dd_HHmm"));
            if (!string.IsNullOrEmpty(git.shortSha))
            {
                sb.Append('_').Append(git.shortSha);
                if (git.dirty) sb.Append("-dirty");
            }
            if (settings.developmentBuild) sb.Append("_dev");
            sb.Append(".apk");
            return sb.ToString();
        }

        static void MirrorToCloud(QuestBuildSettings settings, string apkPath, string fileName,
            QuestBuildReport report)
        {
            if (string.IsNullOrWhiteSpace(settings.cloudMirrorFolder)) return;
            try
            {
                Directory.CreateDirectory(settings.cloudMirrorFolder);
                var cloudPath = Path.Combine(settings.cloudMirrorFolder, fileName);
                File.Copy(apkPath, cloudPath, true);
                report.cloudPath = cloudPath;
                Debug.Log($"[QuestBuild] Mirrored to {cloudPath}");
            }
            catch (Exception e)
            {
                report.errorSummary = $"APK built OK, but cloud mirror copy failed: {e.Message}";
                Debug.LogWarning($"[QuestBuild] Cloud mirror copy failed: {e.Message}");
            }
        }

        static string SummariseErrors(BuildReport buildReport)
        {
            var errors = new List<string>();
            foreach (var step in buildReport.steps)
            {
                foreach (var msg in step.messages)
                {
                    if (msg.type == LogType.Error || msg.type == LogType.Exception)
                    {
                        errors.Add(msg.content);
                        if (errors.Count >= 10) break;
                    }
                }
                if (errors.Count >= 10) break;
            }
            return errors.Count > 0
                ? string.Join(" | ", errors)
                : $"Build result: {buildReport.summary.result}";
        }

        static void WriteReport(QuestBuildReport report)
        {
            var json = JsonUtility.ToJson(report, true);

            try
            {
                var lastBuildPath = QuestBuildSettings.ProjectRelative("UserSettings", "last-build.json");
                Directory.CreateDirectory(Path.GetDirectoryName(lastBuildPath));
                File.WriteAllText(lastBuildPath, json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QuestBuild] Could not write last-build.json: {e.Message}");
            }

            if (!string.IsNullOrEmpty(report.apkPath))
            {
                try { File.WriteAllText(report.apkPath + ".build.json", json); }
                catch (Exception e)
                {
                    Debug.LogWarning($"[QuestBuild] Could not write build sidecar: {e.Message}");
                }
            }
        }

        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "0";
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '-');
            return s.Replace(' ', '-');
        }
    }

    /// <summary>Snapshot of the repository state, captured for build traceability.</summary>
    public struct GitInfo
    {
        public string shortSha;
        public string branch;
        public bool dirty;

        public static GitInfo Resolve()
        {
            var info = new GitInfo();
            try
            {
                var repoDir = QuestBuildSettings.ProjectRelative();
                info.shortSha = RunGit("rev-parse --short HEAD", repoDir);
                info.branch = RunGit("rev-parse --abbrev-ref HEAD", repoDir);
                info.dirty = !string.IsNullOrEmpty(RunGit("status --porcelain", repoDir));
            }
            catch
            {
                // git unavailable — leave fields empty, build proceeds without commit info
            }
            return info;
        }

        static string RunGit(string args, string workingDir)
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi))
            {
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                return output.Trim();
            }
        }
    }
}
