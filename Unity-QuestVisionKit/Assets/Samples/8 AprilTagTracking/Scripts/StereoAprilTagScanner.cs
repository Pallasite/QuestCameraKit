using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Meta.XR;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Stereo-triangulating AprilTag scanner. Runs corner detection on both Quest 3
/// passthrough cameras in parallel, matches detections by tag ID, then
/// triangulates each of the four corners using the per-camera lens rays. The
/// tag pose is recovered from the four world-space corner positions, which
/// removes the per-eye depth bias that the monocular AprilTagScanner exhibits
/// (where any depth error along the active camera's optical axis manifests as
/// stereo parallax in the OTHER eye).
///
/// Setup:
/// 1. Add two PassthroughCameraAccess components — one with CameraPosition=Left
///    and one with CameraPosition=Right — and assign them to leftCamera and
///    rightCamera below.
/// 2. Place this component on the same GameObject as AprilTagDisplayManager.
///    The display manager auto-prefers a stereo scanner if one is present.
/// 3. Disable the monocular AprilTagScanner on the same GameObject (or remove
///    it) to avoid double-scanning.
///
/// The scanner falls back to skipping a frame when:
/// - either camera is not playing,
/// - the L and R capture timestamps are further apart than maxFrameTimeDeltaMs
///   (head motion would skew the triangulation),
/// - a tag is detected in only one eye (no triangulation possible).
/// </summary>
public class StereoAprilTagScanner : MonoBehaviour, IAprilTagScanner
{
    [Tooltip("Left passthrough camera — must have CameraPosition = Left.")]
    [SerializeField] private PassthroughCameraAccess leftCamera;

    [Tooltip("Right passthrough camera — must have CameraPosition = Right.")]
    [SerializeField] private PassthroughCameraAccess rightCamera;

    [Tooltip("Downsampling factor for both camera images in realtime mode. Higher = faster but lower corner accuracy.")]
    [SerializeField] private int sampleFactor = 2;

    [Tooltip("AprilTag quad decimation in realtime mode (1 = no decimation, slower but more accurate).")]
    [SerializeField] private int decimation = 2;

    [Tooltip("Reject detections below this AprilTag decision margin (lower = noisier).")]
    [SerializeField] private float minDecisionMargin = 30f;

    [Tooltip("Reject paired frames whose capture timestamps differ by more than this (in milliseconds). " +
             "Larger deltas mean the user moved their head between L and R captures and triangulation rays no longer share a moment.")]
    [SerializeField] private float maxFrameTimeDeltaMs = 30f;

    [Header("Calibration Mode (used by ScanCalibrationAsync)")]
    [Tooltip("Sample factor for one-shot high-quality scans. 1 = full camera resolution (recommended).")]
    [SerializeField] private int calibrationSampleFactor = 1;

    [Tooltip("AprilTag quad decimation for calibration scans. 1 = no decimation, full sub-pixel refinement.")]
    [SerializeField] private int calibrationDecimation = 1;

    [Tooltip("How many frame pairs to capture for a calibration scan. Variance drops with sqrt(N).")]
    [SerializeField] private int calibrationFrameCount = 16;

    private ComputeShader _downsampleShader;
    private RenderTexture _leftDownsampled;
    private RenderTexture _rightDownsampled;
    private RawTagDetector _leftDetector;
    private RawTagDetector _rightDetector;
    private Vector2Int _detectorResolution;
    private int _detectorDecimation = -1;
    private bool _isScanning;

    private static readonly int Input1 = Shader.PropertyToID("_Input");
    private static readonly int Output = Shader.PropertyToID("_Output");
    private static readonly int InputWidth = Shader.PropertyToID("_InputWidth");
    private static readonly int InputHeight = Shader.PropertyToID("_InputHeight");
    private static readonly int OutputWidth = Shader.PropertyToID("_OutputWidth");
    private static readonly int OutputHeight = Shader.PropertyToID("_OutputHeight");

    private struct CaptureFrame
    {
        public Texture Texture;
        public Pose Pose;
        public PassthroughCameraAccess.CameraIntrinsics Intrinsics;
        public Vector2Int Resolution;
        public DateTime Timestamp;
    }

    private struct ScaledIntrinsics
    {
        public float fx, fy, cx, cy;
    }

