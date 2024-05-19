using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

using AutoVoxel.World;

using Box2D.NetStandard.Dynamics.World;

using CodeKneading.Player.Behaviour;
using CodeKneading.Voxel;

using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.Input.Components;

namespace CodeKneading.Player;

public class GamePlayer : GameObject
{
    private bool flyMode = true;
    private float cameraOrbit = 0.0f;

    // useful properties
    public bool IsGrounded { get; private set; }

    public Camera3D Camera { get; init; }
    public TransformComponent3D Transform { get; init; }

    private PlayerBehaviourManagerComponent BehaviourManager { get; init; }

    internal VoxelWorld World { get; private set; }

    public GamePlayer()
    {
        Camera = AddEntity(new Camera3D
        {
            Enabled = false, // Disable mouse updates
        });

        BehaviourManager = AddComponent<PlayerBehaviourManagerComponent>();

        Transform = AddComponent<TransformComponent3D>();
        Transform.Position = new Vector3(64, 32, 64);
    }

    public override void Initialize()
    {
        base.Initialize();
        World = Parent.GetEntity<VoxelWorld>()!;

        MouseInputManager.Mouse.Cursor.CursorMode = Silk.NET.Input.CursorMode.Raw;
    }

    public override void UpdatePhysics(float dt)
    {
        UpdateStateValues();

        base.UpdatePhysics(dt);
    }

    private void UpdateStateValues()
    {
        IsGrounded = World[(int)(Transform.Position.X), (int)(Transform.Position.Y - 2.1f), (int)(Transform.Position.Z)].Type != TileType.None;
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        Camera.Position = Vector3.Lerp(Camera.Position, Transform.Position, dt * 100.0f);
    }
}
