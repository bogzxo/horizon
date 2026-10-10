using System.Numerics;

using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Rendering;
using Horizon.Rendering.Meshes;

using Silk.NET.Input;

namespace Horizon.Testing.Examples.Game;

/// <summary>
/// The third dimension. A floor, a crate with the town's bricks on it, a ball, a can and a sheet of hills built out
/// of vertices by hand, all lit by one sun and seen through a <see cref="Camera3D"/> that goes round them. The
/// engine draws flat things by the thousand and this is the other thing it does, the same device, the same
/// renderers and the same post processing, with a depth buffer and a lens.
/// <para>
/// What to look at. <see cref="Renderer3D"/>, a renderer whose children are drawn with the depth test on.
/// <see cref="Mesh3D"/> and the shapes it comes as, <see cref="Mesh3D.Upload"/> for triangles of your own with
/// <see cref="Mesh3D.ComputeNormals"/> to light them, <see cref="Mesh3D.Cull"/> and the winding it goes by, and
/// <see cref="Camera3D"/> with <see cref="Camera3D.LookAt"/>. The sun is <see cref="Mesh3D.Sun"/>, one for every
/// mesh, there is no 3D lighting beyond it yet.
/// </para>
/// </summary>
public class MeshesExample : Scene, ITestControls
{
    private const string BRICKS = "Assets/examples/world/tiles_albedo.png";

    // How far the camera stands off and how fast it goes round, and how fast the ball bobs
    private const float ORBIT = 7.0f;
    private const float ORBIT_SPEED = 0.25f;

    public override Camera ActiveCamera { get; protected set; } = null!;

