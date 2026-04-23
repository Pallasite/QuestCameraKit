using System.Threading.Tasks;

/// <summary>
/// Abstracts the per-frame AprilTag detection so AprilTagDisplayManager can
/// drive either the monocular AprilTagScanner or the StereoAprilTagScanner
/// through the same call site.
/// </summary>
public interface IAprilTagScanner
{
    Task<AprilTagResult[]> ScanFrameAsync();
}
