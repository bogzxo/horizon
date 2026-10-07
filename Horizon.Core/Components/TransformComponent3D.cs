using System.Numerics;

namespace Horizon.Core.Components;

/// <summary>
/// Represents a component that handles the 3D transformation of a game entity.
/// </summary>
public class TransformComponent3D : GameComponent
{
    /// <summary>
    /// The position of the game entity in 3D space.
    /// </summary>
    private Vector3 pos;

    /// <summary>
    /// The rotation angles of the game entity in degrees around each axis (X, Y, and Z).
    /// </summary>
    private Vector3 rot;

    /// <summary>
    /// The size factors of the game entity along each axis (X and Y).
    /// </summary>
    private Vector3 size = Vector3.One;

    /// <summary>
    /// Updates the model matrix based on the current position, rotation, and size values.
    /// </summary>
    private void updateModelMatrix()
    {
        // Create quaternions for each rotation axis
        Quaternion rotation = Quaternion.CreateFromYawPitchRoll(float.DegreesToRadians(rot.X), float.DegreesToRadians(rot.Y), float.DegreesToRadians(rot.Z));

        // Create the model matrix
        ModelMatrix =
            Matrix4x4.CreateScale(size.X, size.Y, size.Z)
            * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(pos.X, pos.Y, 0.0f);
    }

    /// <summary>
    /// The model matrix representing the transformation of the game entity.
    /// </summary>
    public Matrix4x4 ModelMatrix { get; private set; }

    /// <summary>
    /// Gets or sets the position of the game entity in 3D space.
    /// </summary>
    public Vector3 Position
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
    public Vector3 Rotation
    {
        get => rot;
        set
        {
            rot = value;
            updateModelMatrix();
        }
    }

    /// <summary>
    /// Gets or sets the size in pixels.
    /// </summary>
    public Vector3 Size
    {
        get => size;
        set
        {
            size = value;
            updateModelMatrix();
        }
    }

    /// <summary>
    /// Initializes the transform component.
    /// </summary>
    public override void Initialize()
    {
        updateModelMatrix();
    }
}