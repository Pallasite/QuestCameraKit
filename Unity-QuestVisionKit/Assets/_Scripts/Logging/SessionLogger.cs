using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using QuestBuild;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Singleton MonoBehaviour that writes a single wide-and-sparse CSV per session.
/// Designed for the gait research anchor-drift experiment: events accumulate at
/// modest rates (low tens of Hz peak across all sources), are produced on the
/// main thread, and drain through a background writer thread so the per-frame
/// cost is just an enqueue.
///
/// Lifecycle:
///   - Awake claims the singleton slot and sets DontDestroyOnLoad on the host GO.
///   - OnEnable opens <c>Application.persistentDataPath/&lt;participantId&gt;_&lt;unixMs&gt;.csv</c>
///     and starts the writer thread, then emits a <c>session_start</c> row.
///   - OnDisable / OnApplicationQuit emit a closing <c>session_end</c> /
///     <c>application_quit</c> row, signal the writer to drain, and join.
///
/// Crash resilience: the writer flushes every <see cref="flushIntervalSeconds"/>,
/// so a force-quit loses at most that window. All I/O exceptions on the writer
/// thread are caught and logged as warnings — logging never crashes the experiment.
///
/// Timestamping: events capture <see cref="Time.realtimeSinceStartupAsDouble"/>
/// and <see cref="Time.frameCount"/> at creation time (main thread), so the
/// writer only needs to serialize already-populated <see cref="LogEvent"/>
/// instances.
///
/// See SessionLoggerSchema.md for the column contract and ADB pull workflow.
/// </summary>
[DisallowMultipleComponent]
public sealed class SessionLogger : MonoBehaviour
{
    public static SessionLogger Instance { get; private set; }

    [Header("Identity")]
    [Tooltip("Used in the output filename: <participantId>_<unixMs>.csv. Set per session.")]
    [SerializeField] private string participantId = "P000";

    [Header("Writer")]
    [Tooltip("Disables logging without removing the component. Useful for pre-session warmup.")]
    [SerializeField] private bool enableLogging = true;

    [Tooltip("Background writer drains the queue continuously; flushes to disk every N seconds. " +
             "A force-quit loses at most this window. 2s is the spec default.")]
    [SerializeField, Range(0.5f, 5f)] private float flushIntervalSeconds = 2f;

    [Tooltip("Free-form notes appended to the session_start detail field. Useful for participant/condition tagging.")]
    [SerializeField] private string sessionHeaderNotes = "";

    private readonly ConcurrentQueue<LogEvent> _queue = new ConcurrentQueue<LogEvent>();
    private Thread _writerThread;
    private volatile bool _running;
    private volatile bool _flushRequested;
    private string _resolvedPath;
    private string _participantSource = "inspector";
    private double _sessionStartTime;
    private long _enqueuedCount;
    private long _writtenCount;

    /// <summary>Seconds since this session's <see cref="OnEnable"/>. Main-thread only (reads <see cref="Time.realtimeSinceStartupAsDouble"/>).</summary>
    public double NowSession => Time.realtimeSinceStartupAsDouble - _sessionStartTime;
    public string ResolvedPath => _resolvedPath;
    public long EnqueuedCount => Interlocked.Read(ref _enqueuedCount);
    public long WrittenCount => Interlocked.Read(ref _writtenCount);
    public bool IsRunning => _running;
    public string ParticipantId => participantId;

    /// <summary>Main-thread entry point. Drops the event if the writer isn't running.</summary>
    public void Enqueue(LogEvent e)
    {
        if (!_running || e == null) return;
        _queue.Enqueue(e);
        Interlocked.Increment(ref _enqueuedCount);
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning($"[SessionLogger] Duplicate instance on {gameObject.name}; destroying this one.");
            Destroy(this);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()
    {
        if (!enableLogging)
        {
            Debug.Log("[SessionLogger] enableLogging is false; logger inactive.");
            return;
        }

        try
        {
            // Per-participant override, mirroring TrialLoader's runtime-push
            // pattern: `adb push participant.txt` next to trial_conditions.csv.
            // First non-empty line wins; absent file = Inspector value as before.
            TryApplyParticipantFileOverride();

            var unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var fileName = $"{SanitizeForFilename(participantId)}_{unixMs}.csv";
            // Route through SessionPaths so the CSV lands inside the per-launch
            // session folder alongside the dev log + any sample CSVs from this run.
            _resolvedPath = SessionPaths.Combine(fileName);
            _sessionStartTime = Time.realtimeSinceStartupAsDouble;
            _running = true;
            _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "SessionLoggerWriter" };
            _writerThread.Start();

            var headerDetail = string.Format(CultureInfo.InvariantCulture,
                "build={0};scene={1};participant={2};participant_source={3};unix_ms={4};schema_version={5};flush_interval_s={6};notes={7}",
                Application.version,
                SceneManager.GetActiveScene().name,
                participantId,
                _participantSource,
                unixMs,
                LogEvent.CurrentSchemaVersion,
                flushIntervalSeconds.ToString("F2", CultureInfo.InvariantCulture),
                sessionHeaderNotes ?? "");

            Enqueue(LogEvent.SessionEvent("session_start", headerDetail));
            Debug.Log($"[SessionLogger] Logging to {_resolvedPath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[SessionLogger] Failed to start logger: {e.Message}");
            _running = false;
        }
    }