    private void Awake()
    {
        _downsampleShader = Resources.Load<ComputeShader>("DownsampleRGBA");
        if (!_downsampleShader)
        {
            Debug.LogError("[StereoAprilTagScanner] DownsampleRGBA.compute not found in a Resources folder.");
        }

        if (!leftCamera || !rightCamera)
        {
            Debug.LogError("[StereoAprilTagScanner] Both leftCamera and rightCamera must be assigned.");
        }
    }

    private void OnDestroy()
    {
        _leftDetector?.Dispose();
        _leftDetector = null;
        _rightDetector?.Dispose();
        _rightDetector = null;

        if (_leftDownsampled)
        {
            _leftDownsampled.Release();
            Destroy(_leftDownsampled);
        }
        if (_rightDownsampled)
        {
            _rightDownsampled.Release();
            Destroy(_rightDownsampled);
        }
    }

    public async Task<AprilTagResult[]> ScanFrameAsync()
    {
        if (_isScanning || !_downsampleShader || !leftCamera || !rightCamera)
        {
            return Array.Empty<AprilTagResult>();
        }

        _isScanning = true;
        try
        {
            var pair = await AcquirePairAsync();
            if (pair == null) return Array.Empty<AprilTagResult>();

            var (left, right) = pair.Value;

            var dtMs = Math.Abs((left.Timestamp - right.Timestamp).TotalMilliseconds);
            if (dtMs > maxFrameTimeDeltaMs) return Array.Empty<AprilTagResult>();

            return await DetectAndTriangulateAsync(left, right, sampleFactor, decimation);
        }
        finally
        {
            _isScanning = false;
        }
    }

    /// <summary>
    /// One-shot high-quality scan. Captures `calibrationFrameCount` synchronized
    /// frame pairs at `calibrationSampleFactor` / `calibrationDecimation` (typically
    /// no downsampling), triangulates each tag's corners per frame, then takes the
    /// component-wise median across frames before computing the final pose.
    /// Median is used instead of mean so a single bad frame can't pull the result.
    ///
    /// Heavy — typically 0.5-2 seconds depending on frameCount and resolution.
    /// Use for one-time anchor seeding or periodic drift correction, not per-frame.
    /// </summary>
    public async Task<AprilTagResult[]> ScanCalibrationAsync(int frameCount = -1, CancellationToken ct = default)
    {
        if (frameCount <= 0) frameCount = calibrationFrameCount;
        if (_isScanning || !_downsampleShader || !leftCamera || !rightCamera)
        {
            return Array.Empty<AprilTagResult>();
        }

        _isScanning = true;
        try
        {
            // Per tag: list of [4 world-space corner positions] across captured frames.
            var observations = new Dictionary<int, List<Vector3[]>>();
            int captured = 0;

            while (captured < frameCount)
            {
                ct.ThrowIfCancellationRequested();

                var pair = await AcquirePairAsync(ct);
                if (pair == null) break;
                var (left, right) = pair.Value;

                var dtMs = Math.Abs((left.Timestamp - right.Timestamp).TotalMilliseconds);
                if (dtMs > maxFrameTimeDeltaMs) continue;

                var perFrame = await DetectAndTriangulateAsync(left, right, calibrationSampleFactor, calibrationDecimation);
                if (perFrame.Length == 0)
                {
                    captured++;
                    continue;
                }

                foreach (var r in perFrame)
                {
                    if (!r.worldPoseOverride.HasValue || r.observedCorners == null) continue;
                    if (!observations.TryGetValue(r.tagId, out var list))
                    {
                        list = new List<Vector3[]>();
                        observations[r.tagId] = list;
                    }
                    list.Add(r.observedCorners);
                }

                captured++;
            }

            var results = new List<AprilTagResult>(observations.Count);
            foreach (var kvp in observations)
            {
                if (kvp.Value.Count == 0) continue;
                var medianCorners = new Vector3[4];
                for (int c = 0; c < 4; c++)
                {
                    medianCorners[c] = ComponentwiseMedian(kvp.Value, c);
                }
                var (pos, rot) = PoseFromCorners(medianCorners);
                results.Add(new AprilTagResult
                {
                    tagId = kvp.Key,
                    worldPoseOverride = new Pose(pos, rot),
                    cameraPose = Pose.identity,
                    intrinsics = leftCamera.Intrinsics,
                    captureResolution = leftCamera.CurrentResolution,
                    observedCorners = medianCorners,
                });
            }
            return results.ToArray();
        }
        finally
        {
            _isScanning = false;
        }
    }

