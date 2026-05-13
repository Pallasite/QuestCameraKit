using System.Collections;
using UnityEngine;

/// <summary>
/// Verification harness for <see cref="SessionLogger"/>. Emits N events/sec for D
/// seconds, then logs a summary. Used to validate the async writer's throughput,
/// flush cadence, and crash-resilience claims before the real subsystems are
/// wired up.
///
/// Verification checklist (per Phase 1 plan):
///   1. File exists at <c>Application.persistentDataPath/&lt;participant&gt;_&lt;ts&gt;.csv</c>
///   2. Row count within ±5 of <c>eventsPerSecond * durationSeconds</c>
///   3. All rows parse as valid CSV
///   4. Header matches SessionLoggerSchema.md
///   5. Kill the player mid-run → restart → previous file intact through last 2s flush
///   6. ADB pull verifies on host
///
/// Keep disabled or remove after Phase 1 verification.
/// </summary>
[DisallowMultipleComponent]
public sealed class LoggerSynthLoadTest : MonoBehaviour
{
    [SerializeField] private bool runOnStart = false;
    [SerializeField, Range(1, 1000)] private int eventsPerSecond = 100;
    [SerializeField, Range(1f, 600f)] private float durationSeconds = 60f;

    [ContextMenu("Run Now")]
    public void RunNow() => StartCoroutine(RunCoroutine());

    private void Start()
    {
        if (runOnStart) StartCoroutine(RunCoroutine());
    }

    private IEnumerator RunCoroutine()
    {
        if (SessionLogger.Instance == null)
        {
            Debug.LogError("[LoggerSynthLoadTest] No SessionLogger.Instance — add a SessionLogger to the scene first.");
            yield break;
        }

        var logger = SessionLogger.Instance;
        var startSession = logger.NowSession;
        var startEnqueued = logger.EnqueuedCount;

        Debug.Log($"[LoggerSynthLoadTest] Starting: {eventsPerSecond} ev/s for {durationSeconds}s -> {logger.ResolvedPath}");
        logger.Enqueue(LogEvent.SessionEvent("synth_load_test_start",
            $"target_events={Mathf.RoundToInt(eventsPerSecond * durationSeconds)};rate_hz={eventsPerSecond}"));

        // Time-based driver — more accurate than WaitForSeconds(1/rate) at high rates.
        long emittedCount = 0;
        while (logger.NowSession - startSession < durationSeconds)
        {
            double elapsed = logger.NowSession - startSession;
            long targetCount = (long)System.Math.Floor(elapsed * eventsPerSecond);
            while (emittedCount < targetCount)
            {
                logger.Enqueue(LogEvent.SessionEvent("synth_load_test", $"i={emittedCount}"));
                emittedCount++;
            }
            yield return null;
        }

        // Emit any final residual to hit the target exactly.
        long finalTarget = Mathf.RoundToInt(eventsPerSecond * durationSeconds);
        while (emittedCount < finalTarget)
        {
            logger.Enqueue(LogEvent.SessionEvent("synth_load_test", $"i={emittedCount}"));
            emittedCount++;
        }

        logger.Enqueue(LogEvent.SessionEvent("synth_load_test_end", $"emitted={emittedCount}"));
        var elapsedTotal = logger.NowSession - startSession;
        Debug.Log($"[LoggerSynthLoadTest] Done. Emitted {emittedCount} in {elapsedTotal:F2}s. " +
                  $"Logger enqueued total {logger.EnqueuedCount - startEnqueued} (incl. our markers), written {logger.WrittenCount}.");
    }
}
