using System.Numerics;

using Horizon.Core.Threading;
using Horizon.Engine;
using Horizon.Input;
using Horizon.OpenGL;
using Horizon.Rendering;
using Horizon.Rendering.Primitives;
using Horizon.Rendering.Spriting.Data;
using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

using Silk.NET.Input;

namespace Horizon.Testing.Examples.Rendering;

/// <summary>
/// Flat coloured shapes with no textures, no sprites and no fuss: the three shapes the primitive renderer knows, lines
/// made out of them, a box of bouncing balls with debug overlays drawn over the top, a bar graph, and a
/// <see cref="Mesh2D"/> star for when three shapes aren't enough. Everything moves on its own.
/// <para>
/// What to look at: <see cref="PrimitiveRenderer"/> (every shape in one draw call), <see cref="ShapePrimitive"/> and
/// <see cref="PrimitiveShapeType"/> (triangle, rectangle, circle), <see cref="PrimitiveRenderer.UploadMethod.Manual"/>
/// with <see cref="PrimitiveRenderer.UploadAll"/>, <see cref="PrimitiveRenderer.ViewMatrix"/> fed with
/// <see cref="Camera.ViewProj"/>, a <see cref="SnapshotBuffer{T}"/> to get the list of shapes from the simulation to
/// the render thread, and a <see cref="Mesh2D"/> with your own <see cref="Technique"/> and <see cref="Mesh2D.Upload"/>.
/// It's all drawn inside a <see cref="Renderer2D"/>.
/// </para>
/// <para>
/// In your own game:
/// <code>
/// class Overlay : PrimitiveRenderer
/// {
///     public Overlay() : base(UploadMethod.Manual) { }
///
///     public override void Render(float dt)                 // render thread
///     {
///         Shapes.Clear();
///         // Scale is HALF the size, rotation is in degrees, colour is rgb (no alpha)
///         Shapes.Add(new ShapePrimitive(PrimitiveShapeType.Circle, position, new Vector2(radius), colour, 0.0f));
///
///         ViewMatrix = camera.ViewProj;                     // view AND projection, the shader has nothing else
///         UploadAll();
///         base.Render(dt);
///     }
/// }
///
/// // Initialize, render thread
/// renderer.AddEntity(new Overlay());
/// </code>
/// </para>
/// </summary>
public class PrimitivesExample : Scene, ITestControls
{
    // The camera and the UI both see exactly this much of the world, so a spot in the UI is the same spot in the world
    private static readonly Vector2 DesignSize = new(1600, 900);

    private static readonly Vector4 Background = new(0.07f, 0.08f, 0.12f, 1.0f);
    private static readonly Vector4 PanelColour = new(0.1f, 0.12f, 0.17f, 0.88f);
    private static readonly Vector4 CaptionColour = new(1.0f, 1.0f, 1.0f, 0.85f);

    // Shape colours are rgb only, the shader paints every one fully opaque. "Faint" here means darker, not see-through
    private static readonly Vector3 Ink = new(0.92f, 0.94f, 1.0f);
    private static readonly Vector3 ArenaFloor = new(0.11f, 0.13f, 0.19f);
    private static readonly Vector3 ArenaWall = new(0.45f, 0.52f, 0.7f);
    private static readonly Vector3 BoxColour = new(0.25f, 0.6f, 0.38f);
    private static readonly Vector3 ArrowColour = new(1.0f, 0.82f, 0.25f);
    private static readonly Vector3 AxisColour = new(0.3f, 0.34f, 0.45f);
    private static readonly Vector3 WaveColour = new(0.3f, 0.85f, 1.0f);

    // The box the balls bounce about in, in units of the world (the middle of the screen is 0, 0 and up is up)
    private static readonly Vector2 ArenaMin = new(-290.0f, -360.0f);
    private static readonly Vector2 ArenaMax = new(380.0f, 320.0f);

    private const float GRAVITY = 900.0f;
    private const float BOUNCE = 0.86f;
    private const int START_BALLS = 30, MIN_BALLS = 5, MAX_BALLS = 300, BALLS_A_PRESS = 10;

    // The balls get a kick this often on their own, so the thing never settles into a heap and goes boring
    private const float KICK_EVERY = 2.5f;

    // Where the three specimens, the wave and the bar graph live
    private const float SPECIMEN_Y = 150.0f;
    private static readonly Vector2 WaveFrom = new(-770.0f, -130.0f);
    private const float WAVE_WIDTH = 400.0f, WAVE_HEIGHT = 70.0f;
    private const int WAVE_SEGMENTS = 48;

