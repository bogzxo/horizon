using System.Numerics;

using Horizon.Engine;

namespace Horizon.Rendering.PostProcessing;

/// <summary>
/// Motion blur the way a camera with a slow shutter (or the phosphor of an old tube) does it. What was on screen a moment ago
/// is still there, fading, so everything that moves leaves a soft trail behind it. Rain turns into streaks, a fist into a swipe,
/// and a camera that follows somebody drags the street along behind them.
/// <para>
/// It doesn't need to know what moves or how fast. It keeps the picture it showed last and blends the new one into it, which
/// is all there is to it. That makes it work the same on anything that can be drawn (sprites, particles, a UI on its own layer)
/// and at any frame rate, as the fading goes by the clock and not by the frame.
/// </para>
/// There is not much to tune:
/// <code>
/// renderer.PostProcessing.Add(new MotionBlurEffect
/// {
///     Trail = 0.06f,    // how long a trail takes to fade, in seconds. Longer trails for a dreamier picture
///     Strength = 0.7f,  // how much of the trail shows, 0 is none of it
///     Solid = 1.0f,     // how much of whatever is moving stays as sharp as it is drawn, with only its trail behind it
///     Camera = camera   // the camera the picture is seen through, so that panning it doesn't smear what stands still
/// });
/// </code>
/// See <see cref="VelocityBlurEffect"/> for the other way of doing it, which smears along the way things are going.
/// </summary>
public sealed class MotionBlurEffect : PostEffect
{
    private const string UNIFORM_HISTORY = "uHistory";
    private const string UNIFORM_KEEP = "uKeep";
    private const string UNIFORM_SHIFT = "uShift";
    private const string UNIFORM_STRENGTH = "uStrength";
    private const string UNIFORM_SOLID = "uSolid";
    private const string UNIFORM_MOTION = "uMotion";
    private const string UNIFORM_HAS_MOTION = "uHasMotion";
    private const string UNIFORM_STILL = "uStill";

    // How motion is written into the picture of a renderer that keeps track of it. So many halves of the screen a second
    // either way, in so many steps. Must match encodeMotion in the shaders that write it
    private const float MOTION_RANGE = 2.0f;
    private const float MOTION_STEPS = 127.0f;

    // How far (in pictures) the camera can move in one frame before it counts as having been put somewhere else.
    // A camera that jumps leaves nothing worth remembering
    private const float FURTHEST_SHIFT = 0.25f;

    // How long (in seconds) the effect can go without being run before what it remembers is too old to show.
    // A UI that stood still for a while must not get a ghost of where it was back then
    private const float STALE_AFTER = 0.1f;

    private PostTechnique remember = null!, show = null!;

    // Where the middle of the world was on screen the last time, which is how far the camera has moved since is known
    private Vector2? originBefore;

    // The picture that was shown last, and the one the next is blended into. They swap every frame
    private PostTarget? history, next;
    private double lastRun = double.NegativeInfinity;

    /// <summary>
    /// How long a trail takes to fade, in seconds. After this long about a third of it is left, after three times this next to nothing.
    /// What moves is trailed by as far as it gets in that time, so a drop that falls 600 pixels a second at 0.05 leaves some 30 behind it.
    /// </summary>
    public float Trail { get; set; } = 0.05f;

    /// <summary>
    /// How much of the trail shows, from 0 (none of it) to 1. At 1 the thing that moves is smeared into its own trail, lower than that
    /// it stays crisp and the trail is a ghost behind it. Pixel art that is to stay readable wants this well under 1.
    /// </summary>
    public float Strength { get; set; } = 0.7f;

    /// <summary>
    /// How much of whatever is moving right now is shown as sharp as it is drawn, from 0 to 1. At 1 a fighter who dashes stays solid and
    /// leaves a ghost behind, at 0 they are smeared into their own trail and go see-through the faster they are. It takes a renderer that
    /// keeps track of what moves (see <see cref="PostContext.Motion"/>), on any other everything is blended the same.
    /// </summary>
    public float Solid { get; set; } = 1.0f;

