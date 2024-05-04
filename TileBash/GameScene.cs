global using static Horizon.Rendering.Tiling<TileBash.TileTextureID>;

using System.Drawing;
using System.Numerics;

using Bogz.Logging;
using Bogz.Logging.Loggers;

using Box2D.NetStandard.Dynamics.World;

using Horizon.Core;
using Horizon.Engine;
using Horizon.GameEntity.Components.Physics2D;
using Horizon.Rendering;
using Horizon.Rendering.Particles;
using Horizon.Rendering.Primitives;
using Horizon.Rendering.Spriting;

using Silk.NET.Input;
using Silk.NET.OpenGL;

using TileBash.Animals;
using TileBash.Player;

using TiledSharp;

namespace TileBash;

public class GameScene : Scene
{
    public override Camera ActiveCamera { get; protected set; }

    private Random random;
    private Player2D player;
    private SpriteBatch spriteBatch;
    private Camera2D cam;
    private TileMap tilemap;
    private World world;
    private ParticleRenderer2D rainParticleSystem;
    private DeferredRenderer2D deferredRenderer;
    private PrimitiveRenderer primitiveRenderer;

    private readonly Dictionary<RectangleF, uint> zOffsetTriggers = new();

    private int catCounter = 0;

    public GameScene()
    {
        primitiveRenderer = new PrimitiveRenderer(PrimitiveRenderer.UploadMethod.Automatic);

        if ((tilemap = TileMap.FromTiledMap(this, "content/maps/main.tmx", objCallback)!) == null)
        {
            ConcurrentLogger.Instance.Log(LogLevel.Fatal, "Failed to load tilemap, aborting...");
            Environment.Exit(1);
        }

        primitiveRenderer.Add(new ShapePrimitive
        {
            Type = 0,
            Position = new Vector2(0, 0),
            Scale = Vector2.One
        });
    }

    private void objCallback(TmxObject? obj)
    {
        if (obj is null || !(obj.Properties.TryGetValue("player_level", out string? zSetStr) && uint.TryParse(zSetStr, out uint zSet))) return;

        // Assuming tile height is available from somewhere
        float tileHeight = 16;

        zOffsetTriggers.Add(new RectangleF { X = (float)obj.X, Y = (float)obj.Y, Width = (float)obj.Width, Height = (float)obj.Height }, zSet);

        // Calculate the center of the rectangle, adjusting for half a tile height
        float centerX = (float)obj.X + (float)obj.Width / 2.0f;
        float centerY = (float)obj.Y + (float)obj.Height / 2.0f - (tileHeight / 2.0f);

        // Adjust the Position property to represent the center of the shape
        primitiveRenderer.Add(new ShapePrimitive
        {
            Type = (uint)PrimitiveShapeType.Rectangle,
            Position = new Vector2(centerX, centerY),
            // Scale remains the same
            Scale = new Vector2((float)obj.Width / 2.0f, (float)obj.Height / 2.0f)
        });
    }



    public override void Initialize()
    {
        base.Initialize();

        InitializeGl();
        random = new Random(Environment.TickCount);

        world = AddComponent<Box2DWorldComponent>();

        AddEntity(player = new Player2D(world, tilemap));
        deferredRenderer = AddEntity<DeferredRenderer2D>(
            new((uint)Engine.WindowManager.WindowSize.X, (uint)Engine.WindowManager.WindowSize.Y)
        );
        cam = AddEntity<Camera2D>(new(deferredRenderer.ViewportSize / 4.0f));
        ActiveCamera = cam;
        deferredRenderer.AddEntity(tilemap);

        spriteBatch = tilemap.AddEntity<SpriteBatch>();
        spriteBatch.Add(player);

        spriteBatch.AddEntity(
            rainParticleSystem = new ParticleRenderer2D(100_000)
            {
                MaxAge = 2.5f,
                StartColor = new Vector3(4, 0, 255) / new Vector3(255),
                EndColor = new Vector3(66, 135, 245) / new Vector3(255),
                Enabled = true
            }
        );

        //spriteBatch.AddEntity(primitiveRenderer);

        AddEntity(
            new IntervalRunner(
                1 / 25.0f,
                () =>
                {
                    (float, float) roll(int diag) =>
                        (
                            random.NextSingle() * Engine.WindowManager.WindowSize.X + diag / 2.0f,
                            random.NextSingle() * Engine.WindowManager.WindowSize.Y + diag / 2.0f
                        );

                    for (int diagonal = 0; diagonal < 4; diagonal++)
                    {
                        (var x, var y) = roll(diagonal); // slight bias
                        SpawnParticle(cam.ScreenToWorld(new Vector2(x, y)), -Vector2.One, 0.2f);
                    }
                }
            )
        );

        base.Initialize();
        primitiveRenderer.Add(new ShapePrimitive
        {
            Type = (uint)PrimitiveShapeType.Rectangle,
            Position = new Vector2(0),
            Scale = new Vector2(0)
        });
    }

