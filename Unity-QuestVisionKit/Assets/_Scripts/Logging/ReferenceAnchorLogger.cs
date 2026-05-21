using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using QuestBuild;
using UnityEngine;

/// <summary>
/// Spawns N OVRSpatialAnchors at Inspector-configured offsets from a reference
/// Transform (typically the AprilTag-calibrated anchor) and samples each anchor's
/// world pose over time into reference_anchors.csv. Used to characterise whether
/// SLAM drift is uniform across the experimental space or whether each anchor
/// undergoes its own bundle-adjustment events.
///
/// DEV-ONLY: self-gates on Debug.isDebugBuild unless forceOnInRelease is set.
/// In a non-development build the CSV is never opened.
/// </summary>
[DisallowMultipleComponent]
public sealed class ReferenceAnchorLogger : MonoBehaviour
{
    [Serializable]
    public class AnchorOffset
    {
        [Tooltip("Free-form label written into the anchor_label column.")]
        public string label = "";

        [Tooltip("Position offset (metres) from referenceRoot, expressed in the root's local frame. Y up.")]
        public Vector3 offsetLocal = Vector3.zero;
    }

    [Header("Anchoring")]
    [Tooltip("Transform whose pose is the origin for the configured offsets - typically the AprilTag-calibrated anchor.")]
    [SerializeField] private Transform referenceRoot;

    [Tooltip("Reference anchors to spawn. Default config: 1 center + 4 corners at +/-2m on X/Z.")]
    [SerializeField] private AnchorOffset[] anchors = new AnchorOffset[]
    {
        new AnchorOffset { label = "center",       offsetLocal = new Vector3( 0f, 0f,  0f) },
        new AnchorOffset { label = "corner_pX_pZ", offsetLocal = new Vector3( 2f, 0f,  2f) },
        new AnchorOffset { label = "corner_nX_pZ", offsetLocal = new Vector3(-2f, 0f,  2f) },
        new AnchorOffset { label = "corner_pX_nZ", offsetLocal = new Vector3( 2f, 0f, -2f) },
        new AnchorOffset { label = "corner_nX_nZ", offsetLocal = new Vector3(-2f, 0f, -2f) },
    };

    [Header("Sampling")]
    [Tooltip("Sample rate per anchor (Hz). 5Hz matches the existing baseline cadence.")]
    [SerializeField, Range(0.5f, 30f)] private float sampleHz = 5f;

    [Tooltip("Flush writer every N rows.")]
    [SerializeField] private int flushEvery = 60;

    [Header("Gating")]
    [Tooltip("Disables logging without removing the component.")]
    [SerializeField] private bool enableLogging = true;

    [Tooltip("If true, runs even in release builds. Default false: dev-only.")]
    [SerializeField] private bool forceOnInRelease = false;

    private StreamWriter _writer;
    private string _resolvedPath;
    private float _nextSampleTime;
    private bool _spawned;
    private readonly List<GameObject> _anchorGOs = new List<GameObject>();
    private int _rowsSinceFlush;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string Header =
        "unix_ms,timestamp_session,frame,anchor_id,anchor_label," +
        "pos_x,pos_y,pos_z,rot_x,rot_y,rot_z,rot_w," +
        "is_localized,tracking_state";

    public string ResolvedPath => _resolvedPath;

    private void OnEnable()
    {
        if (!enableLogging) return;
        if (!Debug.isDebugBuild && !forceOnInRelease)
        {
            Debug.Log("[ReferenceAnchorLogger] Skipped: not a development build (set forceOnInRelease to override).");
            return;
        }

        try
        {
            _resolvedPath = SessionPaths.Combine("reference_anchors.csv");
            bool isNew = !File.Exists(_resolvedPath) || new FileInfo(_resolvedPath).Length == 0;
            _writer = new StreamWriter(_resolvedPath, append: true, Encoding.UTF8);
            if (isNew)
            {
                _writer.WriteLine(Header);
                _writer.Flush();
            }
            Debug.Log($"[ReferenceAnchorLogger] Logging to {_resolvedPath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[ReferenceAnchorLogger] Failed to open writer: {e.Message}");
            _writer = null;
        }
    }