    /// <summary>
    /// The camera the picture is seen through, null if there isn't one that moves (a UI). With one, what the picture is remembered by
    /// moves along with the camera. So a pan doesn't smear the scenery, and only what moves through the world leaves a trail.
    /// </summary>
    public Camera? Camera { get; set; }

    // Only for what moves. A picture nothing moves in is the same with and without its trails
    protected internal override bool NeedsMotion => true;

    protected override void Initialize()
    {
        remember = new PostTechnique("trail");
        show = new PostTechnique("trail_show");
    }

    /// <summary>
    /// Helper method to work out how far what stands still has moved across the picture since the last frame, in pictures.
    /// Null if the camera has jumped, or was looked through a different lens.
    /// </summary>
    private Vector2? MeasureShift()
    {
        if (Camera is not { } camera)
            return Vector2.Zero;

        // Where the middle of the world lands on screen. Everything that stands still moves by as much as that does
        Vector4 projected = Vector4.Transform(new Vector4(0.0f, 0.0f, 0.0f, 1.0f), camera.ViewProj);
        var origin = new Vector2(projected.X, projected.Y);

        Vector2? before = originBefore;
        originBefore = origin;

        if (before is not { } was)
            return null;

        // The screen is two units across the way the GPU counts it, a picture is one
        Vector2 shift = (was - origin) * 0.5f;
        return MathF.Abs(shift.X) > FURTHEST_SHIFT || MathF.Abs(shift.Y) > FURTHEST_SHIFT ? null : shift;
    }

    protected override void Render(PostContext context)
    {
        float strength = Math.Clamp(Strength, 0.0f, 1.0f);
        if (Trail <= 0.0f || strength <= 0.0f)
        {
            context.Copy();
            return;
        }

        uint width = (uint)context.SourceSize.X, height = (uint)context.SourceSize.Y;
        bool fresh = history is null || !history.Fits(width, height);
        if (fresh)
        {
            history?.Dispose();
            next?.Dispose();

            // More than a byte a channel. Blended into itself over and over a byte rounds the same way every time, and leaves a stain that never fades
            history = new PostTarget(width, height, PostTarget.Precise);
            next = new PostTarget(width, height, PostTarget.Precise);
        }

        // Not run for a while (switched off, or nothing moved), so what is remembered is from back then and nobody wants to see it
        double now = GameEngine.Instance.TotalTime;
        if (now - lastRun > MathF.Max(STALE_AFTER, context.DeltaTime * 3.0f)) fresh = true;
        lastRun = now;

        // Measured every frame whether it is used or not, so it is always since the last one
        Vector2? moved = MeasureShift();
        if (moved is null) fresh = true;

        // How much of what was there is kept. By the clock, so the trails are as long at thirty frames a second as at three hundred
        float keep = fresh ? 0.0f : MathF.Exp(-context.DeltaTime / Trail);
        Vector2 shift = moved ?? Vector2.Zero;

        // The new picture into what is left of the old ones
        remember.Bind();
        context.Source.Bind(0);
        history!.Texture.Bind(1);
        remember.SetUniform(UNIFORM_KEEP, keep);
        remember.SetUniform(UNIFORM_SHIFT, in shift);
        context.Draw(next!);

        (history, next) = (next, history);

        // And as much of that over the new picture as the strength says, with what is moving itself left alone
        show.Bind();
        context.Source.Bind(0);
        history!.Texture.Bind(1);
        context.Motion?.Bind(2);
        show.SetUniform(UNIFORM_HAS_MOTION, context.Motion is not null);
        show.SetUniform(UNIFORM_STRENGTH, strength);
        show.SetUniform(UNIFORM_SOLID, Math.Clamp(Solid, 0.0f, 1.0f));

        // What stands still in the world goes across the screen as fast as the camera does, the other way. That is not moving
        Vector2 still = Camera is { } camera
            ? -camera.Velocity * new Vector2(camera.Projection.M11, camera.Projection.M22) / MOTION_RANGE * MOTION_STEPS
            : Vector2.Zero;
        show.SetUniform(UNIFORM_STILL, in still);
        context.Draw();

        show.Unbind();
    }

    public override void Dispose()
    {
        history?.Dispose();
        next?.Dispose();
        history = next = null;

        base.Dispose();
    }
}