    private const int BARS = 16;
    private const float BAR_WIDTH = 16.0f, BAR_GAP = 5.0f, BAR_MAX = 200.0f;
    private static readonly Vector2 BarsFrom = new(442.0f, -400.0f);

    // The Mesh2D star, top right
    private static readonly Vector2 StarCentre = new(610.0f, 290.0f);
    private const float STAR_RADIUS = 100.0f;

    public override Camera ActiveCamera { get; protected set; }

    // Listed on screen by the test host
    public IReadOnlyList<TestControl> Controls { get; } =
    [
        new("Space / A", "kick the balls"),
        new("D / X", "debug overlays on / off"),
        new("R / Y", "scatter the balls (a teleport)"),
        new("Up / Down, D-pad", "more / fewer balls")
    ];

    private readonly Camera2D _camera;
    private readonly UICompositor _ui;
    private Label _stats = null!;

    // The simulation's side of things. Only ever touched on the simulation thread (or before it starts)
    private readonly List<Ball> _balls = [];
    private readonly float[] _peaks = new float[BARS];
    private readonly Random _random = new(7);
    private bool _overlays = true;
    private float _kickTimer;
    private int _shapeCount;

    private ShapeLayer _shapes = null!;

    public PrimitivesExample()
    {
        _camera = AddEntity(new Camera2D(DesignSize));
        ActiveCamera = _camera;

        // UI components own nothing on the GPU (the compositor makes its bits in its own Initialize), so the whole UI
        // can be built right here
        _ui = AddComponent(new UICompositor(_camera) { DesignSize = DesignSize });
        BuildCaptions();

        // The balls are plain data, nothing to do with GL, so they're fine in the constructor
        AddBalls(START_BALLS);
    }

    public override void Initialize()
    {
        // Render thread, with the simulation parked: the spot where GPU things get made. Everything made in here is
        // noted in Scene.Assets and freed when the scene's left, so no cleaning up by hand
        var renderer = AddEntity(new Renderer2D((uint)DesignSize.X, (uint)DesignSize.Y) { ClearColor = Background });

        // One PrimitiveRenderer for the lot. It's ONE draw call however many shapes are in it, and they come out in
        // the order they're in the list, so later shapes sit on top of earlier ones. That's your layering sorted
        _shapes = renderer.AddEntity(new ShapeLayer(_camera, DescribeShapes));

        // The star goes in after, so it's drawn over whatever the shapes put there
        renderer.AddEntity(new StarMesh(_camera, StarCentre));

        base.Initialize();
    }

    public override void UpdateState(float dt)
    {
        // Simulation thread. Game logic, input and the UI live here
        base.UpdateState(dt);

        _kickTimer += dt;
        if (_kickTimer >= KICK_EVERY || Pressed(Key.Space, GamepadInput.A))
        {
            _kickTimer = 0.0f;
            Kick();
        }

        if (Pressed(Key.D, GamepadInput.X))
            _overlays = !_overlays;

        if (Pressed(Key.R, GamepadInput.Y))
            Scatter();

        if (Pressed(Key.Up, GamepadInput.DPadUp))
            AddBalls(Math.Min(BALLS_A_PRESS, MAX_BALLS - _balls.Count));

        if (Pressed(Key.Down, GamepadInput.DPadDown))
            _balls.RemoveRange(Math.Max(MIN_BALLS, _balls.Count - BALLS_A_PRESS), Math.Min(BALLS_A_PRESS, _balls.Count - MIN_BALLS));

        // The bar graph's peak caps fall slowly back down to whatever the bar is doing now
        for (int i = 0; i < BARS; i++)
            _peaks[i] = MathF.Max(BarHeight(i), _peaks[i] - 110.0f * dt);

        _stats.Text = $"{_balls.Count} balls, {_shapeCount} shapes, one draw call\noverlays {(_overlays ? "on" : "off")}";
    }

    public override void UpdatePhysics(float dt)
    {
        // Simulation thread, fixed step. Anything you integrate (velocity into position) goes here: dt is always the
        // same, so the balls bounce the same however busy the machine is
        base.UpdatePhysics(dt);

        foreach (Ball ball in _balls)
        {
            ball.Velocity.Y -= GRAVITY * dt;
            ball.Position += ball.Velocity * dt;
            KeepInArena(ball);
        }

        Collide();
    }

    /// <summary>
    /// Helper method to write down everything that's drawn this tick, back to front. The <see cref="ShapeLayer"/>
    /// calls it from its Capture (simulation thread, end of the tick), or from its Render while the simulation is
    /// standing still (the scene warming up), so it only ever reads the game while nothing else is changing it.
    /// </summary>
    private void DescribeShapes(List<Shape> shapes)
    {
        float time = (float)Time;

        DescribeSpecimens(shapes, time);
        DescribeWave(shapes, time);
        DescribeArena(shapes);
        DescribeBars(shapes);

        _shapeCount = shapes.Count;
    }

