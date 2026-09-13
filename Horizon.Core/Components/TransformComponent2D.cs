using System.Numerics;
using Horizon.Rendering;

namespace Horizon.Core.Components;

/// <summary>
/// Represents a component that handles the 2D transformation of a game entity.
/// </summary>
public class TransformComponent2D : IGameComponent
{
    public string Name { get; set; } = "Transform2D";
    public bool Enabled { get; set; }

    /// <summary>
    /// The position of the game entity in 3D space.
    /// </summary>
    private Vector2 pos;

    /// <summary>
    /// The rotation angles of the game entity in degrees around each axis (X, Y, and Z).
    /// </summary>
    private float rot;

    /// <summary>
    /// The size factors of the game entity along each axis (X and Y).
    /// </summary>
    private Vector2 size = Vector2.One;
    /// <summary>
    /// You may override this as a means to fight Z axis clipping.
    /// </summary>
    public float ZOffset = 0.0f;

    /// <summary>
    /// Sets the origin around which the position is considered.
    /// </summary>
    public Origin Origin { get; set; } = Origin.Center;

    
    private Vector2 GetOriginOffset()
    {
        // Assuming your base generic box vertices go from -0.5 to +0.5
        return Origin switch
        {
            Origin.Center => new Vector2(0f, 0f),

            // Stretching from the right means the right edge stays pinned at x=0
            Origin.Right => new Vector2(-0.5f, 0f),
            Origin.Left => new Vector2(0.5f, 0f),

            Origin.Top => new Vector2(0f, -0.5f),  // Note: Y-sign depends on whether your engine is Y-up or Y-down
            Origin.Bottom => new Vector2(0f, 0.5f),

            Origin.TopLeft => new Vector2(0.5f, -0.5f),
            Origin.TopRight => new Vector2(-0.5f, -0.5f),
            Origin.BottomLeft => new Vector2(0.5f, 0.5f),
            Origin.BottomRight => new Vector2(-0.5f, 0.5f),

            _ => Vector2.Zero
        };
    }

    /// <summary>
    /// Updates the model matrix based on the current position, rotation, size, and origin.
    /// </summary>
    private void updateModelMatrix()
    {
        // 1. Get the local space offset based on the selected origin
        Vector2 originOffset = GetOriginOffset();

        // 2. Convert rotation angles to radians
        float radiansZ = MathHelper.DegreesToRadians(rot);
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, radiansZ);

        // 3. Create the model matrix
        // Order matters: Origin Offset -> Scale -> Rotation -> World Translation
        ModelMatrix =
            Matrix4x4.CreateTranslation(originOffset.X, originOffset.Y, 0f)
            * Matrix4x4.CreateScale(size.X, size.Y, 1.0f)
            * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(pos.X, pos.Y, ZOffset);
    }

    /// <summary>
    /// The model matrix representing the transformation of the game entity.
    /// </summary>
    public Matrix4x4 ModelMatrix { get; private set; }

    /// <summary>
    /// Gets or sets the position of the game entity in 3D space.
    /// </summary>
    public Vector2 Position
    {
        get => pos;
        set
        {
            pos = value;
            updateModelMatrix();
        }
    }

    /// <summary>
    /// Gets or sets the rotation angles of the game entity in degrees around each axis (X, Y, and Z).
    /// </summary>
    public float Rotation
    {
        get => rot;
        set
        {
            rot = value;
            updateModelMatrix();
        }
    }
    /// <summary>
    /// Sets the transform position relative to the center of the object.
    /// </summary>
    /// <param name="position"></param>
    public void SetPositionRelativeToOrigin(Vector2 position)
    {
        Position = position + size / new Vector2(2, -2);
    }

    /// <summary>
    /// Gets or sets the size in pixels.
    /// </summary>
    public Vector2 Size
    {
        get => size;
        set
        {
            size = value;
            updateModelMatrix();
        }
    }

    /// <summary>
    /// The parent entity to which this transform component belongs.
    /// </summary>
    public Entity Parent { get; set; }

    /// <summary>
    /// Initializes the transform component.
    /// </summary>
    public void Initialize()
    {
        updateModelMatrix();
    }

    /// <summary>
    /// Updates the transform component based on the elapsed time (dt).
    /// </summary>
    /// <param name="dt">The elapsed time since the last update call.</param>
    public void UpdateState(float dt)
    { }

    public void UpdatePhysics(float dt)
    { }

    /// <summary>
    /// Draws the game entity with the current transformation.
    /// </summary>
    /// <param name="dt">The elapsed time since the last draw call.</param>
    /// <param name="options">Optional render options.</param>
    public void Render(float dt, object? obj = null)
    { }
}