# Triangle Constellation Pairing Down - Scene Architecture

## Overview

This scene implements a **stereo AprilTag tracking and drift correction pipeline** for Meta Quest 3. It detects AprilTag markers using both passthrough cameras, triangulates their 3D positions, builds a constellation-based spatial anchor, and continuously corrects for VR tracking drift using RANSAC + Kabsch rigid-body alignment. An experimenter can fine-tune obstacle placement in real-time using Quest controllers.

---

## Scene Hierarchy

```
Triangle Constellation Pairing Down
├── Directional Light
├── [BuildingBlock] Camera Rig          ← OVRCameraRig + OVRManager + head emulator
│   └── TrackingSpace
│       ├── LeftEyeAnchor               (MainCamera)
│       ├── CenterEyeAnchor             (MainCamera)
│       ├── RightEyeAnchor
│       ├── TrackerAnchor
│       ├── LeftHandAnchor
│       │   ├── LeftControllerAnchor
│       │   ├── LeftControllerInHandAnchor
│       │   └── [BuildingBlock] Hand Tracking left
│       ├── RightHandAnchor
│       │   ├── RightControllerAnchor
│       │   ├── RightControllerInHandAnchor
│       │   └── [BuildingBlock] Hand Tracking right
│       ├── LeftHandAnchorDetached
│       └── RightHandAnchorDetached
├── [BuildingBlock] Passthrough          ← OVRPassthroughLayer
├── [BuildingBlock] Occlusion Dependencies  ← EnvironmentDepthManager
├── Camera Permissions                   ← CameraPermissionRequester
├── Controller Input Handling            ← QuestControllerInput, ObstacleFinesseController
└── April Tags Logic and Managers        ← Core detection + correction pipeline
```

---

## Component Map

### April Tags Logic and Managers (core pipeline)

| Component | Enabled | Role |
|-----------|---------|------|
| `AprilTagScanner` | **Off** | Monocular scanner (disabled; stereo scanner preferred) |
| `StereoAprilTagScanner` | On | Stereo triangulation across L/R passthrough cameras |
| `AprilTagDisplayManager` | On | Orchestrator: drives scanning, converts to world-space, pools markers |
| `AprilTagWireframeVisualizer` | On | Subscribes to `OnTagsDetected`, draws wireframe cubes |
| `MarkerPool` | On | Singleton object pool for marker GameObjects |
| `PassthroughCameraAccess` (x2) | On | Left + Right passthrough camera texture providers |
| `EnvironmentRaycastManager` | On | Optional scene mesh raycasting for placement refinement |
| `ConstellationDriftCorrector` | On | Multi-tag RANSAC+Kabsch drift correction |

### Controller Input Handling

| Component | Role |
|-----------|------|
| `QuestControllerInput` | Discretizes thumbstick into fire events with repeat/rearm |
| `ObstacleFinesseController` | Maps controller input to obstacle nudge/rotate/calibrate |

### Meta Building Blocks

| GameObject | Components | Purpose |
|------------|-----------|---------|
| `[BuildingBlock] Camera Rig` | `OVRCameraRig`, `OVRManager`, `OVRHeadsetEmulator` | Quest XR rig with hand tracking |
| `[BuildingBlock] Passthrough` | `OVRPassthroughLayer` | Video passthrough rendering |
| `[BuildingBlock] Occlusion Dependencies` | `EnvironmentDepthManager` | Depth-based real-world occlusion |

---

## Data Flow

