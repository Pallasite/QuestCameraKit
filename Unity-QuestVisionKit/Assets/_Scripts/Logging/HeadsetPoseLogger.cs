using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using QuestBuild;
using UnityEngine;

/// <summary>
/// Per-frame headset (Camera.main) pose + computed linear/angular velocities
/// into headset_poses.csv, plus a rolling 1s RMS jitter summary into
/// headset_pose_stats.csv. Used for offline jitter and SLAM-quality analysis.
///
/// DEV-ONLY: self-gates on Debug.isDebugBuild unless forceOnInRelease is set.
/// The raw file is large (~30-50 MB per 45-min session) so absence is the
/// signal that this was a non-development build.
/// </summary>
[DisallowMultipleComponent]
public sealed class HeadsetPoseLogger : MonoBehaviour
{
    [Tooltip("Disables logging without removing the component.")]
    [SerializeField] private bool enableLogging = true;

    [Tooltip("If true, runs even in release builds. Default false: dev-only.")]
    [SerializeField] private bool forceOnInRelease = false;

    [Tooltip("Rolling window for jitter stats, in seconds.")]
    [SerializeField, Range(0.25f, 5f)] private float jitterWindowSec = 1f;

    [Tooltip("Flush the raw writer every N rows.")]
    [SerializeField] private int flushEvery = 90;

    private StreamWriter _writerRaw;
    private StreamWriter _writerStats;
    private string _resolvedRaw;
    private string _resolvedStats;
    private int _rowsSinceFlush;

    private Camera _camera;
    private Vector3 _prevPos;
    private Quaternion _prevRot;
    private bool _havePrev;

    private struct Sample { public double t; public Vector3 pos; public Quaternion rot; }
    private readonly Queue<Sample> _window = new Queue<Sample>(256);
    private float _nextStatsTime;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string HeaderRaw =
        "unix_ms,timestamp_session,frame,pos_x,pos_y,pos_z,rot_x,rot_y,rot_z,rot_w,linvel_mps,angvel_dps";
    private const string HeaderStats =
        "unix_ms,timestamp_session,window_sec,sample_count,pos_jitter_rms_mm,rot_jitter_rms_deg";

    public string ResolvedRawPath => _resolvedRaw;
    public string ResolvedStatsPath => _resolvedStats;

    private void OnEnable()
    {
        if (!enableLogging) return;
        if (!Debug.isDebugBuild && !forceOnInRelease)
        {
            Debug.Log("[HeadsetPoseLogger] Skipped: not a development build.");
            return;
        }
        try
        {
            _resolvedRaw = SessionPaths.Combine("headset_poses.csv");
            _resolvedStats = SessionPaths.Combine("headset_pose_stats.csv");
            _writerRaw = OpenWithHeader(_resolvedRaw, HeaderRaw);
            _writerStats = OpenWithHeader(_resolvedStats, HeaderStats);
            Debug.Log($"[HeadsetPoseLogger] Logging to {_resolvedRaw} + {_resolvedStats}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[HeadsetPoseLogger] Open failed: {e.Message}");
            CloseWriters();
        }
    }

    private void OnDisable() => CloseWriters();

    private void Update()
    {
        if (_writerRaw == null) return;
        if (_camera == null) _camera = Camera.main;
        if (_camera == null) return;

        var pos = _camera.transform.position;
        var rot = _camera.transform.rotation;
        double t = Time.realtimeSinceStartupAsDouble;
        long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        double ts = SessionLogger.Instance != null
            ? SessionLogger.Instance.NowSession
            : t;
        int frame = Time.frameCount;
        float dt = Mathf.Max(1e-6f, Time.unscaledDeltaTime);

        float linvel = 0f, angvel = 0f;
        if (_havePrev)
        {
            linvel = Vector3.Distance(pos, _prevPos) / dt;
            angvel = Quaternion.Angle(rot, _prevRot) / dt;
        }
        _prevPos = pos; _prevRot = rot; _havePrev = true;

        WriteRaw(unixMs, ts, frame, pos, rot, linvel, angvel);

        _window.Enqueue(new Sample { t = t, pos = pos, rot = rot });
        double cutoff = t - jitterWindowSec;
        while (_window.Count > 0 && _window.Peek().t < cutoff) _window.Dequeue();

        if (Time.unscaledTime >= _nextStatsTime && _writerStats != null)
        {
            _nextStatsTime = Time.unscaledTime + 1f;
            WriteStats(unixMs, ts);
        }
    }