    /// <summary>
    /// Helper method for the three shapes the renderer knows, each with a dot where its Position is.
    /// </summary>
    private static void DescribeSpecimens(List<Shape> shapes, float time)
    {
        // Rotation is in degrees, anticlockwise. The triangle's tip points up at 0
        shapes.Add(new Shape(PrimitiveShapeType.Triangle, new Vector2(-690.0f, SPECIMEN_Y), new Vector2(76.0f),
            time * 90.0f, Rainbow(time * 0.15f)));

        float squash = 12.0f * MathF.Sin(time * 2.0f);
        shapes.Add(new Shape(PrimitiveShapeType.Rectangle, new Vector2(-570.0f, SPECIMEN_Y), new Vector2(64.0f + squash, 64.0f - squash),
            -time * 45.0f, Rainbow(time * 0.15f + 0.33f)));

        // The circle's really a 12 sided polygon (the geometry shader only makes that many corners). Turning it slowly
        // shows it up. Big circles look a bit crap, small ones you'll never notice
        shapes.Add(new Shape(PrimitiveShapeType.Circle, new Vector2(-450.0f, SPECIMEN_Y), new Vector2(72.0f + 8.0f * MathF.Sin(time * 3.0f)),
            time * 20.0f, Rainbow(time * 0.15f + 0.66f)));

        // Position is the middle of a shape (the triangle's is where its three corners balance, not halfway up)
        for (int i = 0; i < 3; i++)
            shapes.Add(new Shape(PrimitiveShapeType.Circle, new Vector2(-690.0f + i * 120.0f, SPECIMEN_Y), new Vector2(6.0f), 0.0f, Ink));
    }

    /// <summary>
    /// Helper method for a scrolling sine wave: two axes and a line through points, every bit of it rectangles.
    /// </summary>
    private static void DescribeWave(List<Shape> shapes, float time)
    {
        Line(shapes, WaveFrom, WaveFrom + new Vector2(WAVE_WIDTH, 0.0f), 2.0f, AxisColour);
        Line(shapes, WaveFrom - new Vector2(0.0f, WAVE_HEIGHT + 10.0f), WaveFrom + new Vector2(0.0f, WAVE_HEIGHT + 10.0f), 2.0f, AxisColour);

        Vector2 previous = WavePoint(0, time);
        for (int i = 1; i <= WAVE_SEGMENTS; i++)
        {
            Vector2 point = WavePoint(i, time);
            Line(shapes, previous, point, 4.0f, WaveColour);
            previous = point;
        }

        // A dot every few points, drawn after the line so they sit on top of it
        for (int i = 0; i <= WAVE_SEGMENTS; i += 8)
            shapes.Add(new Shape(PrimitiveShapeType.Circle, WavePoint(i, time), new Vector2(12.0f), 0.0f, Ink));
    }

    /// <summary>
    /// Helper method for the arena: floor, walls, the balls, and the debug overlays over the top of the lot.
    /// </summary>
    private void DescribeArena(List<Shape> shapes)
    {
        // A filled rectangle for the floor first, then the walls as lines over it
        shapes.Add(new Shape(PrimitiveShapeType.Rectangle, (ArenaMin + ArenaMax) / 2.0f, ArenaMax - ArenaMin, 0.0f, ArenaFloor));
        Outline(shapes, ArenaMin, ArenaMax, 4.0f, ArenaWall);

        // No outline circles, so a ball's rim is a slightly bigger dark circle behind it. Plus a little shine
        foreach (Ball ball in _balls)
        {
            shapes.Add(new Shape(PrimitiveShapeType.Circle, ball.Position, new Vector2(ball.Radius * 2.0f + 5.0f), 0.0f, ball.Colour * 0.35f));
            shapes.Add(new Shape(PrimitiveShapeType.Circle, ball.Position, new Vector2(ball.Radius * 2.0f), 0.0f, ball.Colour));
            shapes.Add(new Shape(PrimitiveShapeType.Circle, ball.Position + new Vector2(-0.3f, 0.35f) * ball.Radius,
                new Vector2(ball.Radius * 0.5f), 0.0f, Vector3.Lerp(ball.Colour, Vector3.One, 0.7f)));
        }

        if (!_overlays)
            return;

        // Debug overlays go after the balls, so no ball is ever drawn over them. This is the bread and butter of the primitive
        // renderer: bung a box round something, point an arrow where it's going, done
        foreach (Ball ball in _balls)
        {
            Vector2 extent = new(ball.Radius + 4.0f);
            Outline(shapes, ball.Position - extent, ball.Position + extent, 1.5f, BoxColour);
            Arrow(shapes, ball.Position, ball.Position + ball.Velocity * 0.08f, ArrowColour);
        }
    }

