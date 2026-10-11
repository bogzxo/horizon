using System.Numerics;

namespace Horizon.Core.Components;

/// <summary>
/// Where something is in three dimensions, which way it is turned and how big it is, and the matrix that comes of
/// it. The 3D cousin of <see cref="TransformComponent2D"/>. The matrix is worked out when it is asked for and
/// something changed, so setting the three of them one after the other costs one multiply, not three.
/// <code>
/// mesh.Transform.Position = new Vector3(0, 1, -5);
/// mesh.Transform.Rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0, 0);
/// </code>
/// The matrix is the System.Numerics way round, scale then rotation then translation, multiplied as a row vector
/// times the matrix, which is what every shader of the engine expects (see camera.slang).
/// </summary>
public class TransformComponent3D : GameComponent
{
    private Vector3 position;
    private Quaternion rotation = Quaternion.Identity;
    private Vector3 scale = Vector3.One;
    private Matrix4x4 model = Matrix4x4.Identity;
    private volatile bool dirty = true;

    /// <summary>Goes up by one every time something changes, for whoever keeps a copy and wants to know if it is stale.</summary>
    public int Epoch { get; private set; }

    public Vector3 Position
    {
        get => position;
        set { position = value; Touch(); }
    }

    public Quaternion Rotation
    {
        get => rotation;
        set { rotation = value; Touch(); }
    }

    public Vector3 Scale
    {
        get => scale;
        set { scale = value; Touch(); }
    }

    /// <summary>The whole lot as one matrix, local to world.</summary>
    public Matrix4x4 Model
    {
        get
        {
            if (dirty)
            {
                model = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
                dirty = false;
            }

            return model;
        }
    }

    /// <summary>Which way the thing faces, its local -Z turned the way it is turned.</summary>
    public Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, rotation);

    public Vector3 Up => Vector3.Transform(Vector3.UnitY, rotation);

    public Vector3 Right => Vector3.Transform(Vector3.UnitX, rotation);

    /// <summary>Turns it to face a point, the way a camera looks at something.</summary>
    public void LookAt(Vector3 target, Vector3? up = null)
    {
        Vector3 forward = target - position;
        if (forward.LengthSquared() < 1e-12f) return;

        // A look at matrix is the inverse of the pose, so it is turned round to get the pose back
        Matrix4x4 look = Matrix4x4.CreateLookAt(position, target, up ?? Vector3.UnitY);
        if (Matrix4x4.Invert(look, out Matrix4x4 pose))
            Rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(pose));
    }

    private void Touch()
    {
        dirty = true;
        Epoch++;
    }
}
