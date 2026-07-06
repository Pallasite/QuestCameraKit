using UnityEngine;

/// <summary>
/// Secondary visualizer that draws keijiro-style wireframe cubes on
/// detected AprilTags. Subscribes to AprilTagDisplayManager.OnTagsDetected
/// so it reuses the same scan results — no duplicate camera processing.
///
/// Setup:
/// 1. Add this component to the same GameObject as AprilTagDisplayManager.
/// 2. Assign a wireframe material (an unlit single-color material works well;
///    "Unlit/Color" with a bright green or cyan is a good default).
/// 3. The wireframe cube, crosshair, and forward axis will draw on every
///    detected tag at its world-space pose, scaled to the physical tag size.
///
/// The wireframe shows:
/// - A square on the tag face (the detected tag plane)
/// - A cube extruding forward from the tag surface
/// - A crosshair at the tag center
/// - A forward axis line extending beyond the cube
/// </summary>
public class AprilTagWireframeVisualizer : MonoBehaviour
{
    public enum DisplayMode
    {
        /// <summary>Legacy / debug — draw wireframes on every detection batch.</summary>
        Always,
        /// <summary>Draw when uncalibrated OR during a streaming sweep; hide in steady-state.</summary>
        DuringCalibrationOnly,
        /// <summary>Draw only during an active streaming sweep.</summary>
        DuringStreamingOnly,
        /// <summary>Never draw — useful to disable visuals without removing the component.</summary>
        Never,
        /// <summary>
        /// Gate on the single/double-tag placement flow: draw while the obstacle is
        /// NOT yet placed (Setup), hide once placed so wireframe cubes don't
        /// distract the participant during walks. Appended member — do not reorder
        /// (serialized by index in scenes).
        /// </summary>
        DuringPlacementSetup,
    }

    [Tooltip("Material for the wireframe lines. Use an unlit color material " +
             "(e.g. Unlit/Color set to bright green) for best visibility.")]
    [SerializeField] private Material wireframeMaterial;

    [Tooltip("Known physical tag edge length in meters. This is the 'prior' — what you measured with " +
             "calipers and printed on. Used as the authoritative size for wireframe scaling instead of " +
             "the per-frame triangulated size (which jitters with stereo noise frame-to-frame). When a " +
             "sizeSource scanner is assigned/found, ITS tagSizeMeters wins so you only have to keep one " +
             "Inspector field in sync; this local field is the fallback.")]
    [SerializeField] private float tagSizeMeters = 0.171f;

    [Tooltip("Optional StereoAprilTagScanner used as the authoritative source for tagSizeMeters. " +
             "Auto-resolved from the same GameObject if left null. Keeps the visualizer in sync with " +
             "whatever size the scanner is configured for (one Inspector field, one source of truth).")]
    [SerializeField] private StereoAprilTagScanner sizeSource;

    [Header("Visibility gating")]
    [Tooltip("Optional ConstellationDriftCorrector reference. Auto-resolved from the same GameObject " +
             "if left null. Required for any mode other than 'Always' to meaningfully gate visibility.")]
    [SerializeField] private ConstellationDriftCorrector corrector;

    [Tooltip("When to draw wireframes. Default 'DuringCalibrationOnly' shows them while uncalibrated " +
             "and during streaming sweeps, then hides them in steady-state when the obstacle is anchored.")]
    [SerializeField] private DisplayMode displayMode = DisplayMode.DuringCalibrationOnly;

    [Tooltip("Used by the 'DuringPlacementSetup' mode: draw while this placement controller has no " +
             "placed obstacle, hide once placed. Auto-resolved (scene-wide) if left null.")]
    [SerializeField] private ObstaclePlacementController placement;

    /// <summary>
    /// Diagnostic override (not serialized): when true, wireframes draw regardless
    /// of <see cref="displayMode"/>. Driven by the SessionHUD diagnostics toggle.
    /// </summary>
    public bool ForceVisible { get; set; }

    [Header("Quality color")]
    [Tooltip("Vary wireframe color per tag based on detection quality (observation count during " +
             "streaming, corner residual otherwise). Set false to always render the base material color.")]
    [SerializeField] private bool colorByQuality = true;

    [Tooltip("Color mapping for streaming sweeps. Evaluated at t = observationCount / 10 (clamped). " +
             "Default ramp: red (0 obs) -> yellow (~5 obs) -> green (10+ obs, ready to commit).")]
    [SerializeField] private Gradient streamingQualityGradient = DefaultStreamingGradient();

    [Tooltip("Observation count that maps to t=1 in the streaming gradient. Match this to the " +
             "corrector's streamingMinObservationsPerTag (default 10) so green means 'commit-ready'.")]
    [SerializeField] private int streamingQualityFullObservationCount = 10;

    [Tooltip("Color mapping for non-streaming detections (pre-cal or, if visible, steady-state). " +
             "Evaluated at t = residual / residualWarnDoubled (clamped). Default ramp: " +
             "green (0 mm residual) -> yellow -> red (3 mm+, exceeds the corrector's warn threshold).")]
    [SerializeField] private Gradient residualQualityGradient = DefaultResidualGradient();

    [Tooltip("Residual (meters) that maps to t=1 in the residual gradient. Default 3 mm matches " +
             "the corrector's residualRmsWarnMeters default.")]
    [SerializeField] private float residualQualityFullScaleMeters = 0.003f;

