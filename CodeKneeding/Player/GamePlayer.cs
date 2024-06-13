using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

using Box2D.NetStandard.Dynamics.World;

using CodeKneading.Player.Behaviour;
using CodeKneading.Voxel;

using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.Input.Components;

using Silk.NET.OpenGL;

namespace CodeKneading.Player;

public class GamePlayer : GameObject
{
    private bool cameraLocked = false;
    private float cameraOrbit = 0.0f;

    // useful properties
    public bool IsGrounded { get; private set; }

    public Camera3D Camera { get; init; }
    public static TransformComponent3D Transform { get; private set; }

    private PlayerBehaviourManagerComponent BehaviourManager { get; init; }

    internal VoxelWorld World { get; private set; }
    public Vector3 LookTarget { get; private set; }

    public GamePlayer()
    {
        AddEntity(Camera = new Camera3D());

        BehaviourManager = AddComponent<PlayerBehaviourManagerComponent>();

        Transform = AddComponent<TransformComponent3D>();
        Transform.Position = new Vector3((VoxelWorld.LOADED_DISTANCE / 2) * TileChunk.SIZE, 32, (VoxelWorld.LOADED_DISTANCE / 2) * TileChunk.SIZE);
    }

    public override void Initialize()
    {
        base.Initialize();
        World = Parent.GetEntity<VoxelWorld>()!;

        MouseInputManager.Mouse.Cursor.CursorMode = Silk.NET.Input.CursorMode.Raw;
    }

    public override void UpdatePhysics(float dt)
    {
        
        base.UpdatePhysics(dt);
    }

    private void UpdateStateValues()
    {
        //IsGrounded = World[(int)(Transform.Position.X), (int)(Transform.Position.Y - 2.1f), (int)(Transform.Position.Z)].Type != TileType.None;
    }

    public override void Render(float dt, object? obj = null)
    {
        if (float.IsNaN(Transform.Position.X) || float.IsNaN(Transform.Position.Y) || float.IsNaN(Transform.Position.Z))
        {
            Transform.Position = new Vector3((VoxelWorld.LOADED_DISTANCE / 2) * TileChunk.SIZE, 32 + dt, (VoxelWorld.LOADED_DISTANCE / 2) * TileChunk.SIZE);
        }

        MouseInputManager.Mouse.Cursor.CursorMode = cameraLocked ? Silk.NET.Input.CursorMode.Normal : Silk.NET.Input.CursorMode.Raw;
        Camera.Position = Vector3.Lerp(Camera.Position, Transform.Position, dt * 100.0f);
        base.Render(dt, obj);
    }

    float time = 0.0f;
    public override void UpdateState(float dt)
    {
        time += dt;
        UpdateStateValues();

        //if (cameraLocked)
        //{
        //    Transform.Position = new Vector3(MathF.Sin(time) * VoxelWorld.LOADED_DISTANCE / 2 * TileChunk.SIZE / 2 + ((VoxelWorld.LOADED_DISTANCE * TileChunk.SIZE) / 2.0f), 64, MathF.Cos(time) * VoxelWorld.LOADED_DISTANCE / 2 * TileChunk.SIZE / 2 + ((VoxelWorld.LOADED_DISTANCE * TileChunk.SIZE) / 2.0f));
        //    LookTarget = new Vector3(((VoxelWorld.LOADED_DISTANCE * TileChunk.SIZE) / 2.0f), 0, +((VoxelWorld.LOADED_DISTANCE * TileChunk.SIZE) / 2.0f));
        //}


        if (Engine.InputManager.KeyboardManager.IsKeyPressed(Silk.NET.Input.Key.Escape))
        {
            cameraLocked = !cameraLocked;
            Camera.Enabled = !cameraLocked;
        }

        if (cameraLocked)
        {
            Camera.LookAt(LookTarget);
        }

        base.UpdateState(dt);
    }
}
