using System;
using System.IO;
using UnityEngine;

namespace QuestBuild
{
    /// <summary>
    /// Per-launch on-device folder shared by every writer that produces session
    /// artefacts (dev session log, experiment CSV, sample CSVs, etc.).
    ///
    /// <para>Layout on device:</para>
    /// <code>
    /// Application.persistentDataPath/
    ///   Sessions/
    ///     yyyy-MM-dd_HHmmss_xxxxxxxx/      &lt;-- this launch's SessionId
    ///       session.log                     dev Console capture
    ///       session.json                    dev sidecar (build identity + counters)
    ///       &lt;participantId&gt;_&lt;unixMs&gt;.csv    experiment CSV
    ///       apriltag_solver_comparison.csv  (if the sample is enabled this run)
    ///       ...                             any other per-session output
    /// </code>
    ///
    /// <para>Initialisation is lazy and thread-safe: whichever writer asks first
    /// resolves a single SessionId that the rest of the launch reuses. The dev
    /// SessionLogger typically gets there first via
    /// <c>[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]</c>; if it fails to
    /// initialise, the experiment SessionLogger's <c>OnEnable</c> triggers it.</para>
    /// </summary>
    public static class SessionPaths
    {
        const string SessionsRootFolderName = "Sessions";

        static readonly object _lock = new object();
        static string _sessionId;
        static string _sessionFolder;

        /// <summary>
        /// The folder name for this launch's bundle. Format: <c>yyyy-MM-dd_HHmmss_xxxxxxxx</c>
        /// (UTC stamp + 8 hex). Stable for the entire app lifetime once first read.
        /// </summary>
        public static string SessionId
        {
            get { EnsureInitialised(); return _sessionId; }
        }

        /// <summary>
        /// Absolute path: <c>persistentDataPath/Sessions/&lt;SessionId&gt;</c>.
        /// The directory is created on first read.
        /// </summary>
        public static string SessionFolder
        {
            get { EnsureFolder(); return _sessionFolder; }
        }

        /// <summary>Root of all session folders on this device (<c>persistentDataPath/Sessions</c>).</summary>
        public static string SessionsRoot => Path.Combine(Application.persistentDataPath, SessionsRootFolderName);

        /// <summary>
        /// Convenience: <c>persistentDataPath/Sessions/&lt;SessionId&gt;/&lt;tail...&gt;</c>.
        /// Ensures the session folder exists before returning.
        /// </summary>
        public static string Combine(params string[] tail)
        {
            EnsureFolder();
            if (tail == null || tail.Length == 0) return _sessionFolder;
            var parts = new string[tail.Length + 1];
            parts[0] = _sessionFolder;
            Array.Copy(tail, 0, parts, 1, tail.Length);
            return Path.Combine(parts);
        }

        /// <summary>Ensures the session folder exists on disk. Safe to call repeatedly.</summary>
        public static void EnsureFolder()
        {
            EnsureInitialised();
            try { Directory.CreateDirectory(_sessionFolder); }
            catch (Exception e) { Debug.LogWarning($"[SessionPaths] Could not create {_sessionFolder}: {e.Message}"); }
        }

        static void EnsureInitialised()
        {
            if (_sessionId != null) return;
            lock (_lock)
            {
                if (_sessionId != null) return;
                var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HHmmss");
                var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
                _sessionId = stamp + "_" + suffix;
                _sessionFolder = Path.Combine(Application.persistentDataPath, SessionsRootFolderName, _sessionId);
            }
        }
    }
}