    private void InitializeGl()
    {
        Engine.GL.ClearColor(System.Drawing.Color.CornflowerBlue);
        Engine.GL.Enable(EnableCap.Texture2D);
        //Engine.GL.Enable(EnableCap.StencilTest);
        Engine.GL.Enable(EnableCap.Blend);
        Engine.GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
    }

    private float cameraMovement = 1f;

    public override void UpdateState(float dt)
    {
        primitiveRenderer.ViewMatrix = cam.ProjView;

        if (Engine.InputManager.KeyboardManager.IsKeyPressed(Key.E))
            cameraMovement = Math.Clamp(cameraMovement - 2, 0, 16);
        else if (Engine.InputManager.KeyboardManager.IsKeyPressed(Key.Q))
            cameraMovement = Math.Clamp(cameraMovement + 2, 1, 16);

        cam.Zoom = cameraMovement < 2 ? 1 : 2 * MathF.Round((cameraMovement) / 2);

        if (Engine.InputManager.KeyboardManager.IsKeyPressed(Key.G))
        {
            catCounter += 128;
        }
        if (Engine.InputManager.KeyboardManager.IsKeyPressed(Key.F))
        {
            SpawnCircle();
        }
        if (
            Engine
                .InputManager
                .MouseManager
                .GetData()
                .Actions
                .HasFlag(Horizon.Input.VirtualAction.PrimaryAction)
        )
        {
            var mouseData = Engine.InputManager.MouseManager.GetData();
            SpawnParticle(
                cam.ScreenToWorld(mouseData.Position),
                new Vector2(1.0f - mouseData.Direction.X, mouseData.Direction.Y)
            );
        }


        int index = 0;
        foreach (var (bounds, level) in this.zOffsetTriggers)
        {
            primitiveRenderer.Shapes[index] = primitiveRenderer.Shapes[index] with
            {
                Color = Vector3.Zero
            };
            if (player.BoundingBox.IntersectsWith(bounds))
            {
                primitiveRenderer.Shapes[index] = primitiveRenderer.Shapes[index] with
                {
                    Color = Vector3.One
                };
                player.ZIndex = level;
            }
            index++;
        }

        primitiveRenderer[3] = primitiveRenderer[3] with
        {
            Position = player.Position,
            Scale = player.Transform.Size / 2.0f,
            Type = (uint)PrimitiveShapeType.Rectangle,
        };
        cam.Position = new Vector3(player.Position.X, player.Position.Y, 0.0f);
        base.UpdateState(dt);
    }

    public override void Render(float dt, object? obj = null)
    {
        base.Render(dt);

        if (catCounter > 0)
        {
            Cat[] gattos = new Cat[catCounter];
            for (int i = 0; i < catCounter; i++)
            {
                var x = random.NextSingle() * Engine.WindowManager.WindowSize.X;
                var y = random.NextSingle() * Engine.WindowManager.WindowSize.Y;

                var cat = new Cat();
                cat.Transform.Position = cam.ScreenToWorld(new Vector2(x, y));

                gattos[i] = AddEntity(cat);
            }
            spriteBatch.AddRange(gattos);
            catCounter = 0;
        }
    }

    private void SpawnParticle(Vector2 pos, Vector2 dir, float blend = 0.5f)
    {
        float val = ((random.NextSingle() * 2.0f) - MathF.PI);

        rainParticleSystem.Add(
            new Particle2D(
                new Vector2(MathF.Sin(val), MathF.Cos(val)) * (1.0f - blend) + dir * blend,
                pos
            )
        );
    }

    private void SpawnCircle(int count = 250)
    {
        float val = 0.0f;
        for (int i = 0; i < count; i++)
        {
            val = random.NextSingle() * MathF.PI * 2.0f;

            rainParticleSystem.Add(
                new Particle2D(new Vector2(MathF.Cos(val), MathF.Sin(val)), player.Position)
            );
        }
    }
}