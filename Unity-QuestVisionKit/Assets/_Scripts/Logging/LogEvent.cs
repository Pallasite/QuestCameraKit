using System.Globalization;
using System.Text;
using UnityEngine;

/// <summary>
/// One row in the SessionLogger output CSV. Wide-and-sparse schema — most fields
/// are nullable and only populated by the relevant event type. See
/// SessionLoggerSchema.md for the full column contract.
///
/// Constructed via the static factory methods on this class; never built piecewise
/// by callers. Each factory captures <see cref="Time.realtimeSinceStartupAsDouble"/>
/// and <see cref="Time.frameCount"/> at construction time, so events must be
/// created on the main thread. The background writer thread only reads from
/// already-populated instances.
///
/// Phase 1 emits: <c>session_event</c>, <c>state_snapshot</c>, <c>sleep_event</c>,
/// <c>calibration_event</c>, <c>walk_event</c>. Phase 2 factories
/// (<c>correction_event</c>, <c>source_state_change</c>, <c>validation_walk</c>,
/// <c>snap_event</c>) are defined now so the schema is complete from day one.
/// </summary>
public sealed class LogEvent
{
    /// <summary>Bump this when adding/removing/renaming columns. See SessionLoggerSchema.md changelog.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- Required on every row ----
    public int SchemaVersion;
    public double TimestampSession;
    public int FrameNumber;
    public string EventType;          // "session_event", "state_snapshot", "sleep_event", "calibration_event",
                                      // "walk_event", "correction_event", "source_state_change", "validation_walk", "snap_event"
    public string CorrectionSource;   // "anchor_baseline", "controller", "apriltag", "optitrack", "system"
    public string Mode;               // "applied", "observe", "n/a"

    // ---- session_event / source_state_change ----
    public string Subtype;
    public string Detail;

    // ---- Pose snapshot fields ----
    public Vector3? AnchorPos;
    public Quaternion? AnchorRot;
    public Vector3? HeadsetPos;
    public Quaternion? HeadsetRot;
    public Vector3? ControllerLPos;
    public Quaternion? ControllerLRot;
    public Vector3? ControllerRPos;
    public Quaternion? ControllerRRot;

    // ---- Validity / connection ----
    public bool? PositionValidL, PositionValidR;
    public bool? OrientationValidL, OrientationValidR;
    public bool? ConnectedL, ConnectedR;

    // ---- Self-computed velocities (m/s) ----
    public float? VelocityLMps, VelocityRMps;

    // ---- Battery (percent, 0-100) ----
    public float? BatteryLPercent, BatteryRPercent;

    // ---- Rigid body validator fields ----
    public float? InterControllerDistanceM;
    public float? InterControllerRotationDeg;
    public float? DeviationFromBaselineM;
    public float? DeviationFromBaselineDeg;
    public bool? ValidationEnforced;

    // ---- sleep_event ----
    public string SleepEventType;       // "pulse", "disconnect", "reconnect", "battery_sample"
    public float? TimeSinceLastPulseS;

    // ---- walk_event ----
    public int? WalkIndex;
    public string WalkPhase;             // "start", "moved", "reset", "end"
    public bool? TrialActive;
    public bool? MoveTowardsUser;
    public float? TriggerDistanceM;
    public float? PerturbationDistanceM;
    public float? WalkDurationS;
    public int? CorrectionsAppliedCount;
    public float? MaxCorrectionMagnitudeM;
    public string RejectionReasonHistogram;  // JSON object as a string in this cell

    // ---- calibration_event ----
    public string CalibrationStep;       // "rigid_body_baseline", "rigid_body_sample", etc.
    public int? CalibrationSampleIndex;
    public float? MeanDistanceM, StddevDistanceM;
    public float? MeanRotDeg, StddevRotDeg;