    /// <summary>
    /// Helper method for the bar graph: rectangles grown up from a baseline, and a cap on each where its peak was.
    /// </summary>
    private void DescribeBars(List<Shape> shapes)
    {
        for (int i = 0; i < BARS; i++)
        {
            float height = BarHeight(i);
            float x = BarsFrom.X + i * (BAR_WIDTH + BAR_GAP) + BAR_WIDTH / 2.0f;

            // A rectangle's Position is its middle, so a bar standing on the baseline goes half its height up
            shapes.Add(new Shape(PrimitiveShapeType.Rectangle, new Vector2(x, BarsFrom.Y + height / 2.0f), new Vector2(BAR_WIDTH, height),
                0.0f, HeatColour(height / BAR_MAX)));
            shapes.Add(new Shape(PrimitiveShapeType.Rectangle, new Vector2(x, BarsFrom.Y + _peaks[i] + 5.0f), new Vector2(BAR_WIDTH, 4.0f),
                0.0f, Ink));
        }

        Line(shapes, BarsFrom - new Vector2(4.0f, 2.0f), BarsFrom + new Vector2(BARS * (BAR_WIDTH + BAR_GAP), -2.0f), 3.0f, AxisColour);
    }

    /// <summary>
    /// Helper method for a line from one point to another. There's no line shape, but a rectangle as long as the line
    /// and as wide as it is thick, turned to face along it, is exactly the same thing. It's made a thickness longer
    /// (half at each end) so lines that join up don't leave a notch at the corner.
    /// </summary>
    private static void Line(List<Shape> shapes, Vector2 from, Vector2 to, float thickness, Vector3 colour)
    {
        Vector2 along = to - from;

        // Always added, even when it's zero long, so the list is the same length every tick (see ShapeLayer.Render)
        shapes.Add(new Shape(PrimitiveShapeType.Rectangle, (from + to) / 2.0f, new Vector2(along.Length() + thickness, thickness),
            float.RadiansToDegrees(MathF.Atan2(along.Y, along.X)), colour));
    }

    /// <summary>
    /// Helper method for the outline of a box, four lines.
    /// </summary>
    private static void Outline(List<Shape> shapes, Vector2 min, Vector2 max, float thickness, Vector3 colour)
    {
        Line(shapes, min, new Vector2(max.X, min.Y), thickness, colour);
        Line(shapes, new Vector2(max.X, min.Y), max, thickness, colour);
        Line(shapes, max, new Vector2(min.X, max.Y), thickness, colour);
        Line(shapes, new Vector2(min.X, max.Y), min, thickness, colour);
    }

    /// <summary>
    /// Helper method for an arrow: a line with a triangle on the end, the triangle turned to point the same way.
    /// </summary>
    private static void Arrow(List<Shape> shapes, Vector2 from, Vector2 to, Vector3 colour)
    {
        Vector2 along = to - from;
        float degrees = float.RadiansToDegrees(MathF.Atan2(along.Y, along.X));

        Line(shapes, from, to, 2.0f, colour);

        // The triangle points up at 0 degrees and a line's angle counts from pointing right, hence the - 90. The head
        // shrinks away to nothing on a short arrow rather than being left out, so the list stays the same length
        float head = MathF.Min(14.0f, along.Length() * 0.4f);
        shapes.Add(new Shape(PrimitiveShapeType.Triangle, to, new Vector2(head), degrees - 90.0f, colour));
    }

    /// <summary>
    /// Helper method to give the balls a boot: up, and a bit sideways. Simulation thread.
    /// </summary>
    private void Kick()
    {
        foreach (Ball ball in _balls)
            ball.Velocity += new Vector2(Between(-300.0f, 300.0f), Between(500.0f, 950.0f));
    }

    /// <summary>
    /// Helper method to put every ball somewhere new at once. Simulation thread.
    /// </summary>
    private void Scatter()
    {
        foreach (Ball ball in _balls)
        {
            ball.Position = RandomSpot(ball.Radius);
            ball.Velocity = new Vector2(Between(-200.0f, 200.0f), 0.0f);
        }

        // Put there, not moved there. Without this the frames between this tick and the last draw every ball sliding
        // across the arena to its new spot, which looks like a bloody mess
        _shapes.Break();
    }