    private async Task<AprilTagResult[]> DetectAndTriangulateAsync(
        CaptureFrame left, CaptureFrame right, int sampleFactorToUse, int decimationToUse)
    {
        var (targetW, targetH) = GetTargetDimensions(left.Texture, sampleFactorToUse);
        if (!EnsureResources(targetW, targetH, decimationToUse)) return Array.Empty<AprilTagResult>();

        DispatchDownsample(left.Texture, _leftDownsampled, targetW, targetH);
        DispatchDownsample(right.Texture, _rightDownsampled, targetW, targetH);

        var leftPixelsTask = ReadPixelsAsync(_leftDownsampled);
        var rightPixelsTask = ReadPixelsAsync(_rightDownsampled);
        await Task.WhenAll(leftPixelsTask, rightPixelsTask);

        var leftPixels = leftPixelsTask.Result;
        var rightPixels = rightPixelsTask.Result;
        if (leftPixels == null || rightPixels == null || leftPixels.Length == 0 || rightPixels.Length == 0)
        {
            return Array.Empty<AprilTagResult>();
        }

        _leftDetector.ProcessImage(new ReadOnlySpan<Color32>(leftPixels));
        _rightDetector.ProcessImage(new ReadOnlySpan<Color32>(rightPixels));

        return Triangulate(left, right, targetW, targetH);
    }

    private static Vector3 ComponentwiseMedian(List<Vector3[]> obs, int cornerIndex)
    {
        int n = obs.Count;
        var xs = new float[n];
        var ys = new float[n];
        var zs = new float[n];
        for (int i = 0; i < n; i++)
        {
            xs[i] = obs[i][cornerIndex].x;
            ys[i] = obs[i][cornerIndex].y;
            zs[i] = obs[i][cornerIndex].z;
        }
        Array.Sort(xs);
        Array.Sort(ys);
        Array.Sort(zs);
        int mid = n / 2;
        if ((n & 1) == 0)
        {
            return new Vector3(
                (xs[mid - 1] + xs[mid]) * 0.5f,
                (ys[mid - 1] + ys[mid]) * 0.5f,
                (zs[mid - 1] + zs[mid]) * 0.5f);
        }
        return new Vector3(xs[mid], ys[mid], zs[mid]);
    }

    private AprilTagResult[] Triangulate(CaptureFrame left, CaptureFrame right, int width, int height)
    {
        var leftIntr = ScaleIntrinsics(left.Intrinsics, left.Resolution, width, height);
        var rightIntr = ScaleIntrinsics(right.Intrinsics, right.Resolution, width, height);

        var rightById = new Dictionary<int, RawTagDetection>();
        foreach (var d in _rightDetector.Detections)
        {
            if (d.DecisionMargin < minDecisionMargin) continue;
            rightById[d.ID] = d;
        }

        var results = new List<AprilTagResult>();
        foreach (var leftDet in _leftDetector.Detections)
        {
            if (leftDet.DecisionMargin < minDecisionMargin) continue;
            if (!rightById.TryGetValue(leftDet.ID, out var rightDet)) continue;

            var worldCorners = new Vector3[4];
            for (int i = 0; i < 4; i++)
            {
                var lPx = GetCorner(leftDet, i);
                var rPx = GetCorner(rightDet, i);

                var lDir = PixelToWorldDirection(lPx, leftIntr, left.Pose.rotation);
                var rDir = PixelToWorldDirection(rPx, rightIntr, right.Pose.rotation);

                worldCorners[i] = TriangulateRays(left.Pose.position, lDir, right.Pose.position, rDir);
            }

            var (pos, rot) = PoseFromCorners(worldCorners);

            // Synthesize a "camera pose" at the midpoint of the two lenses for callers
            // (e.g. EnvironmentRaycast) that still want a ray origin.
            var midpoint = new Pose(
                (left.Pose.position + right.Pose.position) * 0.5f,
                Quaternion.Slerp(left.Pose.rotation, right.Pose.rotation, 0.5f));

            results.Add(new AprilTagResult
            {
                tagId = leftDet.ID,
                worldPoseOverride = new Pose(pos, rot),
                cameraPose = midpoint,
                intrinsics = left.Intrinsics,
                captureResolution = left.Resolution,
                observedCorners = worldCorners,
                // localPosition / localRotation left default — display manager uses
                // worldPoseOverride and ignores these when the override is set.
            });
        }

        return results.ToArray();
    }

