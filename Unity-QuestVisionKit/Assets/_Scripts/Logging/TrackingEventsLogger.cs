using System;
using System.Globalization;
using System.IO;
using System.Text;
using QuestBuild;
using UnityEngine;

/// <summary>
/// Polls Meta tracking state each frame and emits a row to tracking_events.csv
/// only when something changes - head tracking lost/recovered, controller pose
/// validity flips, controller connect/disconnect, user-presence transitions.
/// The first row each session is a `tracking_baseline` snapshot so absolute
/// state is recoverable even if no transitions occur.
///
/// DEV-ONLY: self-gates on Debug.isDebugBuild unless forceOnInRelease is set.
/// </summary>
[DisallowMultipleComponent]
public sealed class TrackingEventsLogger : MonoBehaviour
{
    [Tooltip("Disables logging without removing the component.")]
    [SerializeField] private bool enableLogging = true;

    [Tooltip("If true, runs even in release builds. Default false: dev-only.")]
    [SerializeField] private bool forceOnInRelease = false;

    [Tooltip("Flush writer every N rows (sparse stream; small flush threshold).")]
    [SerializeField] private int flushEvery = 8;

    private StreamWriter _writer;
    private string _resolvedPath;
    private int _rowsSinceFlush;

    private bool _initialized;
    private bool _prevHead;
    private bool _prevCtrlLValid;
    private bool _prevCtrlRValid;
    private bool _prevCtrlLConnected;
    private bool _prevCtrlRConnected;
    private bool _prevUserPresent;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string Header =
        "unix_ms,timestamp_session,frame,event,state_from,state_to,detail";

    public string ResolvedPath => _resolvedPath;

    private void OnEnable()
    {
        if (!enableLogging) return;
        if (!Debug.isDebugBuild && !forceOnInRelease)
        {
            Debug.Log("[TrackingEventsLogger] Skipped: not a development build.");
            return;
        }

        try
        {
            _resolvedPath = SessionPaths.Combine("tracking_events.csv");
            bool isNew = !File.Exists(_resolvedPath) || new FileInfo(_resolvedPath).Length == 0;
            _writer = new StreamWriter(_resolvedPath, append: true, Encoding.UTF8);
            if (isNew)
            {
                _writer.WriteLine(Header);
                _writer.Flush();
            }
            Debug.Log($"[TrackingEventsLogger] Logging to {_resolvedPath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[TrackingEventsLogger] Failed to open writer: {e.Message}");
            _writer = null;
        }
    }

    private void OnDisable()
    {
        if (_writer != null)
        {
            try { _writer.Flush(); _writer.Dispose(); }
            catch (Exception e) { Debug.LogWarning($"[TrackingEventsLogger] Close error: {e.Message}"); }
            _writer = null;
        }
    }

    private void Update()
    {
        if (_writer == null) return;

        bool head = SafeNodeValid(OVRPlugin.Node.Head, defaultValue: true);
        bool ctrlLValid = OVRInput.GetControllerPositionValid(OVRInput.Controller.LTouch);
        bool ctrlRValid = OVRInput.GetControllerPositionValid(OVRInput.Controller.RTouch);
        bool ctrlLConn  = OVRInput.IsControllerConnected(OVRInput.Controller.LTouch);
        bool ctrlRConn  = OVRInput.IsControllerConnected(OVRInput.Controller.RTouch);
        bool userPres   = SafeUserPresent(defaultValue: true);

        if (!_initialized)
        {
            _prevHead = head;
            _prevCtrlLValid = ctrlLValid;
            _prevCtrlRValid = ctrlRValid;
            _prevCtrlLConnected = ctrlLConn;
            _prevCtrlRConnected = ctrlRConn;
            _prevUserPresent = userPres;
            _initialized = true;
            WriteRow("tracking_baseline", "", "",
                $"head={B(head)};ctrlL_valid={B(ctrlLValid)};ctrlR_valid={B(ctrlRValid)};" +
                $"ctrlL_conn={B(ctrlLConn)};ctrlR_conn={B(ctrlRConn)};user_present={B(userPres)}");
            return;
        }

        if (head != _prevHead)
        {
            WriteRow(head ? "head_tracking_recovered" : "head_tracking_lost",
                B(_prevHead), B(head), null);
            _prevHead = head;
        }
        if (ctrlLValid != _prevCtrlLValid)
        {
            WriteRow(ctrlLValid ? "controller_L_pose_valid" : "controller_L_pose_invalid",
                B(_prevCtrlLValid), B(ctrlLValid), null);
            _prevCtrlLValid = ctrlLValid;
        }
        if (ctrlRValid != _prevCtrlRValid)
        {
            WriteRow(ctrlRValid ? "controller_R_pose_valid" : "controller_R_pose_invalid",
                B(_prevCtrlRValid), B(ctrlRValid), null);
            _prevCtrlRValid = ctrlRValid;
        }
        if (ctrlLConn != _prevCtrlLConnected)
        {
            WriteRow(ctrlLConn ? "controller_L_connected" : "controller_L_disconnected",
                B(_prevCtrlLConnected), B(ctrlLConn), null);
            _prevCtrlLConnected = ctrlLConn;
        }
        if (ctrlRConn != _prevCtrlRConnected)
        {
            WriteRow(ctrlRConn ? "controller_R_connected" : "controller_R_disconnected",
                B(_prevCtrlRConnected), B(ctrlRConn), null);
            _prevCtrlRConnected = ctrlRConn;
        }
        if (userPres != _prevUserPresent)
        {
            WriteRow(userPres ? "user_present" : "user_absent",
                B(_prevUserPresent), B(userPres), null);
            _prevUserPresent = userPres;
        }
    }

    private void WriteRow(string ev, string from, string to, string detail)
    {
        try
        {
            long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            double ts = SessionLogger.Instance != null
                ? SessionLogger.Instance.NowSession
                : Time.realtimeSinceStartupAsDouble;
            int frame = Time.frameCount;

            var sb = new StringBuilder(128);
            sb.Append(unixMs.ToString(Inv)).Append(',');
            sb.Append(ts.ToString("R", Inv)).Append(',');
            sb.Append(frame.ToString(Inv)).Append(',');
            sb.Append(ev).Append(',');
            sb.Append(from).Append(',');
            sb.Append(to).Append(',');
            if (detail != null) EscapeCsv(sb, detail);
            _writer.WriteLine(sb);
            _rowsSinceFlush++;
            if (_rowsSinceFlush >= flushEvery)
            {
                try { _writer.Flush(); } catch { }
                _rowsSinceFlush = 0;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[TrackingEventsLogger] Row write failed: {e.Message}");
        }
    }

    private static string B(bool v) => v ? "1" : "0";

    private static bool SafeNodeValid(OVRPlugin.Node node, bool defaultValue)
    {
        try { return OVRPlugin.GetNodePoseStateValid(node); }
        catch { return defaultValue; }
    }

    private static bool SafeUserPresent(bool defaultValue)
    {
        try { return OVRPlugin.userPresent; }
        catch { return defaultValue; }
    }

    private static StringBuilder EscapeCsv(StringBuilder sb, string value)
    {
        if (string.IsNullOrEmpty(value)) return sb;
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
        {
            sb.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
        }
        else
        {
            sb.Append(value);
        }
        return sb;
    }
}
