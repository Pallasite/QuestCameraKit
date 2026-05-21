using System;
using System.Globalization;
using System.IO;
using System.Text;
using QuestBuild;
using UnityEngine;

/// <summary>
/// Per-frame CSV logger for the AprilTag solver-comparison experiment. Subscribes
/// to AprilTagDisplayManager.OnTagsDetected and writes one row per detected tag
/// per frame, recording which RotationSolver produced the result, the world-space
/// pose, the recovered tag size, and the corner residual (uniform accuracy proxy
/// across all five solver modes).
///
/// Output goes to Application.persistentDataPath, which on Quest is the app's
/// scoped storage and is reachable via adb pull. Rows are appended; toggling
/// through the five solver modes (StereoAprilTagScanner.Solver) during a session
/// produces a single contiguous file that can be split off-line by the `solver`
/// column.
///
/// Header: timestamp_unix_ms,frame,tag_id,solver,pos_x,pos_y,pos_z,rot_x,rot_y,rot_z,rot_w,size_m,residual_m
/// </summary>
[DisallowMultipleComponent]
public class AprilTagSolverComparisonLogger : MonoBehaviour
{
    [Tooltip("Display manager whose OnTagsDetected event will be logged. " +
             "Falls back to GetComponent<AprilTagDisplayManager>() if not assigned.")]
    [SerializeField] private AprilTagDisplayManager displayManager;

    [Tooltip("CSV file name written into the per-launch session folder " +
             "(persistentDataPath/Sessions/<sessionId>/<fileName>). Pull via " +
             "Tools/Pull-Sessions.ps1 or adb pull /sdcard/Android/data/<package>/files/Sessions/")]
    [SerializeField] private string fileName = "apriltag_solver_comparison.csv";

    [Tooltip("Disables logging without removing the component. Useful for " +
             "starting the session, dialing in tag distance, then enabling.")]
    [SerializeField] private bool enableLogging = true;

    [Tooltip("Flush the writer to disk every N rows. Lower = safer if the app " +
             "crashes mid-session, higher = lower per-frame I/O cost.")]
    [SerializeField] private int flushEvery = 30;

    private StreamWriter _writer;
    private int _rowsSinceFlush;
    private string _resolvedPath;

    private const string Header =
        "timestamp_unix_ms,frame,tag_id,solver,pos_x,pos_y,pos_z,rot_x,rot_y,rot_z,rot_w,size_m,residual_m";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string ResolvedPath => _resolvedPath;

    private void Awake()
    {
        if (!displayManager) displayManager = GetComponent<AprilTagDisplayManager>();
    }

    private void OnEnable()
    {
        if (!enableLogging) return;
        if (!displayManager)
        {
            Debug.LogWarning("[AprilTagSolverComparisonLogger] No AprilTagDisplayManager assigned or found on this GameObject. Logging disabled.");
            return;
        }

        try
        {
            // Route through SessionPaths so the sample CSV lands inside the same
            // per-launch session folder as the dev log + experiment CSV.
            _resolvedPath = SessionPaths.Combine(fileName);
            bool isNew = !File.Exists(_resolvedPath) || new FileInfo(_resolvedPath).Length == 0;
            _writer = new StreamWriter(_resolvedPath, append: true, Encoding.UTF8);
            if (isNew)
            {
                _writer.WriteLine(Header);
                _writer.Flush();
            }
            displayManager.OnTagsDetected += HandleTagsDetected;
            Debug.Log($"[AprilTagSolverComparisonLogger] Logging to {_resolvedPath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[AprilTagSolverComparisonLogger] Failed to open {_resolvedPath}: {e.Message}");
            _writer = null;
        }
    }

    private void OnDisable()
    {
        if (displayManager) displayManager.OnTagsDetected -= HandleTagsDetected;
        if (_writer != null)
        {
            try { _writer.Flush(); _writer.Dispose(); }
            catch (Exception e) { Debug.LogWarning($"[AprilTagSolverComparisonLogger] Close error: {e.Message}"); }
            _writer = null;
        }
    }

    private void HandleTagsDetected(AprilTagDisplayManager.TagWorldPose[] poses)
    {
        if (_writer == null || poses == null || poses.Length == 0) return;

        long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int frame = Time.frameCount;

        var sb = new StringBuilder(128);
        for (int i = 0; i < poses.Length; i++)
        {
            var p = poses[i];
            sb.Length = 0;
            sb.Append(unixMs.ToString(Inv)).Append(',');
            sb.Append(frame.ToString(Inv)).Append(',');
            sb.Append(p.TagId.ToString(Inv)).Append(',');
            sb.Append(p.SolverUsed.ToString()).Append(',');
            sb.Append(p.Position.x.ToString("R", Inv)).Append(',');
            sb.Append(p.Position.y.ToString("R", Inv)).Append(',');
            sb.Append(p.Position.z.ToString("R", Inv)).Append(',');
            sb.Append(p.Rotation.x.ToString("R", Inv)).Append(',');
            sb.Append(p.Rotation.y.ToString("R", Inv)).Append(',');
            sb.Append(p.Rotation.z.ToString("R", Inv)).Append(',');
            sb.Append(p.Rotation.w.ToString("R", Inv)).Append(',');
            sb.Append(p.SizeMeters.ToString("R", Inv)).Append(',');
            sb.Append(p.CornerResidualMeters.ToString("R", Inv));
            _writer.WriteLine(sb);

            _rowsSinceFlush++;
        }

        if (_rowsSinceFlush >= flushEvery)
        {
            _writer.Flush();
            _rowsSinceFlush = 0;
        }
    }
}
