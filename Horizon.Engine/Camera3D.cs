using System.Numerics;

using Horizon.Core;

namespace Horizon.Engine;

public class Camera3D : Camera
{
    private float lookSensitivity = 0.1f;
    public float CameraYaw = 0f;
    public float CameraPitch = 0f;

    public Vector3 Front { get; private set; }

    public Camera3D()
        : this(90.0f) { }


    public Camera3D(in float w, in float h)
    {
        Projection = Matrix4x4.CreateOrthographic(w, h, Near = 0.01f, Far = 100.0f);
    }
    public Camera3D(in float fov = 45.0f)
    {
        Projection = Matrix4x4.CreatePerspectiveFieldOfView(
            float.DegreesToRadians(fov),
            GameEngine.Instance.WindowManager.AspectRatio,
            Near = 0.1f,
            Far = 4000.0f
        );
    }

    public void LookAt(in Vector3 target)
    {
        View = Matrix4x4.CreateLookAt(Position, target, CameraUp);
        ViewProj = View * Projection;
    }

    protected override void UpdateMatrices()
    {
        View = Matrix4x4.CreateLookAt(Position, Position + Front, CameraUp);
        ViewProj = View * Projection;
    }

    private void UpdateMouse()
    {
        if (Enabled)
        {
            // Up on the screen is up in the world, which the mouse counts the other way round
            Vector2 moved = Engine.Input.Mouse.Delta * lookSensitivity;

            CameraYaw += moved.X;
            CameraPitch -= moved.Y;

            // We don't want to be able to look behind us by going over our head or under our feet so make sure it stays within these bounds
            CameraPitch = Math.Clamp(CameraPitch, -89.0f, 89.0f);
        }

        Direction = new Vector3(
            MathF.Cos(float.DegreesToRadians(CameraYaw))
                * MathF.Cos(float.DegreesToRadians(CameraPitch)),
            MathF.Sin(float.DegreesToRadians(CameraPitch)),
            MathF.Sin(float.DegreesToRadians(CameraYaw))
                * MathF.Cos(float.DegreesToRadians(CameraPitch))
        );

        Front = Vector3.Normalize(Direction);
    }

    public override void UpdateState(float dt)
    {
        UpdateMouse();
        base.UpdateState(dt);
    }

    public override void Render(float dt)
    {
        base.Render(dt);
    }
}