using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace QuestBuild
{
    /// <summary>
    /// Per-launch session logger. Writes everything Unity logs (including from
    /// background threads) to <c>persistentDataPath/Sessions/{sessionId}.log</c>
    /// with a JSON sidecar containing the build identity and runtime counters.
    ///
    /// The PC-side <c>Tools/Pull-Sessions.ps1</c> pulls these files when the
    /// headset is next connected and files them next to the matching APK.
    ///
    /// Bootstrapping uses <see cref="RuntimeInitializeOnLoadMethodAttribute"/>
    /// + a hidden DontDestroyOnLoad host so the logger survives scene loads.
    /// </summary>
    public static class SessionLogger
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            try
            {
                if (Host != null) return; // domain reload safety
                var go = new GameObject("[QuestBuild.SessionLogger]")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                UnityEngine.Object.DontDestroyOnLoad(go);
                Host = go.AddComponent<SessionLoggerHost>();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QuestBuild] SessionLogger bootstrap failed: {e.Message}");
            }
        }

        internal static SessionLoggerHost Host;
    }

    /// <summary>
    /// Hidden MonoBehaviour that owns the writer, hooks Unity log delivery, and
    /// flushes/heartbeats the sidecar so even crash-killed sessions leave usable
    /// metadata behind.
    /// </summary>
    internal class SessionLoggerHost : MonoBehaviour
    {
        const float HeartbeatSeconds = 30f;

        readonly object _writerLock = new object();
        StreamWriter _writer;
        string _logPath;
        string _sidecarPath;
        SessionSidecar _sidecar;
        DateTime _startUtc;
        float _heartbeatTimer;
        bool _initialized;

        void Awake()
        {
            try
            {
                Initialize();
                _initialized = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[QuestBuild] SessionLogger init failed: {e.Message}");
            }
        }

        void Initialize()
        {
            var dir = Path.Combine(Application.persistentDataPath, "Sessions");
            Directory.CreateDirectory(dir);

            var build = BuildInfo.Load();
            _startUtc = DateTime.UtcNow;
            var sessionId = $"{_startUtc:yyyy-MM-dd_HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";

            _logPath = Path.Combine(dir, sessionId + ".log");
            _sidecarPath = Path.Combine(dir, sessionId + ".json");

            _sidecar = new SessionSidecar
            {
                sessionId = sessionId,
                apkBaseName = build.apkBaseName,
                packageName = build.packageName,
                gitSha = build.gitSha,
                gitBranch = build.gitBranch,
                dirty = build.dirty,
                bundleVersion = build.bundleVersion,
                buildTimestampUtc = build.buildTimestampUtc,
                sessionStartUtc = _startUtc.ToString("o"),
                sessionLastSeenUtc = _startUtc.ToString("o"),
                sessionEndUtc = "",
                cleanExit = false,
                durationSec = 0,
                unityVersion = Application.unityVersion,
                deviceModel = SystemInfo.deviceModel,
                osVersion = SystemInfo.operatingSystem,
                lineCount = 0,
                warningCount = 0,
                errorCount = 0,
                exceptionCount = 0,
            };
            WriteSidecar();

            _writer = new StreamWriter(_logPath, append: false) { AutoFlush = false };
            WriteHeader(build);

            Application.logMessageReceivedThreaded += OnLog;
            Application.quitting += OnQuitting;

            RollOldSessions(dir, Math.Max(1, build.maxSessionsRetainedOnDevice));
        }

        void WriteHeader(BuildInfo build)
        {
            lock (_writerLock)
            {
                _writer.WriteLine($"# session  {_sidecar.sessionId}");
                _writer.WriteLine($"# build    {build.apkBaseName} (sha {build.gitSha}{(build.dirty ? "-dirty" : "")}) v{build.bundleVersion}");
                _writer.WriteLine($"# device   {_sidecar.deviceModel} / {_sidecar.osVersion}");
                _writer.WriteLine($"# unity    {_sidecar.unityVersion}");
                _writer.WriteLine($"# start    {_sidecar.sessionStartUtc}");
                _writer.WriteLine();
            }
        }

        void OnLog(string condition, string stackTrace, LogType type)
        {
            lock (_writerLock)
            {
                if (_writer == null) return;
                try
                {
                    _sidecar.lineCount++;
                    switch (type)
                    {
                        case LogType.Warning: _sidecar.warningCount++; break;
                        case LogType.Assert:
                        case LogType.Error: _sidecar.errorCount++; break;
                        case LogType.Exception: _sidecar.exceptionCount++; break;
                    }
                    _writer.Write(DateTime.UtcNow.ToString("HH:mm:ss.fff"));
                    _writer.Write(" [");
                    _writer.Write(type);
                    _writer.Write("] ");
                    _writer.WriteLine(condition);
                    if ((type == LogType.Exception || type == LogType.Error)
                        && !string.IsNullOrEmpty(stackTrace))
                    {
                        _writer.WriteLine(stackTrace);
                    }
                }
                catch
                {
                    // Never let logging errors crash the host app.
                }
            }
        }

        void Update()
        {
            if (!_initialized) return;
            _heartbeatTimer += Time.unscaledDeltaTime;
            if (_heartbeatTimer >= HeartbeatSeconds)
            {
                _heartbeatTimer = 0f;
                Heartbeat();
            }
        }

        void Heartbeat()
        {
            lock (_writerLock)
            {
                try
                {
                    _writer?.Flush();
                    _sidecar.sessionLastSeenUtc = DateTime.UtcNow.ToString("o");
                    _sidecar.durationSec = Math.Round((DateTime.UtcNow - _startUtc).TotalSeconds, 1);
                    WriteSidecar();
                }
                catch { /* swallow */ }
            }
        }

        void OnApplicationPause(bool paused)
        {
            // Quest's actual "user put it down" signal — flush so the session isn't
            // lost if the OS suspends/kills the app afterwards.
            if (paused) Heartbeat();
        }

        void OnApplicationFocus(bool focused)
        {
            if (!focused) Heartbeat();
        }

        void OnQuitting() => CloseSession(cleanExit: true);

        void OnDestroy()
        {
            // Last-chance flush. Don't claim cleanExit here — OnDestroy fires in
            // many scenarios (scene unload, etc.); only Application.quitting is a
            // true clean shutdown signal.
            Heartbeat();
        }

        void CloseSession(bool cleanExit)
        {
            lock (_writerLock)
            {
                if (_writer == null) return;
                try
                {
                    var now = DateTime.UtcNow;
                    _sidecar.sessionEndUtc = now.ToString("o");
                    _sidecar.sessionLastSeenUtc = _sidecar.sessionEndUtc;
                    _sidecar.cleanExit = cleanExit;
                    _sidecar.durationSec = Math.Round((now - _startUtc).TotalSeconds, 1);
                    _writer.Flush();
                    _writer.Close();
                    _writer = null;
                    WriteSidecar();
                }
                catch { /* swallow */ }
            }
            try { Application.logMessageReceivedThreaded -= OnLog; } catch { }
            try { Application.quitting -= OnQuitting; } catch { }
        }

        void WriteSidecar()
        {
            try
            {
                File.WriteAllText(_sidecarPath, JsonUtility.ToJson(_sidecar, true));
            }
            catch { /* swallow */ }
        }

        static void RollOldSessions(string dir, int keep)
        {
            try
            {
                var info = new DirectoryInfo(dir);
                if (!info.Exists) return;
                var logs = info.GetFiles("*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Skip(keep)
                    .ToList();
                foreach (var log in logs)
                {
                    var sidecar = Path.ChangeExtension(log.FullName, ".json");
                    try { log.Delete(); } catch { }
                    try { if (File.Exists(sidecar)) File.Delete(sidecar); } catch { }
                }
            }
            catch { /* swallow */ }
        }
    }

    [Serializable]
    internal class SessionSidecar
    {
        public string sessionId = "";
        public string apkBaseName = "";
        public string packageName = "";
        public string gitSha = "";
        public string gitBranch = "";
        public bool dirty;
        public string bundleVersion = "";
        public string buildTimestampUtc = "";
        public string sessionStartUtc = "";
        public string sessionLastSeenUtc = "";
        public string sessionEndUtc = "";
        public bool cleanExit;
        public double durationSec;
        public string unityVersion = "";
        public string deviceModel = "";
        public string osVersion = "";
        public int lineCount;
        public int warningCount;
        public int errorCount;
        public int exceptionCount;
    }
}