    // ---- correction_event (Phase 2; defined now) ----
    public bool? Accepted;
    public string RejectionReason;
    public float? DeltaPositionM;
    public float? DeltaRotationDeg;
    public float? EmaAlphaApplied;
    public float? CorrectionAppliedM;
    public float? ControllerDistanceM;
    public float? ControllerVelocityMps;
    public string ContextFor;            // snap_event id when this row is part of a ring-buffer dump

    private LogEvent(string eventType, string correctionSource, string mode)
    {
        SchemaVersion = CurrentSchemaVersion;
        // Time.realtimeSinceStartupAsDouble and Time.frameCount are main-thread only,
        // so we capture them at construction time. The writer thread only reads.
        TimestampSession = SessionLogger.Instance != null ? SessionLogger.Instance.NowSession : 0.0;
        FrameNumber = Time.frameCount;
        EventType = eventType;
        CorrectionSource = correctionSource;
        Mode = mode;
    }

    // =====================================================================================
    // Phase 1 factories — emitted by current Phase 1 code
    // =====================================================================================

    public static LogEvent SessionEvent(string subtype, string detail = null)
        => new LogEvent("session_event", "system", "n/a") { Subtype = subtype, Detail = detail };

    public static LogEvent StateSnapshot(string correctionSource, string mode = "n/a")
        => new LogEvent("state_snapshot", correctionSource, mode);

    public static LogEvent SleepEvent(string sleepEventType, float? timeSinceLastPulseS = null)
        => new LogEvent("sleep_event", "system", "n/a")
            { SleepEventType = sleepEventType, TimeSinceLastPulseS = timeSinceLastPulseS };

    public static LogEvent CalibrationEvent(string step, int? sampleIndex = null,
        float? meanDistanceM = null, float? stddevDistanceM = null,
        float? meanRotDeg = null, float? stddevRotDeg = null)
        => new LogEvent("calibration_event", "controller", "n/a")
            {
                CalibrationStep = step,
                CalibrationSampleIndex = sampleIndex,
                MeanDistanceM = meanDistanceM, StddevDistanceM = stddevDistanceM,
                MeanRotDeg = meanRotDeg, StddevRotDeg = stddevRotDeg
            };

    public static LogEvent WalkEvent(int walkIndex, string phase)
        => new LogEvent("walk_event", "system", "n/a")
            { WalkIndex = walkIndex, WalkPhase = phase };

    // =====================================================================================
    // Phase 2 factories — defined now so the schema is complete; not emitted in Phase 1
    // =====================================================================================

    public static LogEvent CorrectionEvent(string correctionSource, string mode, bool accepted,
        string rejectionReason = null, float? deltaPositionM = null, float? deltaRotationDeg = null,
        float? emaAlphaApplied = null, float? correctionAppliedM = null,
        float? controllerDistanceM = null, float? controllerVelocityMps = null,
        string contextFor = null)
        => new LogEvent("correction_event", correctionSource, mode)
            {
                Accepted = accepted,
                RejectionReason = rejectionReason,
                DeltaPositionM = deltaPositionM,
                DeltaRotationDeg = deltaRotationDeg,
                EmaAlphaApplied = emaAlphaApplied,
                CorrectionAppliedM = correctionAppliedM,
                ControllerDistanceM = controllerDistanceM,
                ControllerVelocityMps = controllerVelocityMps,
                ContextFor = contextFor
            };

    public static LogEvent SourceStateChange(string correctionSource, string mode,
        string newState, string reason = null)
        => new LogEvent("source_state_change", correctionSource, mode)
            { Subtype = newState, Detail = reason };

    public static LogEvent ValidationWalk(int walkIndex,
        float? meanDistanceM = null, float? meanRotDeg = null,
        float? deviationFromBaselineM = null, float? deviationFromBaselineDeg = null)
        => new LogEvent("validation_walk", "controller", "n/a")
            {
                WalkIndex = walkIndex,
                MeanDistanceM = meanDistanceM,
                MeanRotDeg = meanRotDeg,
                DeviationFromBaselineM = deviationFromBaselineM,
                DeviationFromBaselineDeg = deviationFromBaselineDeg
            };

