using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using Meta.XR;
using AprilTag;

/// <summary>
/// Result data from a single AprilTag detection, including the tag's camera-space
/// pose from the AprilTag library plus the camera's world-space pose and intrinsics
/// needed for accurate 3D reconstruction.
/// </summary>
[Serializable]
public class AprilTagResult
{
    public int tagId;
    public Vector3 localPosition;   // Tag pose in camera space (from AprilTag library)
    public Quaternion localRotation; // Tag rotation in camera space
    public Pose cameraPose;          // Camera world-space pose at time of capture
    public PassthroughCameraAccess.CameraIntrinsics intrinsics;
    public Vector2Int captureResolution;
}

/// <summary>
/// Detects AprilTags (tagStandard41h12 family) in passthrough camera frames
/// using the keijiro AprilTag library with real camera intrinsics from
/// Meta's PassthroughCameraAccess API.
///
/// Mirrors QrCodeScanner's architecture: compute-shader downsampling,
/// AsyncGPUReadback for non-blocking pixel extraction, and per-frame
/// camera pose/intrinsics capture.
/// </summary>
public class AprilTagScanner : MonoBehaviour
{
    [Tooltip("Downsampling factor for the camera image. Higher = faster but lower accuracy.")]
    [SerializeField] private int sampleFactor = 2;

    [Tooltip("Physical size of the AprilTag in meters (edge-to-edge of the black border).")]
    [SerializeField] private float tagSizeMeters = 0.171f;

