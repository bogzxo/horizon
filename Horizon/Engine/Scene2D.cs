using System.Numerics;

using Horizon.Rendering;
using Horizon.Rendering.Lighting;
using Horizon.Rendering.Primitives;
using Horizon.Rendering.Spriting;
using Horizon.UI;

namespace Horizon.Engine;

/// <summary>
/// A scene with the usual lot already in it, for a game that wants to draw something rather than wire a renderer
/// up first. It comes with a camera the size of the window (a unit a pixel, the middle of the world in the middle
/// of the screen), a renderer that follows the window, a sprite batch and a primitive renderer inside of that, and a
/// UI over the top. Lit or not, and with the fancy lighting or not, is a flag each.
/// <code>
/// sealed class Pong : Scene2D
/// {
///     public Pong() : base(lit: true, fancy: true) { }
///
///     public override void Initialize()
///     {
///         base.Initialize();
///         Lighting!.AddLight(new Light2D { Position = Vector2.Zero, Radius = 400 });
///         Shapes.Describe = shapes => shapes.FillCircle(ball, 8, Vector3.One);
///     }
/// }
///
/// engine.Run&lt;Pong&gt;();
/// </code>
/// Everything is there to be reached for and nothing has to be used, a scene that only wants the camera takes the camera.
/// </summary>
public abstract class Scene2D : Scene
{
    private readonly bool lit;
    private readonly bool fancy;
    private readonly Vector2? designSize;

    /// <summary>The camera of the scene. As big as the window unless a design size was given.</summary>
    public Camera2D Camera { get; }

    /// <summary>What everything in the world is drawn into. The lit one when the scene is lit, see <see cref="Lighting"/>.</summary>
    public Renderer2D Renderer { get; private set; } = null!;

    /// <summary>The renderer as a lit one, null for a scene that isn't lit.</summary>
    public DeferredRenderer2D? Lighting { get; private set; }

    /// <summary>Sprites, add them here.</summary>
    public SpriteBatch Sprites { get; private set; } = null!;

    /// <summary>Shapes, write them down here (see <see cref="PrimitiveRenderer.Describe"/>). Drawn over the sprites.</summary>
    public PrimitiveRenderer Shapes { get; private set; } = null!;

    /// <summary>A UI over everything, laid out against the window.</summary>
    public UICompositor UI { get; }

    /// <summary>
    /// Whether the fancy (path traced) lighting is on, for a lit scene. From any thread, it takes at the next frame.
    /// </summary>
    public bool Fancy
    {
        get => Lighting?.Lighting == LightingMode.PathTraced;
        set
        {
            if (Lighting is { } lighting) lighting.Lighting = value ? LightingMode.PathTraced : LightingMode.Direct;
        }
    }

    /// <param name="lit">Whether the world is lit by a <see cref="DeferredRenderer2D"/>, with lights to add to it.</param>
    /// <param name="fancy">Whether the lit world starts out with the path traced lighting.</param>
    /// <param name="designSize">
    /// How much of the world the camera sees whatever size the window is, null for a unit a pixel of the window.
    /// </param>
    protected Scene2D(bool lit = false, bool fancy = false, Vector2? designSize = null)
    {
        this.lit = lit;
        this.fancy = fancy;
        this.designSize = designSize;

        Vector2 size = designSize ?? GameEngine.Instance.WindowManager.ViewportSize;
        Camera = AddEntity(new Camera2D(size));
        ActiveCamera = Camera;

        UI = AddComponent(UICompositor.ForScreen());
    }

    public override void Initialize()
    {
        Vector2 window = Engine.WindowManager.ViewportSize;
        uint width = (uint)MathF.Max(1.0f, window.X), height = (uint)MathF.Max(1.0f, window.Y);

        if (lit)
        {
            Lighting = new DeferredRenderer2D(width, height) { FollowWindow = true, Lighting = fancy ? LightingMode.PathTraced : LightingMode.Direct };
            Renderer = AddEntity(Lighting);
        }
        else
        {
            Renderer = AddEntity(new Renderer2D(width, height) { FollowWindow = true });
        }

        Sprites = Renderer.AddEntity(new SpriteBatch());
        // The primitive renderer glows by default, for debug drawings that want seeing in the dark. These are the world's shapes, they get lit like the rest
        Shapes = Renderer.AddEntity(new PrimitiveRenderer { Emissive = 0.0f });

        base.Initialize();
    }

    public override void UpdateState(float dt)
    {
        // The camera keeps up with the window unless it was told how much to see
        if (designSize is null)
        {
            Vector2 window = Engine.WindowManager.ViewportSize;
            if (Camera.ViewSize != window) Camera.ViewSize = window;
        }

        base.UpdateState(dt);
    }
}