    public static LogEvent SnapEvent(string correctionSource,
        float deltaPositionM, float deltaRotationDeg)
        => new LogEvent("snap_event", correctionSource, "applied")
            { DeltaPositionM = deltaPositionM, DeltaRotationDeg = deltaRotationDeg };

    // =====================================================================================
    // CSV serialization. Header order MUST stay in sync with WriteCsvRow column order.
    // Bump CurrentSchemaVersion + update SessionLoggerSchema.md changelog when changing.
    // =====================================================================================

    public const string CsvHeader =
        "schema_version,timestamp_session,frame_number,event_type,correction_source,mode," +
        "subtype,detail," +
        "anchor_pos_xyz,anchor_rot_xyzw,headset_pos_xyz,headset_rot_xyzw," +
        "controller_L_pos_xyz,controller_L_rot_xyzw,controller_R_pos_xyz,controller_R_rot_xyzw," +
        "position_valid_L,position_valid_R,orientation_valid_L,orientation_valid_R," +
        "connected_L,connected_R,velocity_L_mps,velocity_R_mps," +
        "battery_L_percent,battery_R_percent," +
        "inter_controller_distance_m,inter_controller_rotation_deg," +
        "deviation_from_baseline_m,deviation_from_baseline_deg,validation_enforced," +
        "sleep_event_type,time_since_last_pulse_s," +
        "walk_index,walk_phase,trial_active,move_towards_user,trigger_distance_m,perturbation_distance_m," +
        "walk_duration_s,corrections_applied_count,max_correction_magnitude_m,rejection_reason_histogram," +
        "calibration_step,calibration_sample_index,mean_distance_m,stddev_distance_m,mean_rot_deg,stddev_rot_deg," +
        "accepted,rejection_reason,delta_position_m,delta_rotation_deg,ema_alpha_applied,correction_applied_m," +
        "controller_distance_m,controller_velocity_mps,context_for";

