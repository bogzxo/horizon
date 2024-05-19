using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

using Box2D.NetStandard.Dynamics.World;

using CodeKneading.Player;
using CodeKneading.Voxel;

using Horizon.Engine;
using Horizon.Input.Components;
using Horizon.Rendering.Mesh;

using ImGuiNET;

namespace CodeKneading;

internal class MainScene : Scene
{
    public override Camera ActiveCamera { get; protected set; }
    public GamePlayer Player { get; init; }

    protected readonly VoxelWorld World;

    public MainScene()
    {
        this.World = AddEntity<VoxelWorld>();
        this.Player = AddEntity<GamePlayer>();
        this.ActiveCamera = this.Player.Camera;
    }

    public override void Initialize()
    {
        Engine.GL.ClearColor(System.Drawing.Color.CornflowerBlue);
        Engine.GL.Enable(Silk.NET.OpenGL.EnableCap.DepthTest);

        base.Initialize();
    }
}
