using System.Numerics;

using CodeKneading.Player;

using Horizon.Core;
using Horizon.Core.Components;

namespace CodeKneading.Voxel;

internal class SkyManager : IGameComponent
{
    public bool Enabled { get; set; }
    public string Name { get; set; }
    public Entity Parent { get; set; }

    public static readonly Matrix4x4 SunProjFar;
    public static readonly Matrix4x4 SunProjNear;

    static SkyManager ()
    {
        SunProjFar = Matrix4x4.CreateOrthographic(500, 500, 100, 2000);
        SunProjNear = Matrix4x4.CreateOrthographic(50, 50, 1, 1000.0f);
    }

    public Matrix4x4 SunViewFar;
    public Matrix4x4 SunViewNear;

    public Vector3 SunDirection { get; private set; }

    public SkyManager()
    {
        Name = "SkyManager";
        Enabled = true;
    }

    public void Initialize()
    {

    }

    public void Render(float dt, object? obj = null)
    {

    }

    public void UpdatePhysics(float dt)
    {

    }

    float timer = 0.0f;
    public void UpdateState(float dt)
    {
        timer += dt;
        
        Vector3 sunFar = new Vector3(MathF.Sin(timer / (60.0f)) * 500, 250, MathF.Cos(timer / (60.0f)) * 500) + GamePlayer.Transform.Position;
        Vector3 sunNear = new Vector3(MathF.Sin(timer / (60.0f)) * 128, 64, MathF.Cos(timer / (60.0f)) * 128) + GamePlayer.Transform.Position;

        SunDirection = Vector3.Normalize(GamePlayer.Transform.Position - sunFar);


        SunViewFar = Matrix4x4.CreateLookAt(sunFar, GamePlayer.Transform.Position, Vector3.UnitY);
        SunViewNear = Matrix4x4.CreateLookAt(sunNear, GamePlayer.Transform.Position, Vector3.UnitY);
    }
}