    /// <summary>
    /// Helper method to drop some new balls in somewhere random, each its own size and colour. Simulation thread, or
    /// the constructor.
    /// </summary>
    private void AddBalls(int count)
    {
        for (int i = 0; i < count; i++)
        {
            float radius = Between(10.0f, 22.0f);
            _balls.Add(new Ball
            {
                Radius = radius,
                Position = RandomSpot(radius),
                Velocity = new Vector2(Between(-300.0f, 300.0f), Between(-100.0f, 300.0f)),
                Colour = Rainbow(_random.NextSingle())
            });
        }
    }

    /// <summary>
    /// Helper method to bounce a ball back off the walls of the arena.
    /// </summary>
    private static void KeepInArena(Ball ball)
    {
        Vector2 min = ArenaMin + new Vector2(ball.Radius), max = ArenaMax - new Vector2(ball.Radius);

        if (ball.Position.X < min.X) { ball.Position.X = min.X; ball.Velocity.X = MathF.Abs(ball.Velocity.X) * BOUNCE; }
        if (ball.Position.X > max.X) { ball.Position.X = max.X; ball.Velocity.X = -MathF.Abs(ball.Velocity.X) * BOUNCE; }
        if (ball.Position.Y > max.Y) { ball.Position.Y = max.Y; ball.Velocity.Y = -MathF.Abs(ball.Velocity.Y) * BOUNCE; }

        if (ball.Position.Y < min.Y)
        {
            ball.Position.Y = min.Y;
            ball.Velocity.Y = MathF.Abs(ball.Velocity.Y) * BOUNCE;
            ball.Velocity.X *= 0.98f;
        }
    }

    /// <summary>
    /// Helper method to bump the balls off each other: push the overlap apart, swap a bit of speed along the line
    /// between them. Every pair against every pair, which is fine for a few hundred and no more.
    /// </summary>
    private void Collide()
    {
        for (int i = 0; i < _balls.Count; i++)
        {
            Ball a = _balls[i];
            for (int j = i + 1; j < _balls.Count; j++)
            {
                Ball b = _balls[j];
                Vector2 between = b.Position - a.Position;
                float reach = a.Radius + b.Radius, distanceSquared = between.LengthSquared();
                if (distanceSquared >= reach * reach || distanceSquared < 0.0001f)
                    continue;

                float distance = MathF.Sqrt(distanceSquared);
                Vector2 normal = between / distance;

                // Heavier the bigger it is, so the big ones shove the little ones about
                float massA = a.Radius * a.Radius, massB = b.Radius * b.Radius, total = massA + massB;
                float overlap = reach - distance;
                a.Position -= normal * overlap * (massB / total);
                b.Position += normal * overlap * (massA / total);

                float closing = Vector2.Dot(b.Velocity - a.Velocity, normal);
                if (closing >= 0.0f)
                    continue;

                float impulse = -(1.0f + BOUNCE) * closing / (1.0f / massA + 1.0f / massB);
                a.Velocity -= normal * (impulse / massA);
                b.Velocity += normal * (impulse / massB);
            }
        }
    }

    /// <summary>
    /// Helper method for how tall a bar of the graph is right now: a few sines on top of each other, which looks
    /// enough like music if you squint.
    /// </summary>
    private float BarHeight(int bar)
    {
        float time = (float)Time;
        float wobble = 0.5f
            + 0.25f * MathF.Sin(time * 2.3f + bar * 0.55f)
            + 0.15f * MathF.Sin(time * 5.1f - bar * 1.3f)
            + 0.1f * MathF.Sin(time * 9.7f + bar * bar * 0.21f);

        return 12.0f + (BAR_MAX - 12.0f) * Math.Clamp(wobble, 0.0f, 1.0f);
    }

    /// <summary>
    /// Helper method for one point of the wave, scrolling along with time.
    /// </summary>
    private static Vector2 WavePoint(int index, float time)
    {
        float along = index / (float)WAVE_SEGMENTS;
        float y = MathF.Sin(along * MathF.Tau * 1.5f - time * 2.0f) * (0.7f + 0.3f * MathF.Sin(time * 0.7f));
        return WaveFrom + new Vector2(along * WAVE_WIDTH, y * WAVE_HEIGHT);
    }

    /// <summary>
    /// Helper method for somewhere random in the top half of the arena, far enough from the walls for a ball.
    /// </summary>
    private Vector2 RandomSpot(float radius) => new(
        Between(ArenaMin.X + radius, ArenaMax.X - radius),
        Between((ArenaMin.Y + ArenaMax.Y) / 2.0f, ArenaMax.Y - radius));

    /// <summary>
    /// Helper method for a random number between two others. Only the simulation thread rolls dice.
    /// </summary>
    private float Between(float min, float max) => min + _random.NextSingle() * (max - min);

