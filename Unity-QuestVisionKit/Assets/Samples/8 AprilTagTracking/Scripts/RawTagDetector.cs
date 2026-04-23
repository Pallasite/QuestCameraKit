using System;
using System.Collections.Generic;
using AprilTag.Interop;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;

/// <summary>
/// Pixel-space corner output of a single AprilTag detection. Image coordinates
/// follow the keijiro library convention: origin at the top-left of the image
/// passed to ProcessImage, +X right, +Y down.
///
/// Corners are ordered counter-clockwise around the tag face when viewed in the
/// image — matching the AprilTag library's native ordering, which corresponds
/// to tag-local positions: c0 = (-1,-1), c1 = (+1,-1), c2 = (+1,+1), c3 = (-1,+1).
/// </summary>
public struct RawTagDetection
{
    public int ID;
    public Vector2 Center;
    public Vector2 Corner0;
    public Vector2 Corner1;
    public Vector2 Corner2;
    public Vector2 Corner3;
    public float DecisionMargin;
}

/// <summary>
/// Minimal wrapper around AprilTag.Interop that exposes per-corner pixel
/// positions (which keijiro's TagDetector hides behind its TagPose-only API).
///
/// Only does detection — no monocular pose estimation, since the stereo
/// scanner triangulates corners directly. Lifecycle mirrors keijiro's
/// TagDetector: one instance per resolution, dispose when done.
/// </summary>
public sealed class RawTagDetector : IDisposable
{
    private Detector _detector;
    private Family _family;
    private ImageU8 _image;
    private readonly List<RawTagDetection> _detections = new();

    public int Width => _image.Width;
    public int Height => _image.Height;
    public IReadOnlyList<RawTagDetection> Detections => _detections;

    public RawTagDetector(int width, int height, int decimation = 2)
    {
        _detector = Detector.Create();
        _family = Family.CreateTagStandard41h12();
        _image = ImageU8.Create(width, height);

        _detector.ThreadCount = Math.Max(1, JobsUtility.JobWorkerCount);
        _detector.QuadDecimate = decimation;
        _detector.AddFamily(_family);
    }

    public void Dispose()
    {
        _detector?.RemoveFamily(_family);
        _detector?.Dispose();
        _family?.Dispose();
        _image?.Dispose();
        _detector = null;
        _family = null;
        _image = null;
    }

    public void ProcessImage(ReadOnlySpan<Color32> pixels)
    {
        ConvertToGrayscale(pixels, _image);

        using var detections = _detector.Detect(_image);
        _detections.Clear();
        for (int i = 0; i < detections.Length; i++)
        {
            ref var d = ref detections[i];
            _detections.Add(new RawTagDetection
            {
                ID = d.ID,
                DecisionMargin = d.DecisionMargin,
                Center = new Vector2((float)d.Center.x, (float)d.Center.y),
                Corner0 = new Vector2((float)d.Corner1.x, (float)d.Corner1.y),
                Corner1 = new Vector2((float)d.Corner2.x, (float)d.Corner2.y),
                Corner2 = new Vector2((float)d.Corner3.x, (float)d.Corner3.y),
                Corner3 = new Vector2((float)d.Corner4.x, (float)d.Corner4.y),
            });
        }
    }

    // Take the green channel as luminance — same convention as keijiro's
    // ImageConverter. The destination buffer is filled bottom-up because the
    // AsyncGPUReadback Color32 array is bottom-up but the AprilTag C library
    // expects top-down.
    private static void ConvertToGrayscale(ReadOnlySpan<Color32> src, ImageU8 dst)
    {
        int width = dst.Width;
        int height = dst.Height;
        int stride = dst.Stride;
        var buf = dst.Buffer;

        for (int y = 0; y < height; y++)
        {
            int srcRow = y * width;
            int dstRow = stride * (height - 1 - y);
            for (int x = 0; x < width; x++)
            {
                buf[dstRow + x] = src[srcRow + x].g;
            }
        }
    }
}
