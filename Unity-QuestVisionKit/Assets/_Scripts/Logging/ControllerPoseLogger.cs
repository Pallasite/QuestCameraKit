using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using QuestBuild;
using UnityEngine;

/// <summary>
/// Per-frame L+R controller poses + validity to controller_poses.csv, plus
/// per-controller 1s RMS jitter to controller_pose_stats.csv. Used for offline
/// jitter analysis (e.g. average translational + rotational noise while the
/// controllers are seated in their static rig).
///
/// DEV-ONLY: self-gates on Debug.isDebugBuild unless forceOnInRelease is set.
/// </summary>
[DisallowMultipleComponent]
public sealed class ControllerPoseLogger : MonoBehaviour
{
    [Tooltip("Disables logging without removing the component.")]
    [SerializeField] private bool enableLogging = true;

    [Tooltip("If true, runs even in release builds. Default false: dev-only.")]
    [SerializeField] private bool forceOnInRelease = false;

    [Tooltip("Rolling window for jitter stats, in seconds.")]
    [SerializeField, Range(0.25f, 5f)] private float jitterWindowSec = 1f;

    [Tooltip("Flush the raw writer every N rows.")]
    [SerializeField] private int flushEvery = 180;

    private StreamWriter _writerRaw;
    private StreamWriter _writerStats;
    private string _resolvedRaw;
    private string _resolvedStats;
    private int _rowsSinceFlush;

    private struct Sample { public double t; public Vector3 pos; public Quaternion rot; }
    private readonly Queue<Sample> _windowL = new Queue<Sample>(256);
    private readonly Queue<Sample> _windowR = new Queue<Sample>(256);

    private Vector3 _prevPosL; private Quaternion _prevRotL; private bool _havePrevL;
    private Vector3 _prevPosR; private Quaternion _prevRotR; private bool _havePrevR;

    private float _nextStatsTime;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string HeaderRaw =
        "unix_ms,timestamp_session,frame,side,pos_x,pos_y,pos_z,rot_x,rot_y,rot_z,rot_w," +
        "position_valid,orientation_valid,linvel_mps,angvel_dps";
    private const string HeaderStats =
        "unix_ms,timestamp_session,side,window_sec,sample_count,pos_jitter_rms_mm,rot_jitter_rms_deg";

    public string ResolvedRawPath => _resolvedRaw;
    public string ResolvedStatsPath => _resolvedStats;

    private void OnEnable()
    {
        if (!enableLogging) return;
        if (!Debug.isDebugBuild && !forceOnInRelease)
        {
            Debug.Log("[ControllerPoseLogger] Skipped: not a development build.");
            return;
        }
        try
        {
            _resolvedRaw = SessionPaths.Combine("controller_poses.csv");
            _resolvedStats = SessionPaths.Combine("controller_pose_stats.csv");
            _writerRaw = OpenWithHeader(_resolvedRaw, HeaderRaw);
            _writerStats = OpenWithHeader(_resolvedStats, HeaderStats);
            Debug.Log($"[ControllerPoseLogger] Logging to {_resolvedRaw} + {_resolvedStats}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[ControllerPoseLogger] Open failed: {e.Message}");
            CloseWriters();
        }
    }

    private void OnDisable() => CloseWriters();

    private void Update()
    {
        if (_writerRaw == null) return;

        double t = Time.realtimeSinceStartupAsDouble;
        long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        double ts = SessionLogger.Instance != null
            ? SessionLogger.Instance.NowSession
            : t;
        int frame = Time.frameCount;
        float dt = Mathf.Max(1e-6f, Time.unscaledDeltaTime);

        TickSide("L", OVRInput.Controller.LTouch, ref _havePrevL, ref _prevPosL, ref _prevRotL,
            _windowL, t, unixMs, ts, frame, dt);
        TickSide("R", OVRInput.Controller.RTouch, ref _havePrevR, ref _prevPosR, ref _prevRotR,
            _windowR, t, unixMs, ts, frame, dt);

        if (Time.unscaledTime >= _nextStatsTime && _writerStats != null)
        {
            _nextStatsTime = Time.unscaledTime + 1f;
            WriteStats("L", _windowL, unixMs, ts);
            WriteStats("R", _windowR, unixMs, ts);
        }
    }