    /// <summary>
    /// Helper method for a bright colour from anywhere round the colour wheel, 0 to 1 is once round.
    /// </summary>
    private static Vector3 Rainbow(float turn)
    {
        float angle = turn * MathF.Tau;
        return new Vector3(
            0.55f + 0.45f * MathF.Cos(angle),
            0.55f + 0.45f * MathF.Cos(angle - MathF.Tau / 3.0f),
            0.55f + 0.45f * MathF.Cos(angle - 2.0f * MathF.Tau / 3.0f));
    }

    /// <summary>
    /// Helper method for green when there's nothing to it, through yellow to red when it's flat out.
    /// </summary>
    private static Vector3 HeatColour(float amount) =>
        amount < 0.5f
            ? Vector3.Lerp(new Vector3(0.2f, 0.85f, 0.4f), new Vector3(1.0f, 0.85f, 0.2f), amount * 2.0f)
            : Vector3.Lerp(new Vector3(1.0f, 0.85f, 0.2f), new Vector3(1.0f, 0.3f, 0.25f), amount * 2.0f - 1.0f);

    /// <summary>
    /// Helper method for a key, or a button on whichever gamepad was used last, going down this update.
    /// </summary>
    private static bool Pressed(Key key, GamepadInput button) =>
        Engine.Input.Keyboard.WasPressed(key) || Engine.Input.Gamepads.LastUsed?.WasPressed(button) == true;

    /// <summary>
    /// Helper method to put up the panel in the corner and a caption for everything on screen.
    /// </summary>
    private void BuildCaptions()
    {
        var module = _ui.CreateModule();

        var panel = module.AddComponent(new StackPanel
        {
            Anchor = Origin.TopLeft,
            Position = new Vector2(24, -24),
            Color = PanelColour,
            Padding = new UIEdges(18),
            Spacing = 10
        });
        panel.Add(new Label("Shapes") { Anchor = Origin.Left, TextScale = 0.36f });
        panel.Add(new Label(
            "Every shape on screen is one PrimitiveRenderer,\n" +
            "one draw call. The star is a Mesh2D with its\n" +
            "own shader, uploaded once and spun on the GPU.")
        {
            Anchor = Origin.Left,
            Align = Origin.TopLeft,
            TextScale = 0.22f,
            Color = CaptionColour
        });

        Label Caption(string text, Vector2 position) =>
            module.AddComponent(new Label(text) { Position = position, TextScale = 0.2f, Color = CaptionColour });

        Caption("Triangle", new Vector2(-690, SPECIMEN_Y - 75));
        Caption("Rectangle", new Vector2(-570, SPECIMEN_Y - 75));
        Caption("Circle", new Vector2(-450, SPECIMEN_Y - 75));
        Caption("Lines are thin rectangles, turned\nto face the next point", new Vector2(WaveFrom.X + WAVE_WIDTH / 2.0f, WaveFrom.Y - 125));
        Caption("Mesh2D: your own triangles", new Vector2(StarCentre.X, StarCentre.Y - STAR_RADIUS - 50));
        Caption("Rectangles grown up from a line", new Vector2(BarsFrom.X + BARS * (BAR_WIDTH + BAR_GAP) / 2.0f, BarsFrom.Y + BAR_MAX + 40));

        _stats = Caption(string.Empty, new Vector2((ArenaMin.X + ArenaMax.X) / 2.0f, ArenaMax.Y + 40));
    }

    /// <summary>One bouncing ball. The simulation's, the render thread never sees one.</summary>
    private sealed class Ball
    {
        public Vector2 Position, Velocity;
        public float Radius;
        public Vector3 Colour;
    }

    /// <summary>
    /// One shape as the simulation sees it, blendable so a frame drawn between two ticks gets one in between.
    /// <see cref="ShapePrimitive"/> can't be used for this: you can't read its colour back out.
    /// </summary>
    /// <param name="Type">Triangle, rectangle or circle.</param>
    /// <param name="Position">Where its middle is, in the world.</param>
    /// <param name="Size">The whole width and height. ShapePrimitive wants half of that, see <see cref="ToPrimitive"/>.</param>
    /// <param name="Rotation">In degrees, anticlockwise.</param>
    /// <param name="Colour">Red, green and blue. There's no alpha, every shape is solid.</param>
    private readonly record struct Shape(PrimitiveShapeType Type, Vector2 Position, Vector2 Size, float Rotation, Vector3 Colour)
        : IBlendable<Shape>
    {
        public static Shape Blend(in Shape from, in Shape to, float amount) => new(
            to.Type,
            Interpolate.Linear(from.Position, to.Position, amount),
            Interpolate.Linear(from.Size, to.Size, amount),
            Interpolate.Angle(from.Rotation, to.Rotation, amount),
            Interpolate.Linear(from.Colour, to.Colour, amount));

        /// <summary>
        /// What the renderer wants. Its Scale goes out from the middle both ways (-1 to 1 times the scale), so it's half
        /// the size you'd think. Hand it the whole size and everything comes out twice as big, which will do your head in.
        /// </summary>
        public ShapePrimitive ToPrimitive() => new(Type, Position, Size / 2.0f, Colour, Rotation);
    }