    private PassthroughCameraAccess _cameraAccess;
    private RenderTexture _downsampledTexture;
    private ComputeShader _downsampleShader;
    private TagDetector _tagDetector;
    private Vector2Int _lastDetectorResolution;
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
    }

    private void Awake()
    {
        _cameraAccess = GetComponent<PassthroughCameraAccess>();

        _downsampleShader = Resources.Load<ComputeShader>("DownsampleRGBA");
        if (_downsampleShader == null)
        {
            Debug.LogError("[AprilTagScanner] DownsampleRGBA.compute not found in a Resources folder.");
        }
    }

    private void OnDestroy()
    {
        _tagDetector?.Dispose();
        _tagDetector = null;

        if (_downsampledTexture != null)
        {
            _downsampledTexture.Release();
            Destroy(_downsampledTexture);
        }
    }

    /// <summary>
    /// Captures the current camera frame, runs AprilTag detection, and returns
    /// results with both local (camera-space) poses and the camera world pose.
    /// </summary>
    public async Task<AprilTagResult[]> ScanFrameAsync()
    {
        if (_isScanning || !_downsampleShader) return Array.Empty<AprilTagResult>();

        _isScanning = true;
        try
        {
            var frame = await AcquireFrameAsync();
            if (frame == null)
            {
                return Array.Empty<AprilTagResult>();
            }

            var (targetWidth, targetHeight) = GetTargetDimensions(frame.Value.Texture);
            if (!EnsureDownsampleTarget(targetWidth, targetHeight))
            {
                return Array.Empty<AprilTagResult>();
            }

            DispatchDownsample(frame.Value.Texture, targetWidth, targetHeight);
            var pixels = await ReadPixelsAsync(_downsampledTexture);
            if (pixels == null || pixels.Length == 0)
            {
                return Array.Empty<AprilTagResult>();
            }

            return DetectTags(frame.Value, pixels, targetWidth, targetHeight);
        }
        finally
        {
            _isScanning = false;
        }
    }

    private AprilTagResult[] DetectTags(CaptureFrame frame, Color32[] pixels, int width, int height)
    {
        // Create or recreate the detector if the resolution changed
        var res = new Vector2Int(width, height);
        if (_tagDetector == null || res != _lastDetectorResolution)
        {
            _tagDetector?.Dispose();
            _tagDetector = new TagDetector(width, height);
            _lastDetectorResolution = res;
        }

        // Scale intrinsics from sensor space to downsampled image space.
        //
        // The passthrough camera intrinsics (fx, fy, cx, cy) are defined in
        // sensor-pixel coordinates. The image we're processing has been:
        //   1. Cropped from the sensor (if current resolution != sensor resolution)
        //   2. Downsampled by sampleFactor
        //
        // We need to transform the intrinsics to match the downsampled image.
        var intrinsics = frame.Intrinsics;
        var sensorRes = (Vector2)intrinsics.SensorResolution;
        var currentRes = (Vector2)frame.Resolution;
        if (currentRes == Vector2.zero) currentRes = sensorRes;

        // Compute the crop region in sensor-pixel coordinates
        var crop = ComputeSensorCrop(sensorRes, currentRes);

        // The downsampled image corresponds to the crop region.
        // Scale factor: downsampled pixels / crop pixels (in sensor space)
        var scaleX = (float)width / crop.width;
        var scaleY = (float)height / crop.height;

        // Focal length scales linearly with pixel dimensions
        var fx = intrinsics.FocalLength.x * scaleX;
        var fy = intrinsics.FocalLength.y * scaleY;

        // Principal point must first be shifted relative to the crop origin,
        // then scaled to the downsampled dimensions
        var cx = (intrinsics.PrincipalPoint.x - crop.x) * scaleX;
        var cy = (intrinsics.PrincipalPoint.y - crop.y) * scaleY;

        // Run detection with real intrinsics
        _tagDetector.ProcessImage(new ReadOnlySpan<Color32>(pixels), fx, fy, cx, cy, tagSizeMeters);

        var results = new System.Collections.Generic.List<AprilTagResult>();
        foreach (var tag in _tagDetector.DetectedTags)
        {
            results.Add(new AprilTagResult
            {
                tagId = tag.ID,
                localPosition = tag.Position,
                localRotation = tag.Rotation,
                cameraPose = frame.Pose,
                intrinsics = frame.Intrinsics,
                captureResolution = frame.Resolution
            });
        }

        return results.ToArray();
    }

    private static Rect ComputeSensorCrop(Vector2 sensorResolution, Vector2 currentResolution)
    {
        if (sensorResolution == Vector2.zero)
        {
            return new Rect(0, 0, currentResolution.x, currentResolution.y);
        }

        var scaleFactor = new Vector2(
            currentResolution.x / sensorResolution.x,
            currentResolution.y / sensorResolution.y);
        var maxScale = Mathf.Max(scaleFactor.x, scaleFactor.y);
        if (maxScale <= 0) maxScale = 1f;
        scaleFactor /= maxScale;

        return new Rect(
            sensorResolution.x * (1f - scaleFactor.x) * 0.5f,
            sensorResolution.y * (1f - scaleFactor.y) * 0.5f,
            sensorResolution.x * scaleFactor.x,
            sensorResolution.y * scaleFactor.y);
    }

    private async Task<CaptureFrame?> AcquireFrameAsync()
    {
        while (true)
        {
            if (_cameraAccess && _cameraAccess.IsPlaying)
            {
                var texture = _cameraAccess.GetTexture();
                if (texture)
                {
                    return new CaptureFrame
                    {
                        Texture = texture,
                        Pose = _cameraAccess.GetCameraPose(),
                        Intrinsics = _cameraAccess.Intrinsics,
                        Resolution = _cameraAccess.CurrentResolution
                    };
                }
            }
            await Task.Delay(16);
        }
    }

    private (int width, int height) GetTargetDimensions(Texture texture)
    {
        var divisor = Mathf.Max(1, sampleFactor);
        return (Mathf.Max(1, texture.width / divisor), Mathf.Max(1, texture.height / divisor));
    }

    private bool EnsureDownsampleTarget(int width, int height)
    {
        if (_downsampledTexture && _downsampledTexture.width == width && _downsampledTexture.height == height)
        {
            return true;
        }

        if (_downsampledTexture)
        {
            _downsampledTexture.Release();
        }

        _downsampledTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
        {
            enableRandomWrite = true
        };
        _downsampledTexture.Create();
        return true;
    }

    private void DispatchDownsample(Texture source, int targetWidth, int targetHeight)
    {
        var kernel = _downsampleShader.FindKernel("CSMain");
        _downsampleShader.SetTexture(kernel, Input1, source);
        _downsampleShader.SetTexture(kernel, Output, _downsampledTexture);
        _downsampleShader.SetInt(InputWidth, source.width);
        _downsampleShader.SetInt(InputHeight, source.height);
        _downsampleShader.SetInt(OutputWidth, targetWidth);
        _downsampleShader.SetInt(OutputHeight, targetHeight);

        var threadGroupsX = Mathf.CeilToInt(targetWidth / 8f);
        var threadGroupsY = Mathf.CeilToInt(targetHeight / 8f);
        _downsampleShader.Dispatch(kernel, threadGroupsX, threadGroupsY, 1);
    }

    private Task<Color32[]> ReadPixelsAsync(RenderTexture rt)
    {
        var tcs = new TaskCompletionSource<Color32[]>();

        AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, request =>
        {
            if (request.hasError)
            {
                tcs.SetException(new Exception("[AprilTagScanner] GPU readback error."));
            }
            else
            {
                tcs.SetResult(request.GetData<Color32>().ToArray());
            }
        });
        return tcs.Task;
    }
}