    private void OnDisable()
    {
        if (!_running) return;
        Enqueue(LogEvent.SessionEvent("session_end"));
        _running = false;
        try
        {
            _writerThread?.Join(TimeSpan.FromSeconds(5));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SessionLogger] Writer thread join error (swallowed): {e.Message}");
        }
    }

    private void OnApplicationQuit()
    {
        if (!_running) return;
        Enqueue(LogEvent.SessionEvent("application_quit"));
        _running = false;
        try { _writerThread?.Join(TimeSpan.FromSeconds(3)); }
        catch { /* shutdown best-effort */ }
    }

    /// <summary>
    /// On Quest the OS pauses (rather than quits) when the headset is doffed —
    /// the writer's flush window is exactly the data that used to go missing at
    /// session end. On pause: mark the moment in the CSV, ask the writer to
    /// flush now, and give it a bounded moment to drain before Android may
    /// suspend our threads. Best-effort: shrinks the loss window, cannot
    /// guarantee zero on a hard suspend.
    /// </summary>
    private void OnApplicationPause(bool paused)
    {
        if (!_running) return;

        if (paused)
        {
            Enqueue(LogEvent.SessionEvent("application_pause"));
            _flushRequested = true;

            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
            while (_flushRequested && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }
        }
        else
        {
            Enqueue(LogEvent.SessionEvent("application_resume"));
        }
    }

    private void TryApplyParticipantFileOverride()
    {
        try
        {
            var path = Path.Combine(Application.persistentDataPath, "participant.txt");
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                participantId = trimmed;
                _participantSource = "file";
                Debug.Log($"[SessionLogger] participantId overridden from participant.txt: '{participantId}'");
                return;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SessionLogger] participant.txt read failed (using Inspector value): {e.Message}");
        }
    }

    // ---- writer thread ----

    private void WriterLoop()
    {
        StreamWriter writer = null;
        try
        {
            writer = new StreamWriter(_resolvedPath, append: false, Encoding.UTF8);
            writer.WriteLine(LogEvent.CsvHeader);
            writer.Flush();
        }
        catch (Exception e)
        {
            // Unity's Debug.Log is thread-safe (queued to main thread).
            Debug.LogWarning($"[SessionLogger] Writer thread open failed (swallowed): {e.Message}");
            try { writer?.Dispose(); } catch { }
            return;
        }

        var sb = new StringBuilder(1024);
        var lastFlush = DateTime.UtcNow;
        var flushSpan = TimeSpan.FromSeconds(flushIntervalSeconds);

        try
        {
            while (_running || !_queue.IsEmpty)
            {
                bool wroteAny = false;
                while (_queue.TryDequeue(out var e))
                {
                    try
                    {
                        e.WriteCsvRow(sb);
                        writer.WriteLine(sb);
                        Interlocked.Increment(ref _writtenCount);
                        wroteAny = true;
                    }
                    catch (Exception ex)
                    {
                        // Per-row exception — swallow and continue. Don't crash the writer for one bad row.
                        Debug.LogWarning($"[SessionLogger] Row write exception (swallowed): {ex.Message}");
                    }
                }

                if (_flushRequested || (wroteAny && (DateTime.UtcNow - lastFlush) >= flushSpan))
                {
                    try { writer.Flush(); }
                    catch (Exception ex) { Debug.LogWarning($"[SessionLogger] Flush exception (swallowed): {ex.Message}"); }
                    lastFlush = DateTime.UtcNow;
                    // Only acknowledge the forced flush once the queue is truly
                    // drained — an event enqueued between the dequeue loop and
                    // this flush would otherwise be stranded unflushed while the
                    // pause handler stops waiting.
                    if (_queue.IsEmpty) _flushRequested = false;
                }

                Thread.Sleep(50);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SessionLogger] Writer loop exception (swallowed): {e.Message}");
        }
        finally
        {
            try { writer.Flush(); writer.Dispose(); }
            catch (Exception e) { Debug.LogWarning($"[SessionLogger] Writer close exception (swallowed): {e.Message}"); }
        }
    }

    private static string SanitizeForFilename(string s)
    {
        if (string.IsNullOrEmpty(s)) return "session";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }
}