    private void OnDisable()
    {
        TeardownAnchors();
        if (_writer != null)
        {
            try { _writer.Flush(); _writer.Dispose(); }
            catch (Exception e) { Debug.LogWarning($"[ReferenceAnchorLogger] Close error: {e.Message}"); }
            _writer = null;
        }
    }

    private void Update()
    {
        if (_writer == null) return;

        // Lazy-spawn once the reference root is available and active in the scene
        // (typically once AprilTag calibration enables the anchor GameObject).
        if (!_spawned)
        {
            if (referenceRoot == null || !referenceRoot.gameObject.activeInHierarchy) return;
            SpawnAnchors();
        }

        if (Time.unscaledTime < _nextSampleTime) return;
        _nextSampleTime = Time.unscaledTime + (1f / Mathf.Max(0.01f, sampleHz));

        long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        double ts = SessionLogger.Instance != null
            ? SessionLogger.Instance.NowSession
            : Time.realtimeSinceStartupAsDouble;
        int frame = Time.frameCount;

        var sb = new StringBuilder(160);
        for (int i = 0; i < _anchorGOs.Count; i++)
        {
            var go = _anchorGOs[i];
            if (go == null) continue;

            var label = i < anchors.Length ? (anchors[i].label ?? "") : "";
            var pos = go.transform.position;
            var rot = go.transform.rotation;

            bool isLocalized = false;
            string trackingState = "Unknown";
            var anchor = go.GetComponent<OVRSpatialAnchor>();
            if (anchor != null)
            {
                try { isLocalized = anchor.Localized; }
                catch { isLocalized = false; }
                trackingState = isLocalized ? "Localized" : "Pending";
            }

            sb.Length = 0;
            sb.Append(unixMs.ToString(Inv)).Append(',');
            sb.Append(ts.ToString("R", Inv)).Append(',');
            sb.Append(frame.ToString(Inv)).Append(',');
            sb.Append(i.ToString(Inv)).Append(',');
            EscapeCsv(sb, label).Append(',');
            sb.Append(pos.x.ToString("R", Inv)).Append(',');
            sb.Append(pos.y.ToString("R", Inv)).Append(',');
            sb.Append(pos.z.ToString("R", Inv)).Append(',');
            sb.Append(rot.x.ToString("R", Inv)).Append(',');
            sb.Append(rot.y.ToString("R", Inv)).Append(',');
            sb.Append(rot.z.ToString("R", Inv)).Append(',');
            sb.Append(rot.w.ToString("R", Inv)).Append(',');
            sb.Append(isLocalized ? "1" : "0").Append(',');
            sb.Append(trackingState);
            _writer.WriteLine(sb);
            _rowsSinceFlush++;
        }

        if (_rowsSinceFlush >= flushEvery)
        {
            try { _writer.Flush(); } catch { }
            _rowsSinceFlush = 0;
        }
    }

    private void SpawnAnchors()
    {
        try
        {
            for (int i = 0; i < anchors.Length; i++)
            {
                var off = anchors[i].offsetLocal;
                var worldPos = referenceRoot.TransformPoint(off);
                var worldRot = referenceRoot.rotation;

                var go = new GameObject($"[RefAnchor_{i}_{anchors[i].label}]");
                go.transform.SetPositionAndRotation(worldPos, worldRot);
                go.AddComponent<OVRSpatialAnchor>();
                _anchorGOs.Add(go);
            }
            _spawned = true;
            Debug.Log($"[ReferenceAnchorLogger] Spawned {_anchorGOs.Count} reference anchors anchored on {referenceRoot.name}.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[ReferenceAnchorLogger] Spawn failed: {e.Message}");
            _spawned = true; // don't keep retrying every frame on a hard failure
        }
    }

    private void TeardownAnchors()
    {
        foreach (var go in _anchorGOs)
        {
            if (go != null) Destroy(go);
        }
        _anchorGOs.Clear();
        _spawned = false;
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
