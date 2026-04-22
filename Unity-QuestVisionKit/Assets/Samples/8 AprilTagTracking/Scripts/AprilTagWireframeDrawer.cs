using UnityEngine;

/// <summary>
/// Draws a wireframe cube + crosshair + forward axis on detected AprilTags
/// using immediate-mode Graphics.DrawMesh. Adapted from keijiro's TagDrawer
/// for use with world-space poses in the Quest passthrough pipeline.
///
/// The wireframe shows:
/// - A unit square on the tag face (the tag plane)
/// - A cube extruding forward from the tag (depth = tag size)
/// - A crosshair on the tag face center
/// - A forward axis line extending 1.5x the tag size outward
///
/// All geometry is procedural (no prefabs needed). Requires only a
/// material assignment — an unlit color material works well.
/// </summary>
public sealed class AprilTagWireframeDrawer : System.IDisposable
{
    Mesh _mesh;
    Material _material;

    public AprilTagWireframeDrawer(Material material)
    {
        _mesh = BuildWireframeMesh();
        _material = material;
    }

    public void Dispose()
    {
        if (_mesh != null)
        {
            Object.Destroy(_mesh);
            _mesh = null;
        }
        _material = null;
    }

    /// <summary>
    /// Draws the wireframe cube at the given world-space pose.
    /// Call this from LateUpdate for each detected tag.
    /// </summary>
    /// <param name="worldPosition">Tag center in world space.</param>
    /// <param name="worldRotation">Tag orientation in world space.</param>
    /// <param name="tagSize">Physical tag size in meters (used as uniform scale).</param>
    public void Draw(Vector3 worldPosition, Quaternion worldRotation, float tagSize)
    {
        if (_mesh == null || _material == null) return;

        var xform = Matrix4x4.TRS(worldPosition, worldRotation, Vector3.one * tagSize);
        Graphics.DrawMesh(_mesh, xform, _material, 0);
    }

    /// <summary>
    /// Builds the same wireframe mesh as keijiro's TagDrawer:
    /// - 8 vertices for a unit cube (face at z=0, back at z=-1)
    /// - 4 vertices for a crosshair on the tag face
    /// - 2 vertices for a forward axis line (z=0 to z=-1.5)
    /// All rendered as line segments.
    /// </summary>
    static Mesh BuildWireframeMesh()
    {
        var vertices = new Vector3[]
        {
            // Cube front face (tag plane at z=0)
            new(-0.5f, -0.5f, 0),  // 0: bottom-left
            new(+0.5f, -0.5f, 0),  // 1: bottom-right
            new(+0.5f, +0.5f, 0),  // 2: top-right
            new(-0.5f, +0.5f, 0),  // 3: top-left

            // Cube back face (extruded forward along -Z)
            new(-0.5f, -0.5f, -1), // 4
            new(+0.5f, -0.5f, -1), // 5
            new(+0.5f, +0.5f, -1), // 6
            new(-0.5f, +0.5f, -1), // 7

            // Crosshair on tag face
            new(-0.2f, 0, 0),      // 8: left
            new(+0.2f, 0, 0),      // 9: right
            new(0, -0.2f, 0),      // 10: bottom
            new(0, +0.2f, 0),      // 11: top

            // Forward axis
            new(0, 0, 0),          // 12: center
            new(0, 0, -1.5f)       // 13: forward tip
        };

        var indices = new int[]
        {
            // Front face edges
            0, 1, 1, 2, 2, 3, 3, 0,
            // Back face edges
            4, 5, 5, 6, 6, 7, 7, 4,
            // Connecting edges (front to back)
            0, 4, 1, 5, 2, 6, 3, 7,
            // Crosshair
            8, 9, 10, 11,
            // Forward axis
            12, 13
        };

        var mesh = new Mesh();
        mesh.vertices = vertices;
        mesh.SetIndices(indices, MeshTopology.Lines, 0);
        return mesh;
    }
}