    private AprilTagDisplayManager _displayManager;
    private AprilTagWireframeDrawer _drawer;
    private MaterialPropertyBlock _propertyBlock;

    // Store the latest poses so we can draw them in LateUpdate
    // (Graphics.DrawMesh should be called from LateUpdate for correct rendering order)
    private AprilTagDisplayManager.TagWorldPose[] _latestPoses;
    private int _lastUpdateFrame = -1;

    private void Awake()
    {
        // Auto-resolve sibling components from the same GameObject when not
        // wired explicitly. Matches the AprilTag stack's typical layout so the
        // visibility modes + size source work zero-touch.
        if (!corrector) corrector = GetComponent<ConstellationDriftCorrector>();
        if (!sizeSource) sizeSource = GetComponent<StereoAprilTagScanner>();
        if (!placement) placement = FindAnyObjectByType<ObstaclePlacementController>();
    }

    private void OnEnable()
    {
        _displayManager = GetComponent<AprilTagDisplayManager>();
        if (!_displayManager)
        {
            Debug.LogError("[AprilTagWireframeVisualizer] No AprilTagDisplayManager found " +
                           "on this GameObject. Add one or place this component alongside it.");
            enabled = false;
            return;
        }

        if (!wireframeMaterial)
        {
            Debug.LogError("[AprilTagWireframeVisualizer] No wireframe material assigned.");
            enabled = false;
            return;
        }

        _drawer = new AprilTagWireframeDrawer(wireframeMaterial);
        _propertyBlock = new MaterialPropertyBlock();
        _displayManager.OnTagsDetected += HandleTagsDetected;
    }

    private void OnDisable()
    {
        if (_displayManager)
        {
            _displayManager.OnTagsDetected -= HandleTagsDetected;
        }

        _drawer?.Dispose();
        _drawer = null;
        _propertyBlock = null;
        _latestPoses = null;
    }

    private void HandleTagsDetected(AprilTagDisplayManager.TagWorldPose[] poses)
    {
        _latestPoses = poses;
        _lastUpdateFrame = Time.frameCount;
    }

    private void LateUpdate()
    {
        if (_drawer == null || _latestPoses == null) return;

        // Only draw if we received poses this frame (avoids stale draws)
        if (Time.frameCount != _lastUpdateFrame) return;

        if (!ShouldDrawThisFrame()) return;

        // Single tag-size source for the whole frame. We deliberately ignore
        // pose.SizeMeters (the per-frame triangulated estimate) — the Inspector
        // value is the known physical prior and using the noisy estimate makes
        // the wireframe wiggle in size frame-to-frame for no good reason.
        var size = sizeSource != null ? sizeSource.TagSizeMeters : tagSizeMeters;
        if (size <= 0f) size = tagSizeMeters; // last-ditch fallback if the scanner field is zero/unset

        foreach (var pose in _latestPoses)
        {
            if (colorByQuality && _propertyBlock != null)
            {
                var color = ComputeColor(pose);
                _drawer.Draw(pose.Position, pose.Rotation, size, color, _propertyBlock);
            }
            else
            {
                _drawer.Draw(pose.Position, pose.Rotation, size);
            }
        }
    }

    private bool ShouldDrawThisFrame()
    {
        if (ForceVisible) return true;   // diagnostics override

        switch (displayMode)
        {
            case DisplayMode.Never:
                return false;
            case DisplayMode.DuringPlacementSetup:
                // No placement controller found -> fall back to always-on so the
                // wireframe never silently disappears in a misconfigured scene.
                if (placement == null) return true;
                return !placement.IsPlaced;
            case DisplayMode.Always:
                return true;
            case DisplayMode.DuringStreamingOnly:
                return corrector != null && corrector.IsStreamingCalibration;
            case DisplayMode.DuringCalibrationOnly:
                // No corrector → fall through to legacy always-on so we don't
                // silently hide everything if the field is unwired.
                if (corrector == null) return true;
                if (corrector.IsStreamingCalibration) return true;
                return !corrector.IsCalibrated;
            default:
                return true;
        }
    }

    private Color ComputeColor(AprilTagDisplayManager.TagWorldPose pose)
    {
        if (corrector != null && corrector.IsStreamingCalibration)
        {
            float denom = Mathf.Max(1, streamingQualityFullObservationCount);
            float t = Mathf.Clamp01(corrector.GetStreamingObservationCount(pose.TagId) / denom);
            return streamingQualityGradient.Evaluate(t);
        }
        float denomRes = Mathf.Max(1e-6f, residualQualityFullScaleMeters);
        float tRes = Mathf.Clamp01(pose.CornerResidualMeters / denomRes);
        return residualQualityGradient.Evaluate(tRes);
    }

    // Palette ramp for "few -> many observations" (bad -> good). See
    // ExperimentPalette — the red<->green defaults collided in opposite
    // directions across the two gradients and were not colorblind-safe.
    // NOTE: serialized scene values override these defaults; existing scenes
    // are unaffected until their gradients are edited.
    private static Gradient DefaultStreamingGradient() => ExperimentPalette.BadToGood();

    // Palette ramp for "small residual -> large residual" (good -> bad).
    private static Gradient DefaultResidualGradient() => ExperimentPalette.GoodToBad();
}