```
                    ┌─────────────────────────────────────────────┐
                    │         PassthroughCameraAccess (L/R)       │
                    │   (GPU texture + lens pose + intrinsics)    │
                    └──────────────────┬──────────────────────────┘
                                       │
                                       ▼
                    ┌──────────────────────────────────────────────┐
                    │          StereoAprilTagScanner               │
                    │                                              │
                    │  1. GPU downsample (compute shader)          │
                    │  2. AsyncGPUReadback → Color32[]             │
                    │  3. RawTagDetector (keijiro AprilTag lib)    │
                    │     on each eye in parallel                  │
                    │  4. Match detections by tag ID               │
                    │  5. Triangulate 4 corners (ray intersection) │
                    │  6. Kabsch pose-from-corners                 │
                    │                                              │
                    │  Returns: AprilTagResult[]                   │
                    │    { tagId, worldPoseOverride, corners... }  │
                    └──────────────────┬──────────────────────────┘
                                       │
                           IAprilTagScanner.ScanFrameAsync()
                                       │
                                       ▼
                    ┌──────────────────────────────────────────────┐
                    │          AprilTagDisplayManager              │
                    │                                              │
                    │  • Calls scanner each Update()               │
                    │  • Converts camera-space → world-space       │
                    │  • Optional EnvironmentRaycast refinement    │
                    │  • Pools markers via MarkerPool              │
                    │  • Fires OnTagsDetected event                │
                    └────────┬────────────────────┬────────────────┘
                             │                    │
                    OnTagsDetected         OnTagsDetected
                             │                    │
                             ▼                    ▼
              ┌──────────────────────┐  ┌────────────────────────────┐
              │ AprilTagWireframe-   │  │ ConstellationDriftCorrector │
              │ Visualizer           │  │                             │
              │                      │  │ 1. Project detections into  │
              │ Draws wireframe      │  │    anchor-local space       │
              │ cubes at tag poses   │  │ 2. RANSAC + Kabsch solve    │
              │ (keijiro-style)      │  │ 3. Rotation gate filter     │
              │                      │  │ 4. Consistency window       │
              │                      │  │ 5. Lerp onto CorrectionRoot│
              └──────────────────────┘  └─────────────┬──────────────┘
                                                      │
                                         CorrectionRoot (child of
                                          OVRSpatialAnchor)
                                                      │
                                                      ▼
                                              ┌───────────────┐
                                              │   Obstacle     │
                                              │  (prefab)      │
                                              └───────┬───────┘
                                                      │
                                                      │ localPosition/Rotation
                                                      │ written by:
                                                      ▼
                                        ┌──────────────────────────┐
                                        │ ObstacleFinesseController │
                                        │                           │
                                        │ Quest controller input:   │
                                        │  L stick → XZ translate   │
                                        │  R stick Y → Y translate  │
                                        │  R stick X → yaw rotate   │
                                        │  L grip → fine mode       │
                                        │  R grip + A → calibrate   │
                                        └──────────────────────────┘
```

---

## Key Algorithms

### Stereo Triangulation (`StereoAprilTagScanner`)

1. **GPU Downsample** — compute shader reduces both camera textures by `sampleFactor` (default 2x)
2. **Async Readback** — `AsyncGPUReadback.Request` avoids stalling the render thread
3. **Corner Detection** — `RawTagDetector` wraps keijiro's AprilTag interop (no pose estimation, corners only)
4. **ID Matching** — only tags seen in both eyes proceed to triangulation
5. **Ray Triangulation** — per-corner closest-point solve on skew rays from L/R lens origins
6. **Pose from Corners** — Kabsch (planar specialization) or naive cross-product, toggleable in Inspector

**Calibration mode** (`ScanCalibrationAsync`): captures N frame pairs at full resolution, takes component-wise median of corner positions across frames, then computes a single high-quality pose per tag. Used for initial anchor seeding.

### Constellation Drift Correction (`ConstellationDriftCorrector`)

1. **Calibrate** — captures reference constellation via stereo calibration scan, creates `OVRSpatialAnchor` at centroid, stores each tag's anchor-local pose
2. **Per-frame** — projects new detections into anchor-local space, runs RANSAC (32 iterations, 5mm inlier threshold) + Horn 1987 Kabsch (power iteration on 4x4 symmetric matrix) to find the optimal rigid correction
3. **Rotation gate** — rejects tags whose rotation disagrees with the position-based consensus (catches misdetections)
4. **Consistency window** — requires N consecutive agreeing candidates (3mm translation, 1deg rotation)
5. **Trigger + Apply** — incremental magnitude check against currently-applied correction, then SmoothStep lerp over 1s onto `CorrectionRoot.localPose`

