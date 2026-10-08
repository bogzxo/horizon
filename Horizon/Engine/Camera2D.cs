using System.Drawing;
using System.Numerics;

namespace Horizon.Engine;

public class Camera2D : Camera
{
    public float Zoom
    {
        get => _zoom;
        set
        {
            _zoom = value;
            UpdateLens();
        }
    }

    private float _zoom = 1.0f;
    private Vector2 Size { get; set; }

    /// <summary>
    /// How much of the world the camera sees before its zoom. Set to the size of the window it shows a unit of
    /// the world per pixel, which is what a camera that draws a UI wants to keep doing when the window is resized.
    /// </summary>
    public Vector2 ViewSize
    {
        get => Size;
        set
        {
            Size = value;
            UpdateLens();
        }
    }

    /// <summary>
    /// A camera that can be drawn through straight away, also one that is never added to anything (one a UI is laid
    /// out by): its matrices are there from the start and kept up with its lens.
    /// </summary>
    public Camera2D(in Vector2 size)
    {
        this.Size = size;
        Zoom = 1.0f;
    }

    /// <summary>
    /// Helper method to work the projection out from the size and the zoom, and the matrices that go with it, for a
    /// camera that isn't in the scene and so is never brought up to date by it.
    /// </summary>
    private void UpdateLens()
    {
        Projection = Matrix4x4.CreateOrthographic(Size.X * Zoom, Size.Y * Zoom, 0.1f, 1000.0f);
        UpdateMatrices();
    }

    /// <summary>
    /// What it sees from a place: as much of the world as the lens is wide and high (an orthographic projection is two
    /// over that), around where the camera is.
    /// </summary>
    protected override RectangleF BoundsAt(Vector3 position, in Matrix4x4 projection)
    {
        float width = projection.M11 != 0.0f ? 2.0f / projection.M11 : 0.0f;
        float height = projection.M22 != 0.0f ? 2.0f / projection.M22 : 0.0f;

        return new RectangleF(position.X - 0.5f * width, position.Y - 0.5f * height, width, height);
    }
}