    /// <summary>Render this event into <paramref name="sb"/> as one CSV row (no trailing newline). Caller is responsible for the newline.</summary>
    public void WriteCsvRow(StringBuilder sb)
    {
        sb.Length = 0;
        sb.Append(SchemaVersion.ToString(Inv)).Append(',');
        sb.Append(TimestampSession.ToString("R", Inv)).Append(',');
        sb.Append(FrameNumber.ToString(Inv)).Append(',');
        AppendCsv(sb, EventType); sb.Append(',');
        AppendCsv(sb, CorrectionSource); sb.Append(',');
        AppendCsv(sb, Mode); sb.Append(',');
        AppendCsv(sb, Subtype); sb.Append(',');
        AppendCsv(sb, Detail); sb.Append(',');
        AppendVec3(sb, AnchorPos); sb.Append(',');
        AppendQuat(sb, AnchorRot); sb.Append(',');
        AppendVec3(sb, HeadsetPos); sb.Append(',');
        AppendQuat(sb, HeadsetRot); sb.Append(',');
        AppendVec3(sb, ControllerLPos); sb.Append(',');
        AppendQuat(sb, ControllerLRot); sb.Append(',');
        AppendVec3(sb, ControllerRPos); sb.Append(',');
        AppendQuat(sb, ControllerRRot); sb.Append(',');
        AppendBool(sb, PositionValidL); sb.Append(',');
        AppendBool(sb, PositionValidR); sb.Append(',');
        AppendBool(sb, OrientationValidL); sb.Append(',');
        AppendBool(sb, OrientationValidR); sb.Append(',');
        AppendBool(sb, ConnectedL); sb.Append(',');
        AppendBool(sb, ConnectedR); sb.Append(',');
        AppendFloat(sb, VelocityLMps); sb.Append(',');
        AppendFloat(sb, VelocityRMps); sb.Append(',');
        AppendFloat(sb, BatteryLPercent); sb.Append(',');
        AppendFloat(sb, BatteryRPercent); sb.Append(',');
        AppendFloat(sb, InterControllerDistanceM); sb.Append(',');
        AppendFloat(sb, InterControllerRotationDeg); sb.Append(',');
        AppendFloat(sb, DeviationFromBaselineM); sb.Append(',');
        AppendFloat(sb, DeviationFromBaselineDeg); sb.Append(',');
        AppendBool(sb, ValidationEnforced); sb.Append(',');
        AppendCsv(sb, SleepEventType); sb.Append(',');
        AppendFloat(sb, TimeSinceLastPulseS); sb.Append(',');
        AppendInt(sb, WalkIndex); sb.Append(',');
        AppendCsv(sb, WalkPhase); sb.Append(',');
        AppendBool(sb, TrialActive); sb.Append(',');
        AppendBool(sb, MoveTowardsUser); sb.Append(',');
        AppendFloat(sb, TriggerDistanceM); sb.Append(',');
        AppendFloat(sb, PerturbationDistanceM); sb.Append(',');
        AppendFloat(sb, WalkDurationS); sb.Append(',');
        AppendInt(sb, CorrectionsAppliedCount); sb.Append(',');
        AppendFloat(sb, MaxCorrectionMagnitudeM); sb.Append(',');
        AppendCsv(sb, RejectionReasonHistogram); sb.Append(',');
        AppendCsv(sb, CalibrationStep); sb.Append(',');
        AppendInt(sb, CalibrationSampleIndex); sb.Append(',');
        AppendFloat(sb, MeanDistanceM); sb.Append(',');
        AppendFloat(sb, StddevDistanceM); sb.Append(',');
        AppendFloat(sb, MeanRotDeg); sb.Append(',');
        AppendFloat(sb, StddevRotDeg); sb.Append(',');
        AppendBool(sb, Accepted); sb.Append(',');
        AppendCsv(sb, RejectionReason); sb.Append(',');
        AppendFloat(sb, DeltaPositionM); sb.Append(',');
        AppendFloat(sb, DeltaRotationDeg); sb.Append(',');
        AppendFloat(sb, EmaAlphaApplied); sb.Append(',');
        AppendFloat(sb, CorrectionAppliedM); sb.Append(',');
        AppendFloat(sb, ControllerDistanceM); sb.Append(',');
        AppendFloat(sb, ControllerVelocityMps); sb.Append(',');
        AppendCsv(sb, ContextFor);
    }

    private static readonly char[] CsvSpecials = { ',', '"', '\n', '\r' };

    private static void AppendCsv(StringBuilder sb, string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        if (s.IndexOfAny(CsvSpecials) >= 0)
        {
            sb.Append('"').Append(s.Replace("\"", "\"\"")).Append('"');
        }
        else
        {
            sb.Append(s);
        }
    }

    private static void AppendFloat(StringBuilder sb, float? v)
    {
        if (v.HasValue) sb.Append(v.Value.ToString("R", Inv));
    }

    private static void AppendInt(StringBuilder sb, int? v)
    {
        if (v.HasValue) sb.Append(v.Value.ToString(Inv));
    }

    private static void AppendBool(StringBuilder sb, bool? v)
    {
        if (v.HasValue) sb.Append(v.Value ? "1" : "0");
    }

    private static void AppendVec3(StringBuilder sb, Vector3? v)
    {
        if (!v.HasValue) return;
        var p = v.Value;
        sb.Append(p.x.ToString("R", Inv)).Append('|')
          .Append(p.y.ToString("R", Inv)).Append('|')
          .Append(p.z.ToString("R", Inv));
    }

    private static void AppendQuat(StringBuilder sb, Quaternion? q)
    {
        if (!q.HasValue) return;
        var r = q.Value;
        sb.Append(r.x.ToString("R", Inv)).Append('|')
          .Append(r.y.ToString("R", Inv)).Append('|')
          .Append(r.z.ToString("R", Inv)).Append('|')
          .Append(r.w.ToString("R", Inv));
    }
}