    private void WriteRaw(long unixMs, double ts, int frame,
        Vector3 pos, Quaternion rot, float linvel, float angvel)
    {
        try
        {
            var sb = new StringBuilder(160);
            sb.Append(unixMs.ToString(Inv)).Append(',');
            sb.Append(ts.ToString("R", Inv)).Append(',');
            sb.Append(frame.ToString(Inv)).Append(',');
            sb.Append(pos.x.ToString("R", Inv)).Append(',');
            sb.Append(pos.y.ToString("R", Inv)).Append(',');
            sb.Append(pos.z.ToString("R", Inv)).Append(',');
            sb.Append(rot.x.ToString("R", Inv)).Append(',');
            sb.Append(rot.y.ToString("R", Inv)).Append(',');
            sb.Append(rot.z.ToString("R", Inv)).Append(',');
            sb.Append(rot.w.ToString("R", Inv)).Append(',');
            sb.Append(linvel.ToString("R", Inv)).Append(',');
            sb.Append(angvel.ToString("R", Inv));
            _writerRaw.WriteLine(sb);
            _rowsSinceFlush++;
            if (_rowsSinceFlush >= flushEvery)
            {
                try { _writerRaw.Flush(); } catch { }
                _rowsSinceFlush = 0;
            }
        }
        catch (Exception e) { Debug.LogWarning($"[HeadsetPoseLogger] Raw row failed: {e.Message}"); }
    }

    private void WriteStats(long unixMs, double ts)
    {
        try
        {
            int n = _window.Count;
            if (n < 2) return;

            float posSq = 0f, rotSq = 0f; int pairs = 0;
            bool havePrev = false;
            Vector3 prevPos = default;
            Quaternion prevRot = default;
            foreach (var s in _window)
            {
                if (havePrev)
                {
                    float dp_mm = Vector3.Distance(s.pos, prevPos) * 1000f;
                    float dr_deg = Quaternion.Angle(s.rot, prevRot);
                    posSq += dp_mm * dp_mm;
                    rotSq += dr_deg * dr_deg;
                    pairs++;
                }
                prevPos = s.pos; prevRot = s.rot; havePrev = true;
            }
            float posRms = pairs > 0 ? Mathf.Sqrt(posSq / pairs) : 0f;
            float rotRms = pairs > 0 ? Mathf.Sqrt(rotSq / pairs) : 0f;

            var sb = new StringBuilder(120);
            sb.Append(unixMs.ToString(Inv)).Append(',');
            sb.Append(ts.ToString("R", Inv)).Append(',');
            sb.Append(jitterWindowSec.ToString("R", Inv)).Append(',');
            sb.Append(n.ToString(Inv)).Append(',');
            sb.Append(posRms.ToString("R", Inv)).Append(',');
            sb.Append(rotRms.ToString("R", Inv));
            _writerStats.WriteLine(sb);
            try { _writerStats.Flush(); } catch { }
        }
        catch (Exception e) { Debug.LogWarning($"[HeadsetPoseLogger] Stats row failed: {e.Message}"); }
    }

    private static StreamWriter OpenWithHeader(string path, string header)
    {
        bool isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
        var w = new StreamWriter(path, append: true, Encoding.UTF8);
        if (isNew) { w.WriteLine(header); w.Flush(); }
        return w;
    }

    private void CloseWriters()
    {
        if (_writerRaw != null)
        {
            try { _writerRaw.Flush(); _writerRaw.Dispose(); } catch { }
            _writerRaw = null;
        }
        if (_writerStats != null)
        {
            try { _writerStats.Flush(); _writerStats.Dispose(); } catch { }
            _writerStats = null;
        }
    }
}