    // Listed on screen by the test host
    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("Mouse drag", "turn the camera, the wheel brings it in and out"),
        new("Space", "stop and start the turning"),
        new("C", "cull the backs or not, look at the inside of the can"),
        new("W", "wobble the hills"),
    ];

    private Camera3D _camera = null!;
    private Mesh3D _crate = null!, _ball = null!, _can = null!, _hills = null!;
    private Vertex3D[] _hillVertices = [];
    private uint[] _hillIndices = [];

    private float _time, _yaw = 0.6f, _pitch = 0.45f, _distance = ORBIT;
    private bool _turning = true, _wobbling, _culling = true;

    public override void Initialize()
    {
        Vector2 window = Engine.WindowManager.ViewportSize;

        _camera = AddEntity(new Camera3D(window.X / window.Y) { Position = new Vector3(0.0f, 3.0f, ORBIT) });
        _camera.LookAt(Vector3.Zero);
        ActiveCamera = _camera;

        var world = AddEntity(new Renderer3D((uint)window.X, (uint)window.Y)
        {
            FollowWindow = true,
            ClearColor = new Vector4(0.42f, 0.55f, 0.72f, 1.0f)
        });

        if (!Engine.ObjectManager.Textures.TryCreate(
                new TextureDescription { Paths = [BRICKS], Definition = TextureDefinition.RgbaUnsignedByteNearest },
                out var bricks))
        {
            throw new Exception($"Couldn't make a texture out of {BRICKS}: {bricks.Message}");
        }

        var floor = world.AddEntity(Mesh3D.Plane(12.0f, 12.0f, 6.0f));
        floor.Texture = bricks.Asset;
        floor.Tint = new Vector4(0.7f, 0.72f, 0.75f, 1.0f);

        _crate = world.AddEntity(Mesh3D.Cube(1.4f));
        _crate.Texture = bricks.Asset;
        _crate.Transform.Position = new Vector3(-2.2f, 0.7f, 0.0f);

        _ball = world.AddEntity(Mesh3D.Sphere(0.8f));
        _ball.Tint = new Vector4(0.95f, 0.35f, 0.3f, 1.0f);

        _can = world.AddEntity(Mesh3D.Cylinder(0.6f, 1.6f));
        _can.Tint = new Vector4(0.35f, 0.75f, 0.45f, 1.0f);
        _can.Transform.Position = new Vector3(2.4f, 0.8f, 0.0f);

        // Triangles by hand, a grid of hills behind everything. The normals are worked out from the triangles,
        // nobody has to know which way a hill faces
        _hills = world.AddEntity(new Mesh3D { Cull = CullMode.None });
        _hills.Tint = new Vector4(0.55f, 0.65f, 0.4f, 1.0f);
        _hills.Transform.Position = new Vector3(0.0f, 0.0f, -7.0f);
        BuildHills(0.0f);

        base.Initialize();
    }

    /// <summary>Helper method to lay the hills out, a grid of quads with the heights of a few sines, at a moment in time.</summary>
    private void BuildHills(float time)
    {
        const int ACROSS = 24, DEEP = 12;
        const float WIDTH = 24.0f, DEPTH = 10.0f;

        if (_hillVertices.Length == 0)
        {
            _hillVertices = new Vertex3D[(ACROSS + 1) * (DEEP + 1)];
            var indices = new List<uint>(ACROSS * DEEP * 6);
            for (int z = 0; z < DEEP; z++)
            {
                for (int x = 0; x < ACROSS; x++)
                {
                    uint a = (uint)(z * (ACROSS + 1) + x), b = a + ACROSS + 1;
                    indices.AddRange([a, a + 1, b, a + 1, b + 1, b]);
                }
            }

            _hillIndices = [.. indices];
        }

        for (int z = 0; z <= DEEP; z++)
        {
            for (int x = 0; x <= ACROSS; x++)
            {
                float u = x / (float)ACROSS, v = z / (float)DEEP;
                float wx = (u - 0.5f) * WIDTH, wz = (0.5f - v) * DEPTH;
                float height = 1.2f * MathF.Sin(u * 9.0f + time) * MathF.Cos(v * 5.0f - time * 0.7f) + 0.8f * MathF.Sin(u * 3.0f) + 0.6f;

                _hillVertices[z * (ACROSS + 1) + x] = new Vertex3D(new Vector3(wx, height, wz), Vector3.UnitY, new Vector2(u, v));
            }
        }

        Mesh3D.ComputeNormals(_hillVertices, _hillIndices);
        _hills.Upload(_hillVertices, _hillIndices);
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        _time += dt;
        var keyboard = Engine.Input.Keyboard;
        var mouse = Engine.Input.Mouse;

        if (keyboard.WasPressed(Key.Space)) _turning = !_turning;
        if (keyboard.WasPressed(Key.W)) _wobbling = !_wobbling;
        if (keyboard.WasPressed(Key.C))
        {
            _culling = !_culling;
            _crate.Cull = _ball.Cull = _can.Cull = _culling ? CullMode.Back : CullMode.None;
        }

        if (_turning) _yaw += ORBIT_SPEED * dt;
        if (mouse.IsDown(MouseButton.Left))
        {
            _yaw += mouse.Delta.X * 0.005f;
            _pitch = Math.Clamp(_pitch - mouse.Delta.Y * 0.005f, 0.05f, 1.4f);
        }

        _distance = Math.Clamp(_distance - mouse.Scroll * 0.5f, 2.5f, 20.0f);

        // Round and round the middle, a camera is moved by saying where it is and what it looks at
        _camera.Position = new Vector3(MathF.Sin(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), MathF.Cos(_yaw) * MathF.Cos(_pitch)) * _distance + new Vector3(0.0f, 0.8f, 0.0f);
        _camera.LookAt(new Vector3(0.0f, 0.8f, 0.0f));

        _crate.Transform.Rotation = Quaternion.CreateFromYawPitchRoll(_time * 0.5f, 0.0f, 0.0f);
        _ball.Transform.Position = new Vector3(0.0f, 1.4f + MathF.Sin(_time * 2.0f) * 0.5f, 0.0f);
        _can.Transform.Rotation = Quaternion.CreateFromYawPitchRoll(0.0f, 0.0f, MathF.Sin(_time) * 0.25f);
    }

    public override void Render(float dt)
    {
        // Rebuilt on the render thread, the buffers are the GPU's
        if (_wobbling) BuildHills(_time);
        base.Render(dt);
    }
}