### Controller Input (`QuestControllerInput` + `ObstacleFinesseController`)

- Thumbstick discretization with fire/rearm thresholds and auto-repeat at 5Hz
- Two precision modes: coarse (1cm / 1deg) and fine (1mm / 0.1deg) via left grip hold
- Writes to obstacle's `localPosition`/`localRotation` (preserved across recalibrations)
- Calibrate chord: right grip + A button

---

## Transform Hierarchy (Runtime)

```
World
├── ConstellationAnchor (OVRSpatialAnchor at constellation centroid)
│   └── CorrectionRoot (localPose = drift correction from Kabsch)
│       └── Obstacle Prefab (localPose = experimenter's finesse offset)
```

The spatial anchor drifts with the Quest's SLAM map. The `CorrectionRoot` compensates by comparing live AprilTag observations against the calibrated reference. The obstacle's local offset is the experimenter's hand-tuned placement, which survives recalibrations.

---

## Script Locations

```
Assets/
├── _Scripts/
│   └── CameraPermissionRequester.cs
└── Samples/
    ├── Common/Scripts/
    │   ├── MarkerPool.cs               (singleton object pool)
    │   └── MarkerController.cs         (TMP label + auto-hide)
    ├── 8 AprilTagTracking/Scripts/
    │   ├── IAprilTagScanner.cs          (interface: ScanFrameAsync)
    │   ├── RawTagDetector.cs            (keijiro interop wrapper)
    │   ├── AprilTagScanner.cs           (monocular, disabled in scene)
    │   ├── StereoAprilTagScanner.cs     (stereo triangulation)
    │   ├── AprilTagDisplayManager.cs    (orchestrator + world-space conversion)
    │   ├── AprilTagWireframeVisualizer.cs (wireframe cube renderer)
    │   └── AprilTagWireframeDrawer.cs   (procedural mesh builder)
    └── 9 AprilTagSpatialAnchor/Scripts/
        ├── ConstellationDriftCorrector.cs (RANSAC+Kabsch drift correction)
        ├── QuestControllerInput.cs       (stick discretization layer)
        ├── ObstacleFinesseController.cs  (controller → obstacle transform)
        ├── AprilTagAnchorManager.cs      (per-tag OVRSpatialAnchor diagnostics)
        ├── AprilTagPoseStabilityGate.cs  (sliding window pose validation)
        └── AnchoredContentController.cs  (label + diagnostics for anchored content)
```

---

## Dependencies

| Dependency | Purpose |
|------------|---------|
| **Meta XR SDK** (OVR*) | Camera rig, passthrough, spatial anchors, hand tracking, controllers |
| **Meta XR PassthroughCameraAccess** | Raw passthrough camera textures + intrinsics |
| **Meta XR EnvironmentRaycastManager** | Scene mesh raycasting for placement refinement |
| **Meta XR EnvironmentDepthManager** | Real-world occlusion |
| **keijiro AprilTag** | AprilTag corner detection (native interop) |
| **TextMeshPro** | Marker labels |

---

## Configuration Notes

- **`AprilTagScanner` is disabled** — the stereo scanner is preferred to avoid monocular depth bias
- **Two `PassthroughCameraAccess` components** are required on the pipeline GameObject (one Left, one Right)
- **`ConstellationDriftCorrector.allowedTagIds`** — restrict which tags participate in the constellation (empty = all)
- **`minTagsForCalibration`** — defaults to 3; the scene won't calibrate with fewer visible tags
- **`autoCalibrate`** — off by default; enable for headless testing without the UI panel
- **RANSAC inlier threshold** — 5mm; controls sensitivity to noisy detections
- **Consistency window** — 5 frames at 3mm/1deg agreement before a correction triggers
- **Drift trigger** — 1cm minimum incremental correction to avoid micro-adjustments
- **Lerp duration** — 1 second SmoothStep with 5 second cooldown between corrections
