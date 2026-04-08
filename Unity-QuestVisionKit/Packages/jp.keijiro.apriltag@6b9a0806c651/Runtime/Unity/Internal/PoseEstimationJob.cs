using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace AprilTag {

//
// Job struct that wraps AprilTag pose estimator
//
struct PoseEstimationJob : Unity.Jobs.IJobParallelFor
{
    // Input data struct that simply wraps pointers to tag detection data
    public struct Input
    {
        unsafe Interop.Detection* p;

        unsafe public Input(ref Interop.Detection r)
          => p = (Interop.Detection*)Interop.Util.AsPointer(ref r);

        unsafe public ref Interop.Detection Ref
          => ref Interop.Util.AsRef<Interop.Detection>(p);
    }

    // I/O
    [ReadOnly] NativeArray<Input> _input;
    [WriteOnly] NativeArray<TagPose> _output;

    // Camera parameters
    double _tagSize;
    double _focalLength;
    double _fy;
    byte _useIntrinsics; // byte instead of bool for blittability in Unity Jobs
    double2 _focalCenter;

    // Constructor (FOV-based, legacy)
    public PoseEstimationJob
      (NativeArray<Input> input, NativeArray<TagPose> output,
       int width, int height, float fov, float tagSize)
    {
        _input = input;
        _output = output;
        _tagSize = tagSize;
        _focalLength = height / 2 / math.tan(fov / 2);
        _focalCenter = math.double2(width, height) / 2;
        _fy = 0;
        _useIntrinsics = 0;
    }

    // Constructor (intrinsics-based, for accurate passthrough camera pose)
    public PoseEstimationJob
      (NativeArray<Input> input, NativeArray<TagPose> output,
       double fx, double fy, double cx, double cy, float tagSize)
    {
        _input = input;
        _output = output;
        _tagSize = tagSize;
        _focalLength = fx; // stored for the DetectionInfo fx parameter
        _focalCenter = math.double2(cx, cy);
        // Note: fy is passed separately to DetectionInfo below
        _fy = fy;
        _useIntrinsics = 1;
    }

    // Job execution method
    public void Execute(int i)
    {
        var fy = _useIntrinsics == 1 ? _fy : _focalLength;
        var info = new Interop.DetectionInfo(ref _input[i].Ref, _tagSize,
           _focalLength, fy, _focalCenter.x, _focalCenter.y);

        using var pose = new Interop.Pose(ref info);

        var pos = pose.t.AsFloat3() * math.float3(1, -1, 1);

        var rot = math.quaternion(pose.R.AsFloat3x3());
        rot = rot.value * math.float4(-1, 1, -1, 1);

        _output[i] = new TagPose(_input[i].Ref.ID, pos, rot);
    }
}

} // namespace AprilTag
