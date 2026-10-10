using System.Numerics;

namespace Horizon.Engine;

/// <summary>
/// A camera with a lens, for a world with a third dimension. Put it somewhere, point it at something, give it a
/// field of view, and everything with a <see cref="Core.Components.TransformComponent3D"/> is drawn through it the
/// way a <see cref="Camera2D"/> draws sprites. Orthographic if asked (<see cref="OrthographicSize"/>), for the
/// isometric lot and for anything that wants to measure what it draws.
/// <code>
/// ActiveCamera = AddEntity(new Camera3D(window.X / window.Y) { Position = new Vector3(0, 2, 6) });
/// camera.LookAt(Vector3.Zero);
/// </code>
/// It is shown between two ticks like every camera, see <see cref="Camera"/>, so it glides at any frame rate. What
/// it has no idea about is <see cref="Camera.Bounds"/>, a lens looking into a world has no rectangle of it, and
/// <see cref="Camera.ScreenToWorld"/>, which goes through the plane at the near distance and is not what anybody
/// pointing at a thing in the distance wants. A ray through the pixel is what that wants, see <see cref="Ray"/>.
/// </summary>
public class Camera3D : Camera
{
    private float fieldOfView = MathF.PI / 3.0f, aspect = 16.0f / 9.0f, orthographicSize;
    private Vector3 look = -Vector3.UnitZ;

    /// <summary>The vertical angle the lens takes in, in radians. Sixty degrees to begin with.</summary>
    public float FieldOfView
    {
        get => fieldOfView;
        set { fieldOfView = Math.Clamp(value, 0.01f, MathF.PI - 0.01f); UpdateLens(); }
    }

    /// <summary>Width over height of the picture, so a cube is a cube.</summary>
    public float AspectRatio
    {
        get => aspect;
        set { aspect = MathF.Max(value, 0.01f); UpdateLens(); }
    }

    /// <summary>
    /// How tall (in world units) the picture is for a camera with no perspective, 0 for a lens with perspective,
    /// which is what it is unless somebody says otherwise.
    /// </summary>
    public float OrthographicSize
    {
        get => orthographicSize;
        set { orthographicSize = MathF.Max(value, 0.0f); UpdateLens(); }
    }

    /// <summary>Where the picture starts, nothing nearer than this is drawn. Keep it as far as you can bear, depth precision is spent near here.</summary>
    public float NearPlane
    {
        get => Near;
        set { Near = MathF.Max(value, 0.001f); UpdateLens(); }
    }

    public float FarPlane
    {
        get => Far;
        set { Far = MathF.Max(value, Near + 0.001f); UpdateLens(); }
    }

    /// <summary>Which way the camera looks, a unit vector. Set it, or <see cref="LookAt"/> something.</summary>
    public Vector3 Look
    {
        get => look;
        set
        {
            if (value.LengthSquared() > 1e-12f) look = Vector3.Normalize(value);
        }
    }

    /// <summary>Which way is up for the camera, Y unless it is rolled.</summary>
    public Vector3 Up
    {
        get => CameraUp;
        set
        {
            if (value.LengthSquared() > 1e-12f) CameraUp = Vector3.Normalize(value);
        }
    }

    protected override Vector3 LookDirection => look;

    public Camera3D(float aspectRatio = 16.0f / 9.0f)
    {
        aspect = MathF.Max(aspectRatio, 0.01f);
        Near = 0.1f;
        Far = 500.0f;
        UpdateLens();
    }

    /// <summary>Points the camera at a point of the world, from where it is.</summary>
    public void LookAt(Vector3 target) => Look = target - Position;

    /// <summary>
    /// The ray that goes out through a pixel of the window, where it starts and which way it goes, for finding out
    /// what the pointer is over. Pixels from the top left corner, the way the mouse says where it is.
    /// </summary>
    public (Vector3 Origin, Vector3 Direction) Ray(Vector2 screenPosition)
    {
        Vector2 window = Engine.WindowManager.WindowSize;
        float x = screenPosition.X / window.X * 2.0f - 1.0f;
        float y = 1.0f - screenPosition.Y / window.Y * 2.0f;

        if (!Matrix4x4.Invert(ViewProj, out Matrix4x4 back))
            return (Position, look);

        // Two points down the pixel, one at the near plane and one at the far, and the ray is through both
        Vector4 near = Vector4.Transform(new Vector4(x, y, 0.0f, 1.0f), back);
        Vector4 far = Vector4.Transform(new Vector4(x, y, 1.0f, 1.0f), back);
        Vector3 from = new Vector3(near.X, near.Y, near.Z) / near.W;
        Vector3 to = new Vector3(far.X, far.Y, far.Z) / far.W;
        Vector3 direction = to - from;

        return (from, direction.LengthSquared() > 1e-12f ? Vector3.Normalize(direction) : look);
    }

    private void UpdateLens()
    {
        // System.Numerics puts the depth from 0 at the near plane to 1 at the far one, and the engine's clip
        // correction (made for the other convention) folds that into the top half. That is one bit of depth gone
        // out of twenty four, which nobody will miss, and the alternative is two conventions in one engine
        Projection = orthographicSize > 0.0f
            ? Matrix4x4.CreateOrthographic(orthographicSize * aspect, orthographicSize, Near, Far)
            : Matrix4x4.CreatePerspectiveFieldOfView(fieldOfView, aspect, Near, Far);

        UpdateMatrices();
    }
}
