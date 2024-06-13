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
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;
using Horizon.Rendering.Mesh;

using ImGuiNET;

using Silk.NET.OpenGL;

namespace CodeKneading;

internal class MainScene : Scene
{
    public override Camera ActiveCamera { get; protected set; }
    public GamePlayer Player { get; set; }

    protected VoxelWorld World;
    public static BufferObject CameraData { get; private set; }

    public override unsafe void Initialize()
    {
        this.World = AddEntity<VoxelWorld>();
        this.Player = AddEntity<GamePlayer>();
        base.Initialize();

        Engine.camera = this.ActiveCamera = this.Player.Camera;

        Engine.GL.ClearColor(System.Drawing.Color.CornflowerBlue);
        Engine.GL.Enable(Silk.NET.OpenGL.EnableCap.DepthTest);

        if (Engine.ObjectManager.Buffers.TryCreate(
            new BufferObjectDescription
            {
                Type = Silk.NET.OpenGL.BufferTargetARB.UniformBuffer
            },
            out var resBuf
            ))
        {
            CameraData = resBuf.Asset;
            CameraData.NamedBufferData((nuint)(sizeof(Matrix4x4) * 2 + sizeof(Vector4)));
        }

    }

    public override void Render(float dt, object? obj = null)
    {
        CameraData.NamedBufferSubData(new ReadOnlySpan<Matrix4x4>([ActiveCamera.View, ActiveCamera.Projection]), 0, 32 * 4);

        CameraData.NamedBufferSubData(new ReadOnlySpan<Vector4>([new Vector4(ActiveCamera.Position, 1.0f)]), 32 * 4, 4 * 4);
        base.Render(dt, obj);
    }
}
