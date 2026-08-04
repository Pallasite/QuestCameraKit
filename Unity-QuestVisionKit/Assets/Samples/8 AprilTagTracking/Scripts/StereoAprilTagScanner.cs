using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Meta.XR;
using Unity.Collections;
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

    [Tooltip("When non-empty, per-frame scans keep only these tag IDs — other decoded tags " +
             "are dropped before triangulation and pose solving (the dominant per-tag cost). " +
             "Calibration sweeps ignore this filter: the constellation flow wants every tag. " +
             "Empty = keep all (legacy behavior).")]
    [SerializeField] private int[] targetTagIds = new int[0];

    [Tooltip("Reject paired frames whose capture timestamps differ by more than this (in milliseconds). " +
             "Larger deltas mean the user moved their head between L and R captures and triangulation rays no longer share a moment.")]
    [SerializeField] private float maxFrameTimeDeltaMs = 30f;

    [Header("Tag Geometry")]
    [Tooltip("Edge length of the tag's INTERIOR black square border in meters — the AprilTag " +
             "measurement zone for tagStandard41h12 (this family puts data bits OUTSIDE the " +
             "border, so the outer printed extent is the WRONG measurement; using it once put " +
             "the obstacle ~1 m under the floor). Caliper the interior square. Used by the " +
             "size-aware pose solvers (KabschRescaledRadial, KabschTemplateFit, StereoPnP) " +
             "and the corner residual diagnostic; a size mismatch becomes a pure RANGE error " +
             "of configured/measured, and ObstaclePlacementController warns on >15% deviation.")]
    [SerializeField] private float tagSizeMeters = 0.092f;

    [Header("Calibration Mode (used by ScanCalibrationAsync)")]
    [Tooltip("Sample factor for one-shot high-quality scans. 1 = full camera resolution (recommended).")]
    [SerializeField] private int calibrationSampleFactor = 1;

    [Tooltip("AprilTag quad decimation for calibration scans. 1 = no decimation, full sub-pixel refinement.")]
    [SerializeField] private int calibrationDecimation = 1;

    [Tooltip("How many frame pairs to capture for a calibration scan. Variance drops with sqrt(N).")]
    [SerializeField] private int calibrationFrameCount = 16;

    [Tooltip("Maximum time (seconds) to wait for both cameras to deliver a frame pair before giving up. " +
             "Prevents AcquirePairAsync from blocking indefinitely if a camera never comes online.")]
    [SerializeField] private float acquireTimeoutSeconds = 2f;

    [Header("Diagnostics")]
    [Tooltip("Pose-from-corners solver. Five modes form an accuracy/cost ladder for the methods-section " +
             "comparison: NaiveCross (cross-product, no size prior), Kabsch (planar Procrustes, no size prior), " +
             "KabschRescaledRadial (Kabsch + radial depth correction from tagSizeMeters), KabschTemplateFit " +
             "(Horn 1987 quaternion 3D Procrustes against the known-size template, augmenting plane normal), " +
             "and StereoPnP (Levenberg-Marquardt over 6-DOF pose minimizing per-eye pixel reprojection of the " +
             "rigid 4-corner template).")]
    [SerializeField] private RotationSolver rotationSolver = RotationSolver.Kabsch;

    /// <summary>
    /// Runtime accessor for the active pose solver. Lets experiments cycle
    /// through modes (e.g. via a UI toggle) without round-tripping through
    /// SerializedObject.
    /// </summary>
    public RotationSolver Solver
    {
        get => rotationSolver;
        set => rotationSolver = value;
    }

    /// <summary>
    /// Advances to the next RotationSolver mode, wrapping after StereoPnP,
    /// and returns the new mode. Used by the web console's cycleRotationSolver
    /// action for on-device solver comparison.
    /// </summary>
    public RotationSolver CycleSolver()
    {
        rotationSolver = (RotationSolver)(((int)rotationSolver + 1) % 5);
        return rotationSolver;
    }

    /// <summary>
    /// Runtime accessor for the physical tag edge length in meters. Used by
    /// the size-aware solvers (KabschRescaledRadial, KabschTemplateFit, StereoPnP).
    /// </summary>
    public float TagSizeMeters
    {
        get => tagSizeMeters;
        set => tagSizeMeters = value;
    }

    /// <summary>Per-frame downsampling divisor (1 = full camera resolution).</summary>
    public int SampleFactor
    {
        get => sampleFactor;
        set => sampleFactor = Mathf.Max(1, value);
    }

    /// <summary>Per-frame tag-ID whitelist; empty keeps every decoded tag.</summary>
    public int[] TargetTagIds
    {
        get => targetTagIds;
        set => targetTagIds = value ?? new int[0];
    }

    private bool IsTargetTag(int id)
    {
        var ids = targetTagIds;
        if (ids == null || ids.Length == 0) return true;
        for (int i = 0; i < ids.Length; i++)
            if (ids[i] == id) return true;
        return false;
    }

    public enum RotationSolver
    {
        NaiveCross,
        Kabsch,

        // Option 1: scale triangulated corners radially about the camera midpoint
        // so their mean edge length matches tagSizeMeters, then run Kabsch. Stereo
        // triangulation error is approximately a radial scaling from the cameras
        // (depth and lateral spread covary with depth), so a single isotropic
        // correction fixes the dominant depth bias for free.
        KabschRescaledRadial,

        // Option 2: Horn 1987 quaternion-based 3D Procrustes against the known-size
        // local template, augmented with an out-of-plane point along the world plane
        // normal to break the coplanar rank deficiency. Uses all 9 entries of the 3x3
        // cross-covariance (vs the 4 entries of the 2x2 in the planar Kabsch
        // specialization), so it can produce a different rotation when corner noise
        // has out-of-plane structure. Closed-form via Jacobi eigendecomposition on
        // the 4x4 symmetric N matrix.
        KabschTemplateFit,

        // Option 3: Levenberg-Marquardt over the 6-DOF tag pose minimizing the
        // pixel reprojection error of the rigid 4-corner template (size = tagSizeMeters)
        // in BOTH eyes simultaneously. 16 residuals (4 corners x 2 cameras x 2 image
        // axes) vs 6 unknowns (3 position + 3 axis-angle tangent-space rotation) is
        // the strongest constraint of the five modes — it uses raw pixel observations
        // directly without committing to intermediate triangulation. Initial guess
        // from Kabsch on triangulated corners.
        StereoPnP,
    }

    private ComputeShader _downsampleShader;
    private RenderTexture _leftDownsampled;
    private RenderTexture _rightDownsampled;
    private RawTagDetector _leftDetector;
    private RawTagDetector _rightDetector;
    private Vector2Int _detectorResolution;
    private int _detectorDecimation = -1;
    private bool _isScanning;

    // Persistent readback buffers. AsyncGPUReadback writes into the
    // NativeArrays; the main-thread callback memcpys them into the reusable
    // managed caches, which are what the Task.Run detection worker reads —
    // a worker thread must not touch a NativeArray (development-build safety
    // handles reject non-job-thread access). Reallocated only when the capture
    // resolution changes; replaces the previous multi-MB ToArray() per scan
    // (~20-26 MB/s of GC churn at 8 Hz). _detectTask is the in-flight
    // detection job; OnDestroy waits on it before disposing the detectors.
    private NativeArray<Color32> _leftReadback;
    private NativeArray<Color32> _rightReadback;
    private Color32[] _leftPixelCache;
    private Color32[] _rightPixelCache;
    private Task _detectTask;

    // Pre-allocated collections to avoid per-frame GC pressure.
    private readonly Dictionary<int, RawTagDetection> _rightById = new();
    private readonly List<AprilTagResult> _triangulateResults = new();

    // Per-detection scratch buffers reused across the dispatch in ResolvePose.
    // Pixel arrays hold the 4 corner observations from each eye (StereoPnP needs
    // them; the corner-only modes ignore them). worldCorners is the triangulated
    // 3D corner buffer that downstream solvers operate on. _residualCorners
    // snapshots the corners the solver consumed before RebuildCornersFromPose
    // overwrites them, so the residual measures fit quality rather than
    // template-vs-itself (which is identically zero).
    private readonly Vector2[] _leftPixels = new Vector2[4];
    private readonly Vector2[] _rightPixels = new Vector2[4];
    private readonly Vector3[] _residualCorners = new Vector3[4];

    // Jacobi 4x4 eigendecomposition workspace for KabschTemplateFit.
    private readonly float[,] _jacobiN = new float[4, 4];
    private readonly float[,] _jacobiV = new float[4, 4];

    // Levenberg-Marquardt workspace for StereoPnP. Sized for 16 residuals and
    // 6 unknowns (3 position + 3 axis-angle tangent-space rotation).
    private readonly float[] _pnpResiduals = new float[16];
    private readonly float[] _pnpResidualsTrial = new float[16];
    private readonly float[,] _pnpJacobian = new float[16, 6];
    private readonly float[,] _pnpHessian = new float[6, 6];
    private readonly float[] _pnpGradient = new float[6];
    private readonly float[,] _pnpAugmented = new float[6, 7];
    private readonly Vector3[] _pnpLocalCorners = new Vector3[4];

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

    #if UNITY_EDITOR
    private void OnValidate()
    {
        if (leftCamera && rightCamera
            && leftCamera.CameraPosition == rightCamera.CameraPosition)
        {
            Debug.LogWarning($"[StereoAprilTagScanner] Both cameras are set to {leftCamera.CameraPosition}. " +
                             "Assign one Left and one Right for stereo triangulation to work.");
        }
    }
    #endif

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
        // A detection task may still be inside the native detector — wait
        // (bounded) before disposing, or the P/Invoke would read freed memory.
        // Task exceptions surface at the await site, not here.
        try { _detectTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch { }

        // A pending RequestIntoNativeArray locks its target array; flush the
        // callbacks before disposing the buffers below.
        AsyncGPUReadback.WaitAllRequests();

        _leftDetector?.Dispose();
        _leftDetector = null;
        _rightDetector?.Dispose();
        _rightDetector = null;

        if (_leftReadback.IsCreated) _leftReadback.Dispose();
        if (_rightReadback.IsCreated) _rightReadback.Dispose();

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

            return await DetectAndTriangulateAsync(left, right, sampleFactor, decimation, applyTagFilter: true);
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
    public async Task<AprilTagResult[]> ScanCalibrationAsync(int frameCount = -1, CancellationToken ct = default, Action<int, int, int> onProgress = null)
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

                var perFrame = await DetectAndTriangulateAsync(left, right, calibrationSampleFactor, calibrationDecimation, applyTagFilter: false);
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
                // (capturedFrames, totalFrames, uniqueTagsSoFar)
                onProgress?.Invoke(captured, frameCount, observations.Count);
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
                float residual = (tagSizeMeters > 0f)
                    ? RigidTemplateResidualRms(medianCorners, pos, rot, tagSizeMeters)
                    : 0f;
                results.Add(new AprilTagResult
                {
                    tagId = kvp.Key,
                    worldPoseOverride = new Pose(pos, rot),
                    cameraPose = Pose.identity,
                    intrinsics = leftCamera.Intrinsics,
                    captureResolution = leftCamera.CurrentResolution,
                    observedCorners = medianCorners,
                    solverUsed = CalibrationEffectiveSolver(),
                    cornerResidualMeters = residual,
                    // measuredTagSizeMeters left 0: median corners are post-mutation
                    // geometry (rescaled/rebuilt per frame), so a size measured here
                    // would not be the raw triangulated scale.
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
        CaptureFrame left, CaptureFrame right, int sampleFactorToUse, int decimationToUse,
        bool applyTagFilter)
    {
        var (targetW, targetH) = GetTargetDimensions(left.Texture, sampleFactorToUse);
        if (!EnsureResources(targetW, targetH, decimationToUse)) return Array.Empty<AprilTagResult>();

        DispatchDownsample(left.Texture, _leftDownsampled, targetW, targetH);
        DispatchDownsample(right.Texture, _rightDownsampled, targetW, targetH);

        var leftPixelsTask = ReadPixelsIntoCacheAsync(_leftDownsampled, leftEye: true);
        var rightPixelsTask = ReadPixelsIntoCacheAsync(_rightDownsampled, leftEye: false);
        await Task.WhenAll(leftPixelsTask, rightPixelsTask);

        var leftPixels = leftPixelsTask.Result;
        var rightPixels = rightPixelsTask.Result;
        if (leftPixels == null || rightPixels == null || leftPixels.Length == 0 || rightPixels.Length == 0)
        {
            return Array.Empty<AprilTagResult>();
        }

        // Detection (grayscale conversion + native apriltag detect, BOTH eyes)
        // runs on a thread-pool worker so the main thread never stalls — the
        // synchronous version was the 90->60-70 fps dip near the obstacle.
        // Sequential per eye on purpose: parallel eyes would run two native
        // worker pools at once and re-create the core oversubscription the
        // RawTagDetector ThreadCount clamp removes. Detector refs are captured
        // as locals; OnDestroy Waits on _detectTask before disposing them, so
        // the worker can never see a freed detector.
        var ld = _leftDetector;
        var rd = _rightDetector;
        if (ld == null || rd == null) return Array.Empty<AprilTagResult>();
        _detectTask = Task.Run(() =>
        {
            ld.ProcessImage(new ReadOnlySpan<Color32>(leftPixels));
            rd.ProcessImage(new ReadOnlySpan<Color32>(rightPixels));
        });
        await _detectTask;

        // Scene teardown may have run while detection was off-thread.
        if (_leftDetector == null || _rightDetector == null)
        {
            return Array.Empty<AprilTagResult>();
        }

        return Triangulate(left, right, targetW, targetH, applyTagFilter);
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

    private AprilTagResult[] Triangulate(CaptureFrame left, CaptureFrame right, int width, int height,
                                         bool applyTagFilter)
    {
        var leftIntr = ScaleIntrinsics(left.Intrinsics, left.Resolution, width, height);
        var rightIntr = ScaleIntrinsics(right.Intrinsics, right.Resolution, width, height);

        _rightById.Clear();
        foreach (var d in _rightDetector.Detections)
        {
            if (d.DecisionMargin < minDecisionMargin) continue;
            if (applyTagFilter && !IsTargetTag(d.ID)) continue;
            _rightById[d.ID] = d;
        }

        // Synthesize a "camera pose" at the midpoint of the two lenses for callers
        // (e.g. EnvironmentRaycast) that still want a ray origin. The position is
        // also the radial-rescale anchor for KabschRescaledRadial.
        var midpointPos = (left.Pose.position + right.Pose.position) * 0.5f;
        var midpointRot = Quaternion.Slerp(left.Pose.rotation, right.Pose.rotation, 0.5f);

        _triangulateResults.Clear();
        foreach (var leftDet in _leftDetector.Detections)
        {
            if (leftDet.DecisionMargin < minDecisionMargin) continue;
            if (applyTagFilter && !IsTargetTag(leftDet.ID)) continue;
            if (!_rightById.TryGetValue(leftDet.ID, out var rightDet)) continue;

            // Capture per-corner pixel observations and triangulated 3D positions
            // up front. The pixels are needed by StereoPnP; the corner-only modes
            // ignore them.
            var worldCorners = new Vector3[4];
            for (int i = 0; i < 4; i++)
            {
                _leftPixels[i] = GetCorner(leftDet, i);
                _rightPixels[i] = GetCorner(rightDet, i);

                var lDir = PixelToWorldDirection(_leftPixels[i], leftIntr, left.Pose.rotation);
                var rDir = PixelToWorldDirection(_rightPixels[i], rightIntr, right.Pose.rotation);

                worldCorners[i] = TriangulateRays(left.Pose.position, lDir, right.Pose.position, rDir);
            }

            // Raw stereo-triangulated mean edge length, captured BEFORE ResolvePose
            // can rescale (KabschRescaledRadial) or rebuild (KabschTemplateFit,
            // StereoPnP) the corners — after those mutations the measured size
            // equals tagSizeMeters by construction and carries no information.
            float rawMeasuredSize = MeanEdgeLength(worldCorners);

            var (pos, rot, residual, effectiveSolver) = ResolvePose(
                worldCorners,
                _leftPixels, _rightPixels,
                leftIntr, rightIntr,
                left.Pose, right.Pose,
                midpointPos);

            _triangulateResults.Add(new AprilTagResult
            {
                tagId = leftDet.ID,
                worldPoseOverride = new Pose(pos, rot),
                cameraPose = new Pose(midpointPos, midpointRot),
                intrinsics = left.Intrinsics,
                captureResolution = left.Resolution,
                observedCorners = worldCorners,
                solverUsed = effectiveSolver,
                cornerResidualMeters = residual,
                measuredTagSizeMeters = rawMeasuredSize,
                // localPosition / localRotation left default — display manager uses
                // worldPoseOverride and ignores these when the override is set.
            });
        }

        return _triangulateResults.ToArray();
    }

    /// <summary>
    /// Single dispatch point for all five pose-extraction modes. Mutates the
    /// passed-in corners array in place when the mode is corner-rewriting
    /// (KabschRescaledRadial rescales radially; KabschTemplateFit and StereoPnP
    /// rebuild it as the rigid template at the fitted pose so observedCorners
    /// reflects the optimized geometry for the wireframe visualizer and for
    /// calibration's component-wise median across frames).
    ///
    /// The residual is computed against the corners the solver CONSUMED — for
    /// the rebuild modes those are snapshotted into _residualCorners before
    /// RebuildCornersFromPose overwrites them (previously the residual compared
    /// the rebuilt template against itself, which is identically zero). For
    /// KabschRescaledRadial the consumed corners are post-rescale, so its
    /// residual deliberately excludes the scale error that mode removes.
    ///
    /// effectiveSolver reports the solver that actually ran: when
    /// tagSizeMeters <= 0 the size-aware modes degrade to plain Kabsch
    /// internally, and the result is stamped accordingly.
    /// </summary>
    private (Vector3 pos, Quaternion rot, float cornerResidualMeters, RotationSolver effectiveSolver) ResolvePose(
        Vector3[] worldCorners,
        Vector2[] leftPixels, Vector2[] rightPixels,
        ScaledIntrinsics leftIntr, ScaledIntrinsics rightIntr,
        Pose leftCam, Pose rightCam,
        Vector3 cameraMidpoint)
    {
        Vector3 pos;
        Quaternion rot;
        var effective = rotationSolver;
        var residualSource = worldCorners;
        switch (rotationSolver)
        {
            case RotationSolver.NaiveCross:
                (pos, rot) = PoseFromCornersNaive(worldCorners);
                break;

            case RotationSolver.KabschRescaledRadial:
                if (tagSizeMeters > 0f)
                {
                    RescaleCornersRadially(worldCorners, cameraMidpoint, tagSizeMeters);
                }
                else
                {
                    effective = RotationSolver.Kabsch;
                }
                (pos, rot) = PoseFromCornersKabsch(worldCorners);
                break;

            case RotationSolver.KabschTemplateFit:
                if (tagSizeMeters <= 0f) effective = RotationSolver.Kabsch;
                (pos, rot) = PoseFromCornersTemplateFit(worldCorners, tagSizeMeters);
                Array.Copy(worldCorners, _residualCorners, 4);
                residualSource = _residualCorners;
                RebuildCornersFromPose(worldCorners, pos, rot, tagSizeMeters);
                break;

            case RotationSolver.StereoPnP:
                {
                    if (tagSizeMeters <= 0f) effective = RotationSolver.Kabsch;
                    var (initPos, initRot) = PoseFromCornersKabsch(worldCorners);
                    (pos, rot) = StereoPnPRefine(
                        initPos, initRot,
                        leftPixels, rightPixels,
                        leftIntr, rightIntr,
                        leftCam, rightCam,
                        tagSizeMeters);
                    Array.Copy(worldCorners, _residualCorners, 4);
                    residualSource = _residualCorners;
                    RebuildCornersFromPose(worldCorners, pos, rot, tagSizeMeters);
                }
                break;

            case RotationSolver.Kabsch:
            default:
                (pos, rot) = PoseFromCornersKabsch(worldCorners);
                break;
        }

        float residual = (tagSizeMeters > 0f)
            ? RigidTemplateResidualRms(residualSource, pos, rot, tagSizeMeters)
            : 0f;

        return (pos, rot, residual, effective);
    }

    /// <summary>
    /// RMS distance between the corners the solver consumed and the rigid
    /// template (size = tagSize) placed at the fitted pose. Comparable across
    /// all five solver modes — for StereoPnP this measures 3D corner misfit
    /// even though LM internally minimizes pixel error — with one caveat:
    /// KabschRescaledRadial's consumed corners are post-rescale, so its
    /// residual excludes the scale error that mode removes by construction
    /// (compare its POSITION against other modes, not this residual alone).
    /// </summary>
    private static float RigidTemplateResidualRms(Vector3[] corners, Vector3 pos, Quaternion rot, float tagSize)
    {
        float h = tagSize * 0.5f;
        var l0 = pos + rot * new Vector3(-h, -h, 0f);
        var l1 = pos + rot * new Vector3(+h, -h, 0f);
        var l2 = pos + rot * new Vector3(+h, +h, 0f);
        var l3 = pos + rot * new Vector3(-h, +h, 0f);

        float sum =
            (corners[0] - l0).sqrMagnitude +
            (corners[1] - l1).sqrMagnitude +
            (corners[2] - l2).sqrMagnitude +
            (corners[3] - l3).sqrMagnitude;
        return Mathf.Sqrt(sum * 0.25f);
    }

    /// <summary>
    /// Overwrites corners with the rigid template (size = tagSize) at the given
    /// pose. Used by KabschTemplateFit and StereoPnP so observedCorners reflects
    /// the optimized rigid geometry rather than the raw triangulated points.
    /// </summary>
    private static void RebuildCornersFromPose(Vector3[] corners, Vector3 pos, Quaternion rot, float tagSize)
    {
        if (tagSize <= 0f) return;
        float h = tagSize * 0.5f;
        corners[0] = pos + rot * new Vector3(-h, -h, 0f);
        corners[1] = pos + rot * new Vector3(+h, -h, 0f);
        corners[2] = pos + rot * new Vector3(+h, +h, 0f);
        corners[3] = pos + rot * new Vector3(-h, +h, 0f);
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
    /// <summary>
    /// Corner-only pose dispatcher used by ScanCalibrationAsync's final fit
    /// (which medians per-frame observedCorners and has no surviving pixel
    /// observations, so StereoPnP is unavailable). KabschRescaledRadial also
    /// has no surviving per-frame camera midpoint here, so it falls through
    /// to plain Kabsch — the per-frame corners are already radially rescaled
    /// at capture time, so the median is already in size-corrected geometry.
    /// Likewise StereoPnP's per-frame corners are the rigid template rebuilt
    /// at each frame's PnP pose, so the Kabsch fit here is an aggregation of
    /// per-frame PnP results. The calibration result's solverUsed therefore
    /// reports the PIPELINE that produced the per-frame geometry (see
    /// CalibrationEffectiveSolver), not this final aggregation fit.
    /// </summary>
    private (Vector3 pos, Quaternion rot) PoseFromCorners(Vector3[] c)
    {
        return rotationSolver switch
        {
            RotationSolver.NaiveCross => PoseFromCornersNaive(c),
            RotationSolver.KabschTemplateFit => PoseFromCornersTemplateFit(c, tagSizeMeters),
            _ => PoseFromCornersKabsch(c),
        };
    }

    /// <summary>
    /// Effective solver label for calibration results: the configured pipeline
    /// when tagSizeMeters is valid, otherwise the mode the size-aware pipelines
    /// actually degrade to (plain Kabsch; NaiveCross is size-free and unaffected).
    /// </summary>
    private RotationSolver CalibrationEffectiveSolver()
    {
        if (tagSizeMeters > 0f) return rotationSolver;
        return rotationSolver == RotationSolver.NaiveCross
            ? RotationSolver.NaiveCross
            : RotationSolver.Kabsch;
    }

    /// <summary>Mean of the 4 edge lengths of a corner quad.</summary>
    private static float MeanEdgeLength(Vector3[] c)
        => (Vector3.Distance(c[0], c[1]) + Vector3.Distance(c[1], c[2]) +
            Vector3.Distance(c[2], c[3]) + Vector3.Distance(c[3], c[0])) * 0.25f;

    // Stereo triangulation error is approximately a radial scaling about the camera
    // midpoint: depth uncertainty along the gaze axis is the dominant noise term, and
    // the world-space lateral spread of the corners scales with that depth (since
    // pixel angle × depth = world distance). One isotropic rescale that pins the mean
    // edge length to the known tagSize therefore corrects depth bias and the mirrored
    // lateral spread in a single pass. Mutates corners in place.
    private static void RescaleCornersRadially(Vector3[] c, Vector3 cameraMidpoint, float tagSize)
    {
        float meanEdge = MeanEdgeLength(c);
        if (meanEdge < 1e-6f) return;
        float scale = tagSize / meanEdge;
        for (int i = 0; i < 4; i++)
        {
            c[i] = cameraMidpoint + (c[i] - cameraMidpoint) * scale;
        }
    }

    // Original method: averaged parallel edges + Quaternion.LookRotation. Kept as a
    // diagnostic toggle so on-device A/B comparison is one inspector click.
    // LookRotation only enforces the forward axis exactly and projects up onto its
    // perpendicular, so the in-plane rotation is sensitive to which corners' noise
    // happens to dominate — that's the source of the rotation jitter.
    private static (Vector3 pos, Quaternion rot) PoseFromCornersNaive(Vector3[] c)
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

    // Optimal rigid fit (Kabsch / Procrustes) specialized for a planar 4-corner
    // target. Standard 3D Kabsch via SVD is rank-deficient when the source points
    // are coplanar (our case), so we use a geometric specialization:
    //   1. Centroid → position.
    //   2. Plane normal from cross of diagonals (and a sign check against the
    //      tag-local +Z direction implied by corner ordering).
    //   3. Project corners into the plane, then a 2D Procrustes solve gives the
    //      in-plane rotation θ in closed form.
    //   4. Rebuild the world basis vectors from θ and the plane normal, then a
    //      LookRotation that's now mathematically exact (up is constructed
    //      perpendicular to forward, so no projection is applied).
    // Uses all 8 in-plane and 4 out-of-plane measurements jointly, which is what
    // the naive cross method doesn't do.
    private static (Vector3 pos, Quaternion rot) PoseFromCornersKabsch(Vector3[] c)
    {
        var centroid = (c[0] + c[1] + c[2] + c[3]) * 0.25f;

        // Plane normal — cross of diagonals is well-conditioned for a near-square
        // pattern even with corner noise.
        var diag1 = c[2] - c[0];
        var diag2 = c[3] - c[1];
        var normal = Vector3.Cross(diag1, diag2);
        if (normal.sqrMagnitude < 1e-10f) return (centroid, Quaternion.identity);
        normal.Normalize();

        // Sign check: tag-local +Z should map to +normal. Tag-local +Z is
        // cross(local +X, local +Y) = cross(c1-c0, c3-c0) (the bottom and left
        // edges). Flip if the implied normal points the other way.
        var impliedZ = Vector3.Cross(c[1] - c[0], c[3] - c[0]);
        if (Vector3.Dot(impliedZ, normal) < 0f) normal = -normal;

        // In-plane reference basis (u, v). Pick u along bottom edge projected
        // onto plane; v completes a right-handed frame with normal.
        var bottomEdge = c[1] - c[0];
        var u = bottomEdge - Vector3.Dot(bottomEdge, normal) * normal;
        if (u.sqrMagnitude < 1e-10f) return (centroid, Quaternion.identity);
        u.Normalize();
        var v = Vector3.Cross(normal, u);

        // Project each world corner into the (u, v) frame relative to centroid.
        // Tag-local corner positions in 2D (XY plane, centered):
        //   l0 = (-0.5, -0.5), l1 = (+0.5, -0.5), l2 = (+0.5, +0.5), l3 = (-0.5, +0.5)
        // Closed-form 2D Procrustes for the rotation θ that aligns local → projected:
        //   θ* = atan2( Σ (l.x·p.y − l.y·p.x), Σ (l.x·p.x + l.y·p.y) )
        float num = 0f, den = 0f;
        var localXY = LocalCornerXY;
        for (int i = 0; i < 4; i++)
        {
            var rel = c[i] - centroid;
            float pu = Vector3.Dot(rel, u);
            float pv = Vector3.Dot(rel, v);
            num += localXY[i].x * pv - localXY[i].y * pu;
            den += localXY[i].x * pu + localXY[i].y * pv;
        }
        float theta = Mathf.Atan2(num, den);
        float cos = Mathf.Cos(theta), sin = Mathf.Sin(theta);

        // World direction of tag-local +Y (used as upwards to LookRotation).
        // Tag-local +X maps to (cos·u + sin·v) — not needed here since LookRotation
        // derives it from forward × upwards.
        var worldUp = -sin * u + cos * v;

        return (centroid, Quaternion.LookRotation(normal, worldUp));
    }

    private static readonly Vector2[] LocalCornerXY =
    {
        new Vector2(-0.5f, -0.5f),
        new Vector2(+0.5f, -0.5f),
        new Vector2(+0.5f, +0.5f),
        new Vector2(-0.5f, +0.5f),
    };

    // Horn 1987 quaternion-based 3D Procrustes against the known-size local
    // template. Differs from PoseFromCornersKabsch in that it uses a full 3D
    // Procrustes formulation — including the out-of-plane covariance terms —
    // rather than the closed-form planar specialization. The 4 coplanar corners
    // are augmented with a synthetic 5th point along the world plane normal at
    // distance tagSize/2 to break the rank deficiency that planar SVD would
    // otherwise hit. The optimal rotation is the unit quaternion corresponding
    // to the largest eigenvalue of Horn's 4x4 symmetric N matrix; we extract it
    // via Jacobi rotations in JacobiEigen4x4.
    //
    // Position is the centroid of the 4 main world corners (same as Kabsch);
    // a rigid Procrustes fit's optimal translation under L2 is always the
    // target centroid when the source centroid is at the origin.
    private (Vector3 pos, Quaternion rot) PoseFromCornersTemplateFit(Vector3[] c, float tagSize)
    {
        if (tagSize <= 0f) return PoseFromCornersKabsch(c);

        var centroid = (c[0] + c[1] + c[2] + c[3]) * 0.25f;

        // World plane normal from cross of diagonals — sign-checked against the
        // implied tag-local +Z so the augmenting point lands on the correct side.
        var diag1 = c[2] - c[0];
        var diag2 = c[3] - c[1];
        var normal = Vector3.Cross(diag1, diag2);
        if (normal.sqrMagnitude < 1e-10f) return (centroid, Quaternion.identity);
        normal.Normalize();
        var impliedZ = Vector3.Cross(c[1] - c[0], c[3] - c[0]);
        if (Vector3.Dot(impliedZ, normal) < 0f) normal = -normal;

        // Source = local template (centered at origin, tagSize × tagSize) plus
        // a 5th point along +Z. Target = world corners centered at centroid plus
        // a 5th point along the world normal at the same scale.
        float h = tagSize * 0.5f;
        float aug = tagSize * 0.5f;

        // Build the cross-covariance M = Σ s_i ⊗ t_i (3x3, NOT symmetric).
        // The 5 (source, target) pairs are inlined to keep the per-tag cost flat.
        var t0 = c[0] - centroid;
        var t1 = c[1] - centroid;
        var t2 = c[2] - centroid;
        var t3 = c[3] - centroid;
        var t4 = normal * aug;

        // Source vectors: (-h,-h,0), (+h,-h,0), (+h,+h,0), (-h,+h,0), (0,0,aug)
        // M[i,j] = sum_k s_k[i] * t_k[j]
        float Mxx = (-h) * t0.x + (+h) * t1.x + (+h) * t2.x + (-h) * t3.x;
        float Mxy = (-h) * t0.y + (+h) * t1.y + (+h) * t2.y + (-h) * t3.y;
        float Mxz = (-h) * t0.z + (+h) * t1.z + (+h) * t2.z + (-h) * t3.z;
        float Myx = (-h) * t0.x + (-h) * t1.x + (+h) * t2.x + (+h) * t3.x;
        float Myy = (-h) * t0.y + (-h) * t1.y + (+h) * t2.y + (+h) * t3.y;
        float Myz = (-h) * t0.z + (-h) * t1.z + (+h) * t2.z + (+h) * t3.z;
        float Mzx = aug * t4.x;
        float Mzy = aug * t4.y;
        float Mzz = aug * t4.z;

        // Horn's 4x4 symmetric N matrix (eigenvector for largest eigenvalue is
        // the optimal quaternion (qw, qx, qy, qz)).
        var N = _jacobiN;
        N[0, 0] = Mxx + Myy + Mzz;
        N[0, 1] = Myz - Mzy; N[1, 0] = N[0, 1];
        N[0, 2] = Mzx - Mxz; N[2, 0] = N[0, 2];
        N[0, 3] = Mxy - Myx; N[3, 0] = N[0, 3];
        N[1, 1] = Mxx - Myy - Mzz;
        N[1, 2] = Mxy + Myx; N[2, 1] = N[1, 2];
        N[1, 3] = Mzx + Mxz; N[3, 1] = N[1, 3];
        N[2, 2] = -Mxx + Myy - Mzz;
        N[2, 3] = Myz + Mzy; N[3, 2] = N[2, 3];
        N[3, 3] = -Mxx - Myy + Mzz;

        var V = _jacobiV;
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                V[i, j] = (i == j) ? 1f : 0f;

        JacobiEigen4x4(N, V);

        // Largest eigenvalue's eigenvector is column maxIdx of V after Jacobi.
        int maxIdx = 0;
        float maxEig = N[0, 0];
        for (int i = 1; i < 4; i++)
        {
            if (N[i, i] > maxEig) { maxEig = N[i, i]; maxIdx = i; }
        }

        float qw = V[0, maxIdx];
        float qx = V[1, maxIdx];
        float qy = V[2, maxIdx];
        float qz = V[3, maxIdx];

        // Renormalize to defend against accumulated rounding inside the sweep.
        float qNorm = Mathf.Sqrt(qw * qw + qx * qx + qy * qy + qz * qz);
        if (qNorm < 1e-10f) return (centroid, Quaternion.identity);
        float invQ = 1f / qNorm;

        // Unity Quaternion is (x, y, z, w); Horn's convention is (w, x, y, z).
        return (centroid, new Quaternion(qx * invQ, qy * invQ, qz * invQ, qw * invQ));
    }

    /// <summary>
    /// In-place Jacobi diagonalization of a 4x4 real symmetric matrix. After
    /// convergence, A is diagonal (eigenvalues on the diagonal) and V holds
    /// the orthonormal eigenvectors as columns. ~30 sweeps is enough headroom
    /// for full convergence on Horn's N matrix from a 5-point Procrustes set.
    /// </summary>
    private static void JacobiEigen4x4(float[,] A, float[,] V)
    {
        const int maxSweeps = 30;
        const float eps = 1e-9f;

        for (int sweep = 0; sweep < maxSweeps; sweep++)
        {
            // Pick the largest off-diagonal entry to zero this sweep.
            int p = 0, q = 1;
            float maxOff = Mathf.Abs(A[0, 1]);
            for (int i = 0; i < 4; i++)
            {
                for (int j = i + 1; j < 4; j++)
                {
                    float a = Mathf.Abs(A[i, j]);
                    if (a > maxOff) { maxOff = a; p = i; q = j; }
                }
            }
            if (maxOff < eps) break;

            float app = A[p, p];
            float aqq = A[q, q];
            float apq = A[p, q];

            // Standard Jacobi rotation angle: tan(2θ) = 2·Apq / (Aqq − App).
            float theta = (aqq - app) / (2f * apq);
            float t = (theta >= 0f)
                ? 1f / (theta + Mathf.Sqrt(1f + theta * theta))
                : 1f / (theta - Mathf.Sqrt(1f + theta * theta));
            float ct = 1f / Mathf.Sqrt(1f + t * t);
            float st = t * ct;

            // Update A in place.
            A[p, p] = app - t * apq;
            A[q, q] = aqq + t * apq;
            A[p, q] = 0f; A[q, p] = 0f;
            for (int i = 0; i < 4; i++)
            {
                if (i == p || i == q) continue;
                float aip = A[i, p];
                float aiq = A[i, q];
                A[i, p] = ct * aip - st * aiq; A[p, i] = A[i, p];
                A[i, q] = st * aip + ct * aiq; A[q, i] = A[i, q];
            }

            // Accumulate the rotation in V (eigenvectors as columns).
            for (int i = 0; i < 4; i++)
            {
                float vip = V[i, p];
                float viq = V[i, q];
                V[i, p] = ct * vip - st * viq;
                V[i, q] = st * vip + ct * viq;
            }
        }
    }

    // Levenberg-Marquardt refinement of a 6-DOF tag pose minimizing the per-eye
    // pixel reprojection error of the rigid 4-corner template. Parametrization:
    // 3 position deltas + 3 axis-angle tangent-space rotation deltas; after each
    // accepted step the rotation update is "frozen" into the current quaternion
    // (right-multiply: R ← exp(δω) · R) and δω resets to 0. The numerical
    // Jacobian uses forward differences with eps_t = 1e-4 m and eps_r = 1e-4 rad.
    //
    // Initial guess comes from PoseFromCornersKabsch on the triangulated corners.
    // 16 residuals (4 corners × 2 cameras × 2 image axes) vs 6 unknowns is the
    // strongest constraint of the five solver modes.
    private (Vector3 pos, Quaternion rot) StereoPnPRefine(
        Vector3 initialPos, Quaternion initialRot,
        Vector2[] leftPixels, Vector2[] rightPixels,
        ScaledIntrinsics leftIntr, ScaledIntrinsics rightIntr,
        Pose leftCam, Pose rightCam,
        float tagSize,
        int maxIters = 20)
    {
        if (tagSize <= 0f) return (initialPos, initialRot);

        // Local template at known size (matches the corner ordering used elsewhere).
        float h = tagSize * 0.5f;
        _pnpLocalCorners[0] = new Vector3(-h, -h, 0f);
        _pnpLocalCorners[1] = new Vector3(+h, -h, 0f);
        _pnpLocalCorners[2] = new Vector3(+h, +h, 0f);
        _pnpLocalCorners[3] = new Vector3(-h, +h, 0f);

        var leftWorldToCam = Quaternion.Inverse(leftCam.rotation);
        var rightWorldToCam = Quaternion.Inverse(rightCam.rotation);

        Vector3 t = initialPos;
        Quaternion R = initialRot;

        const float epsT = 1e-4f;
        const float epsR = 1e-4f;
        float lambda = 1e-3f;

        float prevCost = ComputeReprojResiduals(
            t, R, _pnpLocalCorners,
            leftPixels, rightPixels,
            leftIntr, rightIntr,
            leftCam, rightCam,
            leftWorldToCam, rightWorldToCam,
            _pnpResiduals);

        for (int iter = 0; iter < maxIters; iter++)
        {
            // Jacobian columns 0..2: position deltas.
            for (int k = 0; k < 3; k++)
            {
                Vector3 tPert = t;
                tPert[k] += epsT;
                ComputeReprojResiduals(
                    tPert, R, _pnpLocalCorners,
                    leftPixels, rightPixels,
                    leftIntr, rightIntr,
                    leftCam, rightCam,
                    leftWorldToCam, rightWorldToCam,
                    _pnpResidualsTrial);
                for (int r = 0; r < 16; r++)
                {
                    _pnpJacobian[r, k] = (_pnpResidualsTrial[r] - _pnpResiduals[r]) / epsT;
                }
            }

            // Jacobian columns 3..5: tangent-space rotation deltas (axis-angle).
            for (int k = 0; k < 3; k++)
            {
                Vector3 axis = Vector3.zero;
                axis[k] = 1f;
                var dR = Quaternion.AngleAxis(epsR * Mathf.Rad2Deg, axis);
                var rPert = dR * R;
                ComputeReprojResiduals(
                    t, rPert, _pnpLocalCorners,
                    leftPixels, rightPixels,
                    leftIntr, rightIntr,
                    leftCam, rightCam,
                    leftWorldToCam, rightWorldToCam,
                    _pnpResidualsTrial);
                for (int r = 0; r < 16; r++)
                {
                    _pnpJacobian[r, k + 3] = (_pnpResidualsTrial[r] - _pnpResiduals[r]) / epsR;
                }
            }

            // Build H = JᵀJ and g = Jᵀr.
            for (int i = 0; i < 6; i++)
            {
                for (int j = i; j < 6; j++)
                {
                    float s = 0f;
                    for (int r = 0; r < 16; r++) s += _pnpJacobian[r, i] * _pnpJacobian[r, j];
                    _pnpHessian[i, j] = s;
                    _pnpHessian[j, i] = s;
                }
                float gi = 0f;
                for (int r = 0; r < 16; r++) gi += _pnpJacobian[r, i] * _pnpResiduals[r];
                _pnpGradient[i] = gi;
            }

            // Marquardt damping: scale diagonal by (1 + λ).
            for (int i = 0; i < 6; i++) _pnpHessian[i, i] *= (1f + lambda);

            // Solve H δ = −g via Gauss-Jordan.
            if (!SolveLinear6x6(_pnpHessian, _pnpGradient, _pnpAugmented, out var dt0, out var dt1, out var dt2,
                                                                              out var dr0, out var dr1, out var dr2))
            {
                break; // singular — give up with the best estimate so far
            }

            var tNew = new Vector3(t.x + dt0, t.y + dt1, t.z + dt2);
            Vector3 omega = new Vector3(dr0, dr1, dr2);
            float omegaMag = omega.magnitude;
            Quaternion rNew = (omegaMag < 1e-9f)
                ? R
                : Quaternion.AngleAxis(omegaMag * Mathf.Rad2Deg, omega / omegaMag) * R;

            float newCost = ComputeReprojResiduals(
                tNew, rNew, _pnpLocalCorners,
                leftPixels, rightPixels,
                leftIntr, rightIntr,
                leftCam, rightCam,
                leftWorldToCam, rightWorldToCam,
                _pnpResidualsTrial);

            if (newCost < prevCost)
            {
                t = tNew;
                R = rNew;
                Buffer.BlockCopy(_pnpResidualsTrial, 0, _pnpResiduals, 0, 16 * sizeof(float));
                lambda *= 0.5f;
                if ((prevCost - newCost) / Mathf.Max(prevCost, 1e-9f) < 1e-6f) break;
                prevCost = newCost;
            }
            else
            {
                lambda *= 4f;
                if (lambda > 1e6f) break;
            }
        }

        return (t, R);
    }

    /// <summary>
    /// Projects the rigid template at (t, R) into both cameras and writes
    /// 16 pixel residuals (4 corners × 2 eyes × 2 axes). Returns the L2 cost.
    /// Y is flipped between Unity (+Y up) and OpenCV (+Y down) — same convention
    /// as PixelToWorldDirection so the forward and inverse projections are
    /// consistent.
    /// </summary>
    private static float ComputeReprojResiduals(
        Vector3 t, Quaternion R, Vector3[] localCorners,
        Vector2[] leftPixels, Vector2[] rightPixels,
        ScaledIntrinsics leftIntr, ScaledIntrinsics rightIntr,
        Pose leftCam, Pose rightCam,
        Quaternion leftWorldToCam, Quaternion rightWorldToCam,
        float[] residuals)
    {
        float cost = 0f;
        for (int i = 0; i < 4; i++)
        {
            var worldP = t + R * localCorners[i];
            int o = i * 4;

            // Left eye.
            var leftLocal = leftWorldToCam * (worldP - leftCam.position);
            float leftY = -leftLocal.y;
            if (leftLocal.z > 1e-6f)
            {
                float u = leftIntr.fx * leftLocal.x / leftLocal.z + leftIntr.cx;
                float v = leftIntr.fy * leftY / leftLocal.z + leftIntr.cy;
                residuals[o + 0] = u - leftPixels[i].x;
                residuals[o + 1] = v - leftPixels[i].y;
            }
            else
            {
                residuals[o + 0] = 0f;
                residuals[o + 1] = 0f;
            }

            // Right eye.
            var rightLocal = rightWorldToCam * (worldP - rightCam.position);
            float rightY = -rightLocal.y;
            if (rightLocal.z > 1e-6f)
            {
                float u = rightIntr.fx * rightLocal.x / rightLocal.z + rightIntr.cx;
                float v = rightIntr.fy * rightY / rightLocal.z + rightIntr.cy;
                residuals[o + 2] = u - rightPixels[i].x;
                residuals[o + 3] = v - rightPixels[i].y;
            }
            else
            {
                residuals[o + 2] = 0f;
                residuals[o + 3] = 0f;
            }

            cost += residuals[o] * residuals[o]
                 + residuals[o + 1] * residuals[o + 1]
                 + residuals[o + 2] * residuals[o + 2]
                 + residuals[o + 3] * residuals[o + 3];
        }
        return cost;
    }

    /// <summary>
    /// Gauss-Jordan elimination with partial pivoting on a 6x6 system. Solves
    /// H δ = −g in place via the caller-supplied 6x7 augmented matrix scratch.
    /// Returns false if H is singular (pivot below 1e-12). Output split into 6
    /// scalars to avoid allocating a result array.
    /// </summary>
    private static bool SolveLinear6x6(
        float[,] H, float[] g, float[,] aug,
        out float x0, out float x1, out float x2, out float x3, out float x4, out float x5)
    {
        x0 = x1 = x2 = x3 = x4 = x5 = 0f;

        for (int i = 0; i < 6; i++)
        {
            for (int j = 0; j < 6; j++) aug[i, j] = H[i, j];
            aug[i, 6] = -g[i];
        }

        for (int col = 0; col < 6; col++)
        {
            int piv = col;
            float pivAbs = Mathf.Abs(aug[col, col]);
            for (int r = col + 1; r < 6; r++)
            {
                float a = Mathf.Abs(aug[r, col]);
                if (a > pivAbs) { pivAbs = a; piv = r; }
            }
            if (pivAbs < 1e-12f) return false;

            if (piv != col)
            {
                for (int j = 0; j < 7; j++)
                {
                    (aug[col, j], aug[piv, j]) = (aug[piv, j], aug[col, j]);
                }
            }

            float d = aug[col, col];
            for (int j = 0; j < 7; j++) aug[col, j] /= d;

            for (int r = 0; r < 6; r++)
            {
                if (r == col) continue;
                float f = aug[r, col];
                if (Mathf.Abs(f) < 1e-15f) continue;
                for (int j = 0; j < 7; j++) aug[r, j] -= f * aug[col, j];
            }
        }

        x0 = aug[0, 6];
        x1 = aug[1, 6];
        x2 = aug[2, 6];
        x3 = aug[3, 6];
        x4 = aug[4, 6];
        x5 = aug[5, 6];
        return true;
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
        // Poll until both cameras yield a frame this update. Times out to
        // prevent an infinite hang if a camera never comes online.
        var deadline = Time.realtimeSinceStartup + acquireTimeoutSeconds;
        while (Time.realtimeSinceStartup < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var l = TryGetFrame(leftCamera);
            var r = TryGetFrame(rightCamera);
            if (l.HasValue && r.HasValue) return (l.Value, r.Value);
            await Task.Delay(16, ct);
        }
        Debug.LogWarning($"[StereoAprilTagScanner] AcquirePairAsync timed out after {acquireTimeoutSeconds}s — " +
                         $"left playing: {(leftCamera ? leftCamera.IsPlaying : false)}, " +
                         $"right playing: {(rightCamera ? rightCamera.IsPlaying : false)}");
        return null;
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

        // Readback buffers track the same resolution. Safe to dispose here:
        // the _isScanning single-flight guard means no readback is pending
        // when EnsureResources runs (the previous scan awaited its readbacks
        // before returning).
        int pixelCount = width * height;
        if (!_leftReadback.IsCreated || _leftReadback.Length != pixelCount)
        {
            if (_leftReadback.IsCreated) _leftReadback.Dispose();
            if (_rightReadback.IsCreated) _rightReadback.Dispose();
            _leftReadback = new NativeArray<Color32>(pixelCount, Allocator.Persistent);
            _rightReadback = new NativeArray<Color32>(pixelCount, Allocator.Persistent);
            _leftPixelCache = new Color32[pixelCount];
            _rightPixelCache = new Color32[pixelCount];
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

    /// <summary>
    /// Reads the RT into the eye's persistent NativeArray, then memcpys it
    /// into the eye's reusable managed cache on the main thread and returns
    /// the cache. The managed hop (~0.5-1 ms) exists so the Task.Run detection
    /// worker never touches a NativeArray — development-build safety handles
    /// reject non-job-thread access — and it replaces the previous per-scan
    /// multi-MB ToArray() allocation. Callers must not start a second readback
    /// for the same eye before this one completes (the _isScanning
    /// single-flight guard enforces that).
    /// </summary>
    private Task<Color32[]> ReadPixelsIntoCacheAsync(RenderTexture rt, bool leftEye)
    {
        var tcs = new TaskCompletionSource<Color32[]>();
        Action<AsyncGPUReadbackRequest> onDone = request =>
        {
            if (request.hasError)
            {
                tcs.SetException(new Exception("[StereoAprilTagScanner] GPU readback error."));
                return;
            }
            var native = leftEye ? _leftReadback : _rightReadback;
            var cache = leftEye ? _leftPixelCache : _rightPixelCache;
            native.CopyTo(cache);
            tcs.SetResult(cache);
        };
        if (leftEye)
            AsyncGPUReadback.RequestIntoNativeArray(ref _leftReadback, rt, 0, TextureFormat.RGBA32, onDone);
        else
            AsyncGPUReadback.RequestIntoNativeArray(ref _rightReadback, rt, 0, TextureFormat.RGBA32, onDone);
        return tcs.Task;
    }
}
