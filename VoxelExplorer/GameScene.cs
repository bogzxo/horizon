using System.Numerics;
using System.Reflection.Emit;

using Horizon.Engine;
using Horizon.Input;
using Horizon.Input.Components;
using Horizon.Rendering.Mesh;

using VoxelExplorer.Data;

namespace VoxelExplorer
{
    internal class GameScene : Scene
    {
        private VoxelWorld world;
        private VoxelWorldRenderer renderer;
        private VoxelWorldGenerator generator;

        private Camera3D camera;

        public override Camera ActiveCamera { get; protected set; }

        private const float MOVEMENT_SPEED = 5.0f;
        private bool captureInput = true;

        public override void Initialize()
        {
            base.Initialize();

            generator = new();
            world = new(generator);

            generator.StartTask();


            renderer = AddEntity(new VoxelWorldRenderer(world));
            ActiveCamera = camera = AddEntity<Camera3D>();

            //camera.Position = new Vector3(VoxelWorld.WIDTH / 2 * Chunk.SIZE - Chunk.SIZE / 2, 48, VoxelWorld.DEPTH / 2 * Chunk.SIZE - Chunk.SIZE / 2);

            Engine.GL.ClearColor(System.Drawing.Color.CornflowerBlue);
            Engine.GL.Disable(Silk.NET.OpenGL.EnableCap.CullFace);
            Engine.GL.Enable(Silk.NET.OpenGL.EnableCap.Texture2D);
            Engine.GL.Enable(Silk.NET.OpenGL.EnableCap.DepthTest);
            MouseInputManager.Mouse.Cursor.CursorMode = Silk.NET.Input.CursorMode.Raw;

            Engine.Debugger.GeneralDebugger.AddWatch("camera position", () => camera.Position.ToString());
        }

        public override void UpdateState(float dt)
        {
            base.UpdateState(dt);

            if (Engine.InputManager.WasPressed(VirtualAction.Pause))
            {
                captureInput = !captureInput;
                MouseInputManager.Mouse.Cursor.CursorMode = captureInput ? Silk.NET.Input.CursorMode.Raw : Silk.NET.Input.CursorMode.Normal;
            }

            if (!captureInput) return;

            float movementSpeed = MOVEMENT_SPEED * (Engine.InputManager.KeyboardManager.IsKeyDown(Silk.NET.Input.Key.ShiftLeft) ? 6.0f : 1.0f);
            Vector2 axis = Engine.InputManager.GetVirtualController().MovementAxis;
            Vector3 oldPos = camera.Position;
            Vector3 cameraFrontNoPitch = Vector3.Normalize(new Vector3(camera.Front.X, 0, camera.Front.Z));
            Vector3 movement = (Vector3.Normalize(Vector3.Cross(camera.Front, Vector3.UnitY)) * movementSpeed * axis.X * dt +
                                movementSpeed * camera.Front * axis.Y * dt) * new Vector3(1, 1, 1);
            camera.Position = oldPos + movement;
        }
    }
}