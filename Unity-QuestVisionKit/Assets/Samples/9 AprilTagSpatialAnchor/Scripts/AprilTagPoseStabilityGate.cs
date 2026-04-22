using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Sliding-window stability gate for a single AprilTag's observations.
/// Buffers recent (position, rotation, time) samples and reports whether the
/// buffer represents a pose stable enough to commit as an OVRSpatialAnchor.
///
/// "Stable" means:
///   - buffer is full (WindowSize samples), AND
///   - max distance of any sample's position from the centroid is within
///     MaxPositionSpreadMeters, AND
///   - max angular distance of any sample's rotation from the averaged
///     rotation is within MaxRotationSpreadDegrees.
///
/// If no sample arrives within MaxObservationAgeSeconds (the tag was lost),
/// the buffer is cleared on the next observation so gating restarts from zero.
///
/// This is a plain C# class, not a MonoBehaviour — one instance per tag ID,
/// owned by AprilTagAnchorManager.
/// </summary>
public class AprilTagPoseStabilityGate
{
    private struct Sample
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public float Time;
    }

    public int WindowSize { get; set; } = 10;
    public float MaxPositionSpreadMeters { get; set; } = 0.005f;
    public float MaxRotationSpreadDegrees { get; set; } = 2f;
    public float MaxObservationAgeSeconds { get; set; } = 0.5f;

    private readonly Queue<Sample> _buffer = new();
    private float _lastPositionSpread;
    private float _lastRotationSpread;

    public int SampleCount => _buffer.Count;
    public float LastPositionSpread => _lastPositionSpread;
    public float LastRotationSpread => _lastRotationSpread;

    public void Clear()
    {
        _buffer.Clear();
        _lastPositionSpread = 0f;
        _lastRotationSpread = 0f;
    }

    public void AddObservation(Vector3 worldPosition, Quaternion worldRotation, float time)
    {
        // Drop stale buffer if the tag was lost for too long.
        if (_buffer.Count > 0)
        {
            var last = PeekLast();
            if (time - last.Time > MaxObservationAgeSeconds)
            {
                _buffer.Clear();
            }
        }

        _buffer.Enqueue(new Sample
        {
            Position = worldPosition,
            Rotation = worldRotation,
            Time = time
        });

        while (_buffer.Count > WindowSize)
        {
            _buffer.Dequeue();
        }
    }

    /// <summary>
    /// Returns true if the buffer is full and all samples lie within the
    /// configured spread thresholds. Outputs the averaged pose.
    /// </summary>
    public bool IsStable(out Pose averagedPose)
    {
        averagedPose = default;

        if (_buffer.Count < WindowSize)
        {
            _lastPositionSpread = 0f;
            _lastRotationSpread = 0f;
            return false;
        }

        // Position centroid and spread
        var centroid = Vector3.zero;
        foreach (var s in _buffer) centroid += s.Position;
        centroid /= _buffer.Count;

        var maxPosDist = 0f;
        foreach (var s in _buffer)
        {
            var d = Vector3.Distance(s.Position, centroid);
            if (d > maxPosDist) maxPosDist = d;
        }
        _lastPositionSpread = maxPosDist;

        // Rotation average via incremental Slerp. Adequate for small angular
        // spreads, which are the only ones we accept as stable anyway.
        var rotAcc = Quaternion.identity;
        var idx = 0;
        foreach (var s in _buffer)
        {
            rotAcc = idx == 0 ? s.Rotation : Quaternion.Slerp(rotAcc, s.Rotation, 1f / (idx + 1f));
            idx++;
        }

        var maxRotDeg = 0f;
        foreach (var s in _buffer)
        {
            var ang = Quaternion.Angle(s.Rotation, rotAcc);
            if (ang > maxRotDeg) maxRotDeg = ang;
        }
        _lastRotationSpread = maxRotDeg;

        if (maxPosDist > MaxPositionSpreadMeters) return false;
        if (maxRotDeg > MaxRotationSpreadDegrees) return false;

        averagedPose = new Pose(centroid, rotAcc);
        return true;
    }

    private Sample PeekLast()
    {
        Sample last = default;
        foreach (var s in _buffer) last = s;
        return last;
    }
}