    /// <summary>
    /// A <see cref="PrimitiveRenderer"/> that draws whatever the simulation said to at the end of its last two ticks.
    /// <para>
    /// The renderer on its own reads its <see cref="PrimitiveRenderer.Shapes"/> list on the render thread while it
    /// draws, and the automatic upload copies the whole list up to the GPU about 60 times a second. Fill that list from
    /// UpdateState and the render thread reads it while the simulation's halfway through changing it: at best things
    /// flicker, at worst the list grows under it and you get an exception. So this one uploads by hand
    /// (<see cref="PrimitiveRenderer.UploadMethod.Manual"/>) and builds the list itself, on the render thread, out of
    /// what the simulation published in Capture.
    /// </para>
    /// </summary>
    private sealed class ShapeLayer : PrimitiveRenderer
    {
        private readonly Camera _camera;
        private readonly Action<List<Shape>> _describe;

        // A list for every snapshot slot, made once and reused: the simulation fills one in while the render thread
        // reads the two of its frame, and nobody writes to those while it does
        private readonly SnapshotBuffer<List<Shape>> _published = new(() => new List<Shape>(1024));

        // The live list, for frames drawn with the simulation standing still
        private readonly List<Shape> _live = new(1024);

        public ShapeLayer(Camera camera, Action<List<Shape>> describe) : base(UploadMethod.Manual)
        {
            _camera = camera;
            _describe = describe;
        }

        /// <summary>
        /// Has the next ticks not be blended with the ones before: everything was put where it is, not moved there.
        /// Simulation thread.
        /// </summary>
        public void Break() => _published.Break();

        public override void Capture()
        {
            // Simulation thread, at the end of every tick: write down what's drawn, into the list of this capture
            if (_published.BeginPublish() is { } shapes)
            {
                shapes.Clear();
                _describe(shapes);
            }

            base.Capture();
        }

        public override void Render(float dt)
        {
            // Render thread. Build the renderer's own list for this frame, then let it upload and draw
            Shapes.Clear();

            RenderFrame frame = RenderFrame.Active;
            if (frame.IsDecoupled)
            {
                if (_published.TryGet(frame, out List<Shape> previous, out List<Shape> current, out bool continuous))
                {
                    // Shapes are matched up by where they are in the list, so this only works if the list is built in
                    // the same order every tick. When it changes length (a ball added, overlays switched) there's
                    // nothing to match against, so it's treated like a Break(): the older tick until the frame's all
                    // the way at the newer one. Never anything in between, which would be a mess
                    if (continuous && previous.Count == current.Count)
                    {
                        for (int i = 0; i < current.Count; i++)
                            Shapes.Add(Shape.Blend(previous[i], current[i], frame.Alpha).ToPrimitive());
                    }
                    else
                    {
                        foreach (Shape shape in frame.Alpha >= 1.0f ? current : previous)
                            Shapes.Add(shape.ToPrimitive());
                    }
                }
            }
            else
            {
                // The simulation's standing still (the scene's being set up, or there hasn't been a tick yet), so
                // the game itself is safe to read
                _live.Clear();
                _describe(_live);
                foreach (Shape shape in _live)
                    Shapes.Add(shape.ToPrimitive());
            }

            // The shader only has a view matrix and the renderer's own model matrix, no projection. Leave it at its
            // default (identity) and you're drawing straight in clip space, where a 50 unit circle covers the whole
            // screen and then some. The camera's ViewProj is the lot, and read here it's the camera as this frame shows it.
            // The renderer's Transform (the model matrix) is read live too, so leave it be: move the shapes, not it
            ViewMatrix = _camera.ViewProj;

            // Copies the list up to the GPU. Whatever doesn't fit in the renderer's buffer (a few thousand shapes,
            // depends on the driver) is left out with a warning in the log
            UploadAll();

            base.Render(dt);
        }
    }