    private static Vector2 GetCorner(RawTagDetection d, int i) => i switch
    {
        0 => d.Corner0,
        1 => d.Corner1,
        2 => d.Corner2,
        3 => d.Corner3,
        _ => default,
    };

    // OpenCV image convention: origin top-left, +X right, +Y down, +Z forward.
    // Lens pose from PassthroughCameraAccess is in Unity convention (+Y up,
    // +Z forward), so flip Y on the camera-space direction before rotating
    // it into world space.
    private static Vector3 PixelToWorldDirection(Vector2 pixel, ScaledIntrinsics intr, Quaternion lensRotation)
    {
        var openCv = new Vector3(
            (pixel.x - intr.cx) / intr.fx,
            (pixel.y - intr.cy) / intr.fy,
            1f);
        var unityCam = new Vector3(openCv.x, -openCv.y, openCv.z);
        return (lensRotation * unityCam).normalized;
    }

    // Closest-point triangulation of two skew rays. Solves the 2x2 system
    // from the partial derivatives of |L(t) - R(s)|^2 = 0, then returns the
    // midpoint of the connecting segment.
    private static Vector3 TriangulateRays(Vector3 origL, Vector3 dirL, Vector3 origR, Vector3 dirR)
    {
        var w0 = origL - origR;
        float a = Vector3.Dot(dirL, dirL);
        float b = Vector3.Dot(dirL, dirR);
        float c = Vector3.Dot(dirR, dirR);
        float d = Vector3.Dot(dirL, w0);
        float e = Vector3.Dot(dirR, w0);
        float denom = a * c - b * b;

        if (Mathf.Abs(denom) < 1e-6f)
        {
            // Parallel rays — fall back to the left ray midway estimate.
            return origL + dirL * Mathf.Max(0f, -d / Mathf.Max(a, 1e-6f));
        }

        float t = (b * e - c * d) / denom;
        float s = (a * e - b * d) / denom;
        return 0.5f * ((origL + dirL * t) + (origR + dirR * s));
    }

    // Tag-local corner layout (matches AprilTagWireframeDrawer):
    //   0 = (-0.5, -0.5, 0)   bottom-left
    //   1 = (+0.5, -0.5, 0)   bottom-right
    //   2 = (+0.5, +0.5, 0)   top-right
    //   3 = (-0.5, +0.5, 0)   top-left
    // Recover world axes by averaging the two parallel edges on each side.
    private static (Vector3 pos, Quaternion rot) PoseFromCorners(Vector3[] c)
    {
        var center = (c[0] + c[1] + c[2] + c[3]) * 0.25f;
        var right = ((c[1] - c[0]) + (c[2] - c[3])) * 0.5f;
        var up = ((c[3] - c[0]) + (c[2] - c[1])) * 0.5f;
        var forward = Vector3.Cross(right, up);
        if (forward.sqrMagnitude < 1e-8f || up.sqrMagnitude < 1e-8f)
        {
            return (center, Quaternion.identity);
        }
        return (center, Quaternion.LookRotation(forward.normalized, up.normalized));
    }

    private static ScaledIntrinsics ScaleIntrinsics(
        PassthroughCameraAccess.CameraIntrinsics intr, Vector2Int currentRes, int targetW, int targetH)
    {
        var sensorRes = (Vector2)intr.SensorResolution;
        var current = (Vector2)currentRes;
        if (current == Vector2.zero) current = sensorRes;

        var crop = ComputeSensorCrop(sensorRes, current);
        var scaleX = targetW / crop.width;
        var scaleY = targetH / crop.height;

        return new ScaledIntrinsics
        {
            fx = intr.FocalLength.x * scaleX,
            fy = intr.FocalLength.y * scaleY,
            cx = (intr.PrincipalPoint.x - crop.x) * scaleX,
            cy = (intr.PrincipalPoint.y - crop.y) * scaleY,
        };
    }