    private void TickSide(string side, OVRInput.Controller ctrl,
        ref bool havePrev, ref Vector3 prevPos, ref Quaternion prevRot,
        Queue<Sample> window,
        double t, long unixMs, double ts, int frame, float dt)
    {
        bool posValid = OVRInput.GetControllerPositionValid(ctrl);
        bool rotValid = OVRInput.GetControllerOrientationValid(ctrl);
        var pos = OVRInput.GetLocalControllerPosition(ctrl);
        var rot = OVRInput.GetLocalControllerRotation(ctrl);

        float linvel = 0f, angvel = 0f;
        if (havePrev)
        {
            linvel = Vector3.Distance(pos, prevPos) / dt;
            angvel = Quaternion.Angle(rot, prevRot) / dt;
        }
        prevPos = pos; prevRot = rot; havePrev = true;

        WriteRaw(unixMs, ts, frame, side, pos, rot, posValid, rotValid, linvel, angvel);

        window.Enqueue(new Sample { t = t, pos = pos, rot = rot });
        double cutoff = t - jitterWindowSec;
        while (window.Count > 0 && window.Peek().t < cutoff) window.Dequeue();
    }

    private void WriteRaw(long unixMs, double ts, int frame, string side,
        Vector3 pos, Quaternion rot, bool posValid, bool rotValid, float linvel, float angvel)
    {
        try
        {
            var sb = new StringBuilder(192);
            sb.Append(unixMs.ToString(Inv)).Append(',');
            sb.Append(ts.ToString("R", Inv)).Append(',');
            sb.Append(frame.ToString(Inv)).Append(',');
            sb.Append(side).Append(',');
            sb.Append(pos.x.ToString("R", Inv)).Append(',');
            sb.Append(pos.y.ToString("R", Inv)).Append(',');
            sb.Append(pos.z.ToString("R", Inv)).Append(',');
            sb.Append(rot.x.ToString("R", Inv)).Append(',');
            sb.Append(rot.y.ToString("R", Inv)).Append(',');
            sb.Append(rot.z.ToString("R", Inv)).Append(',');
            sb.Append(rot.w.ToString("R", Inv)).Append(',');
            sb.Append(posValid ? "1" : "0").Append(',');
            sb.Append(rotValid ? "1" : "0").Append(',');
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
        catch (Exception e) { Debug.LogWarning($"[ControllerPoseLogger] Raw row ({side}) failed: {e.Message}"); }
    }

    private void WriteStats(string side, Queue<Sample> window, long unixMs, double ts)
    {
        try
        {
            int n = window.Count;
            if (n < 2) return;

            float posSq = 0f, rotSq = 0f; int pairs = 0;
            bool havePrev = false;
            Vector3 prevPos = default;
            Quaternion prevRot = default;
            foreach (var s in window)
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
            sb.Append(side).Append(',');
            sb.Append(jitterWindowSec.ToString("R", Inv)).Append(',');
            sb.Append(n.ToString(Inv)).Append(',');
            sb.Append(posRms.ToString("R", Inv)).Append(',');
            sb.Append(rotRms.ToString("R", Inv));
            _writerStats.WriteLine(sb);
            try { _writerStats.Flush(); } catch { }
        }
        catch (Exception e) { Debug.LogWarning($"[ControllerPoseLogger] Stats row ({side}) failed: {e.Message}"); }
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
        if (_writerRaw != null) { try { _writerRaw.Flush(); _writerRaw.Dispose(); } catch { } _writerRaw = null; }
        if (_writerStats != null) { try { _writerStats.Flush(); _writerStats.Dispose(); } catch { } _writerStats = null; }
    }
}