    /// <summary>
    /// A star with a ring round it, made of triangles we build ourselves. <see cref="Mesh2D"/> holds the vertices and
    /// the triangles between them, and draws them with whatever <see cref="Technique"/> it's given. It sets no
    /// uniforms of its own, so the technique has to do all of that.
    /// </summary>
    private sealed class StarMesh(Camera camera, Vector2 centre) : Mesh2D
    {
        private const int POINTS = 5, RING_SEGMENTS = 64;

        public override void Initialize()
        {
            // Render thread. The technique compiles a shader, which is GL, so it can't be made any earlier than this.
            // It has to be set before base.Initialize(): the mesh draws with it from the first frame
            Shader = new StarTechnique(camera, centre);

            // Makes the vertex buffer and tells it what a Vertex2D looks like
            base.Initialize();

            // Upload replaces the whole lot every time you call it, buffers and all. Fine once, or now and then when
            // the shape really changes, but don't go calling it every frame to move something. Move it in the shader
            // (the way this one spins) or with a uniform instead
            BuildStar(out Vertex2D[] vertices, out uint[] indices);
            Upload(vertices, indices);
        }

        /// <summary>
        /// Helper method to build the star: a fan of triangles from its middle out to ten corners, five tips and five
        /// dips between them. Then a ring round it, a strip of two triangles a segment. The texture coordinate
        /// carries how far out a vertex is (x) and whether it's the ring (y), for the shader.
        /// </summary>
        private static void BuildStar(out Vertex2D[] vertices, out uint[] indices)
        {
            const int CORNERS = POINTS * 2;
            vertices = new Vertex2D[1 + CORNERS + RING_SEGMENTS * 2];
            indices = new uint[CORNERS * 3 + RING_SEGMENTS * 6];

            vertices[0] = new Vertex2D(Vector2.Zero, Vector2.Zero);
            for (int i = 0; i < CORNERS; i++)
            {
                bool tip = i % 2 == 0;
                float angle = MathF.PI / 2.0f + i * MathF.PI / POINTS;
                float radius = tip ? STAR_RADIUS : STAR_RADIUS * 0.42f;
                vertices[1 + i] = new Vertex2D(new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius, new Vector2(tip ? 1.0f : 0.45f, 0.0f));

                // Three indices a triangle, anticlockwise. Middle, this corner, the next one round
                indices[i * 3] = 0;
                indices[i * 3 + 1] = (uint)(1 + i);
                indices[i * 3 + 2] = (uint)(1 + (i + 1) % CORNERS);
            }

            int ring = 1 + CORNERS, at = CORNERS * 3;
            for (int i = 0; i < RING_SEGMENTS; i++)
            {
                Vector2 direction = new(MathF.Cos(i * MathF.Tau / RING_SEGMENTS), MathF.Sin(i * MathF.Tau / RING_SEGMENTS));
                vertices[ring + i * 2] = new Vertex2D(direction * (STAR_RADIUS + 18.0f), Vector2.One);
                vertices[ring + i * 2 + 1] = new Vertex2D(direction * (STAR_RADIUS + 26.0f), Vector2.One);

                // A quad between this segment and the next, as two triangles. 64 segments and it's properly round,
                // unlike the primitive renderer's 12 sided circles
                uint inner = (uint)(ring + i * 2), outer = inner + 1;
                uint nextInner = (uint)(ring + (i + 1) % RING_SEGMENTS * 2), nextOuter = nextInner + 1;
                indices[at++] = inner; indices[at++] = outer; indices[at++] = nextInner;
                indices[at++] = outer; indices[at++] = nextOuter; indices[at++] = nextInner;
            }
        }
    }

    /// <summary>
    /// The star's shader (shaders/testing/primitives.vert and .frag), and every uniform it needs. Bind() calls
    /// <see cref="SetUniforms"/>, so this is set every time the mesh draws, on the render thread.
    /// </summary>
    private sealed class StarTechnique : Technique
    {
        private readonly Camera _camera;
        private readonly Vector2 _centre;

        public StarTechnique(Camera camera, Vector2 centre)
        {
            // Compiled once and cached on disk after that, every StarTechnique gets the same shader
            LoadShader("shaders/testing", "primitives");
            _camera = camera;
            _centre = centre;
        }

        protected override void SetUniforms()
        {
            base.SetUniforms();

            SetUniform("uViewProjection", _camera.ViewProj);
            SetUniform("uCentre", _centre);

            // The star's spin is pure eye candy that the game never reads, so it doesn't need publishing at all: the
            // frame's own clock is enough. PresentationTime is the simulated time the frame shows, so it pauses with the
            // simulation and moves as smoothly as everything else (it's 0 while the scene's warming up, no harm done)
            SetUniform("uTime", (float)RenderFrame.Active.PresentationTime);
        }
    }
}