    private static Rect ComputeSensorCrop(Vector2 sensorRes, Vector2 current)
    {
        if (sensorRes == Vector2.zero) return new Rect(0, 0, current.x, current.y);
        var scale = new Vector2(current.x / sensorRes.x, current.y / sensorRes.y);
        var maxScale = Mathf.Max(scale.x, scale.y);
        if (maxScale <= 0) maxScale = 1f;
        scale /= maxScale;
        return new Rect(
            sensorRes.x * (1f - scale.x) * 0.5f,
            sensorRes.y * (1f - scale.y) * 0.5f,
            sensorRes.x * scale.x,
            sensorRes.y * scale.y);
    }

    private async Task<(CaptureFrame left, CaptureFrame right)?> AcquirePairAsync(CancellationToken ct = default)
    {
        // Poll until both cameras yield a frame this update. Same wait pattern
        // as the monocular scanner, just for the pair.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var l = TryGetFrame(leftCamera);
            var r = TryGetFrame(rightCamera);
            if (l.HasValue && r.HasValue) return (l.Value, r.Value);
            await Task.Delay(16, ct);
        }
    }

    private static CaptureFrame? TryGetFrame(PassthroughCameraAccess cam)
    {
        if (!cam || !cam.IsPlaying) return null;
        var tex = cam.GetTexture();
        if (!tex) return null;
        return new CaptureFrame
        {
            Texture = tex,
            Pose = cam.GetCameraPose(),
            Intrinsics = cam.Intrinsics,
            Resolution = cam.CurrentResolution,
            Timestamp = cam.Timestamp,
        };
    }

    private static (int width, int height) GetTargetDimensions(Texture texture, int sampleFactorToUse)
    {
        var divisor = Mathf.Max(1, sampleFactorToUse);
        return (Mathf.Max(1, texture.width / divisor), Mathf.Max(1, texture.height / divisor));
    }

    private bool EnsureResources(int width, int height, int decimationToUse)
    {
        var res = new Vector2Int(width, height);

        if (!_leftDownsampled || _leftDownsampled.width != width || _leftDownsampled.height != height)
        {
            if (_leftDownsampled) _leftDownsampled.Release();
            _leftDownsampled = CreateRT(width, height);
        }
        if (!_rightDownsampled || _rightDownsampled.width != width || _rightDownsampled.height != height)
        {
            if (_rightDownsampled) _rightDownsampled.Release();
            _rightDownsampled = CreateRT(width, height);
        }

        if (_leftDetector == null || _rightDetector == null
            || res != _detectorResolution || decimationToUse != _detectorDecimation)
        {
            _leftDetector?.Dispose();
            _rightDetector?.Dispose();
            _leftDetector = new RawTagDetector(width, height, decimationToUse);
            _rightDetector = new RawTagDetector(width, height, decimationToUse);
            _detectorResolution = res;
            _detectorDecimation = decimationToUse;
        }

        return true;
    }

    private static RenderTexture CreateRT(int width, int height)
    {
        var rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
        {
            enableRandomWrite = true,
        };
        rt.Create();
        return rt;
    }

    private void DispatchDownsample(Texture source, RenderTexture target, int targetWidth, int targetHeight)
    {
        var kernel = _downsampleShader.FindKernel("CSMain");
        _downsampleShader.SetTexture(kernel, Input1, source);
        _downsampleShader.SetTexture(kernel, Output, target);
        _downsampleShader.SetInt(InputWidth, source.width);
        _downsampleShader.SetInt(InputHeight, source.height);
        _downsampleShader.SetInt(OutputWidth, targetWidth);
        _downsampleShader.SetInt(OutputHeight, targetHeight);

        var threadGroupsX = Mathf.CeilToInt(targetWidth / 8f);
        var threadGroupsY = Mathf.CeilToInt(targetHeight / 8f);
        _downsampleShader.Dispatch(kernel, threadGroupsX, threadGroupsY, 1);
    }

    private static Task<Color32[]> ReadPixelsAsync(RenderTexture rt)
    {
        var tcs = new TaskCompletionSource<Color32[]>();
        AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, request =>
        {
            if (request.hasError)
            {
                tcs.SetException(new Exception("[StereoAprilTagScanner] GPU readback error."));
            }
            else
            {
                tcs.SetResult(request.GetData<Color32>().ToArray());
            }
        });
        return tcs.Task;
    }
}
