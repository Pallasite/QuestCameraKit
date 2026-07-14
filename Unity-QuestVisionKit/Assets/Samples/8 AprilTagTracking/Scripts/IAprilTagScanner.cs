using System.Threading.Tasks;

/// <summary>
/// Abstracts the per-frame AprilTag detection so AprilTagDisplayManager can
/// drive either the monocular AprilTagScanner or the StereoAprilTagScanner
/// through the same call site.
/// </summary>
public interface IAprilTagScanner
{
    Task<AprilTagResult[]> ScanFrameAsync();

    /// <summary>Physical tag edge length (meters) the scanner is configured for.</summary>
    float TagSizeMeters { get; }

    /// <summary>
    /// Per-frame downsampling divisor (1 = full camera resolution). Lower is
    /// more pixels-on-tag and better pose quality at higher per-scan cost —
    /// the experiment's scan profiles trade this against scan rate per phase.
    /// </summary>
    int SampleFactor { get; set; }

    /// <summary>
    /// Per-frame tag-ID whitelist. Non-empty: decoded tags with other IDs are
    /// dropped before any per-tag pose work. Empty: keep every decoded tag.
    /// </summary>
    int[] TargetTagIds { get; set; }
}
