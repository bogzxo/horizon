using System.Drawing;
using System.Numerics;

using Horizon.Core;
using Horizon.Core.Threading;

namespace Horizon.Engine;

/// <summary>
/// What the world is seen through, where from, which way and through what lens.
/// <para>
/// There are two of everything it works out. The simulation's (<see cref="View"/>, <see cref="Bounds"/> and the rest,
/// as read from the updates) is worked out from where the camera is at the end of every tick, so a game that asks what
/// is on screen or where the pointer is in the world gets the same answer all tick long. A frame that is drawn
/// alongside the simulation (see <see cref="RenderFrame.IsDecoupled"/>) gets the camera as it was at the moment the
/// frame shows instead, between two ticks, out of what was published. The same properties read while drawing give that.
/// Whatever draws reads the camera the way it always did, and whatever updates the game does too.
/// </para>
/// </summary>
public abstract class Camera : GameObject
{
    protected Vector3 CameraFront = -Vector3.UnitZ;
    protected Vector3 CameraUp = Vector3.UnitY;

    // The simulation's, which is also what a frame drawn with the simulation standing still goes by (a scene being set up)
    private Matrix4x4 view, projection, viewProj;
    private RectangleF bounds;

    // What was published at the end of every tick, and what was worked out of it for the frame that is being drawn
    private readonly Snapshot<Pose> pose = new();
    private Shown shown;
    private long shownCurrent = -1, shownPrevious = -1;
    private float shownAlpha = float.NaN;

    public Matrix4x4 View
    {
        get => TryShow(out Shown frame) ? frame.View : view;
        protected set => view = value;
    }

    public Matrix4x4 Projection
    {
        get => TryShow(out Shown frame) ? frame.Projection : projection;
        protected set => projection = value;
    }

    public Matrix4x4 ViewProj
    {
        get => TryShow(out Shown frame) ? frame.ViewProj : viewProj;
        protected set => viewProj = value;
    }

    public float Near { get; protected set; }
    public float Far { get; protected set; }

    /// <summary>What of the world the camera sees, for a camera that looks straight at a flat world.</summary>
    public RectangleF Bounds
    {
        get => TryShow(out Shown frame) ? frame.Bounds : bounds;
        protected set => bounds = value;
    }

    public Vector3 Position { get; set; }
    public Vector3 Direction { get; protected set; }

    /// <summary>
    /// How far apart (in units of the world) the places the camera is shown at are, 0 for anywhere. A camera that shows
    /// pixel art a unit per pixel wants 1 here, or the art shimmers as the camera glides over it. The camera is rounded
    /// to it once it has been worked out where it is at the moment a frame shows, so it still moves as smoothly as the
    /// frames let it. Rounded before it is shown between two ticks it would stand still and jump, which is the worst of both. Drawn with the
    /// simulation standing still it is rounded where it is. <see cref="Position"/> itself is never rounded.
    /// </summary>
    public float PixelSnap { get; set; }

    /// <summary>
    /// What the camera is rounded from (see <see cref="PixelSnap"/>), it is shown a whole number of steps away from
    /// here. Nothing for the steps of the world itself. A camera that follows somebody can be kept a whole number of
    /// pixels from them instead, so whoever it follows sits still on screen while the world steps by under them,
    /// rather than wobbling a pixel every time the camera steps at a different moment than they do, which looks shit.
    /// Shown between two ticks the way the camera is.
    /// </summary>
    public Vector2 PixelSnapAnchor { get; set; }

    /// <summary>Which way the camera looks.</summary>
    protected virtual Vector3 LookDirection => CameraFront;

    /// <summary>
    /// What of a flat world the camera sees from a place through a lens, nothing for a camera that doesn't look at one.
    /// </summary>
    protected virtual RectangleF BoundsAt(Vector3 position, in Matrix4x4 projection) => RectangleF.Empty;

    public override void Initialize()
    {
        base.Initialize();
        UpdateMatrices();
    }

    public override void Render(float dt)
    {
        // Drawn with the simulation standing still (a scene being set up, before the first tick) the camera is there to
        // be read as it is, moved since the last tick or not. Drawn alongside it, it isn't, and the frame goes by what was published
        if (!RenderFrame.Active.IsDecoupled)
            UpdateMatrices();

        base.Render(dt);
    }

    public override void Capture()
    {
        UpdateMatrices();

        Vector3 look = LookDirection;
        pose.Publish(new Pose(Position, look == Vector3.Zero ? CameraFront : look, CameraUp, projection, PixelSnapAnchor));

        base.Capture();
    }

    /// <summary>
    /// Has the camera not be shown on its way from where it was to where it is from now on. It was put there, a cut.
    /// Simulation thread.
    /// </summary>
    public void Snap() => pose.Break();

    /// <summary>
    /// Works out the simulation's matrices and bounds from where the camera is now.
    /// </summary>
    protected virtual void UpdateMatrices()
    {
        Vector3 look = LookDirection, position = Snapped(Position, PixelSnapAnchor);
        view = Matrix4x4.CreateLookAt(position, position + (look == Vector3.Zero ? CameraFront : look), CameraUp);
        viewProj = view * projection;
        bounds = BoundsAt(position, projection);
    }

    /// <summary>
    /// Helper method to round where the camera is shown to the steps of <see cref="PixelSnap"/>, counted from an anchor.
    /// </summary>
    private Vector3 Snapped(Vector3 position, Vector2 anchor)
    {
        float step = PixelSnap;
        if (step <= 0.0f)
            return position;

        position.X = anchor.X + MathF.Round((position.X - anchor.X) / step) * step;
        position.Y = anchor.Y + MathF.Round((position.Y - anchor.Y) / step) * step;
        return position;
    }

    /// <summary>
    /// Helper method to get the camera as the frame that is being drawn shows it, worked out once a frame. False on any
    /// thread but the render thread, outside of a frame that is drawn alongside the simulation, and for a camera that
    /// wasn't published (one that isn't in the scene). The simulation's is what there is then.
    /// </summary>
    private bool TryShow(out Shown frame)
    {
        RenderFrame current = RenderFrame.Active;
        if (!current.IsDecoupled)
        {
            frame = default;
            return false;
        }

        if (current.CurrentSequence == shownCurrent && current.PreviousSequence == shownPrevious && current.Alpha == shownAlpha)
        {
            frame = shown;
            return true;
        }

        if (!pose.TryBlend(current, out Pose at))
        {
            frame = default;
            return false;
        }

        Vector3 position = Snapped(at.Position, at.Anchor);

        Matrix4x4 lookAt = Matrix4x4.CreateLookAt(position, position + at.Front, at.Up);
        shown = new Shown(lookAt, at.Projection, lookAt * at.Projection, BoundsAt(position, at.Projection));
        (shownCurrent, shownPrevious, shownAlpha) = (current.CurrentSequence, current.PreviousSequence, current.Alpha);

        frame = shown;
        return true;
    }

    /// <summary>Where the camera was at the end of a tick, and how it looked from there.</summary>
    private readonly record struct Pose(Vector3 Position, Vector3 Front, Vector3 Up, Matrix4x4 Projection, Vector2 Anchor) : IBlendable<Pose>
    {
        public static Pose Blend(in Pose from, in Pose to, float amount) => new(
            Interpolate.Linear(from.Position, to.Position, amount),
            Vector3.Normalize(Interpolate.Linear(from.Front, to.Front, amount)),
            Interpolate.Hold(from.Up, to.Up, amount),
            Matrix4x4.Lerp(from.Projection, to.Projection, amount),
            Interpolate.Linear(from.Anchor, to.Anchor, amount));
    }

    /// <summary>The camera as a frame shows it.</summary>
    private readonly record struct Shown(Matrix4x4 View, Matrix4x4 Projection, Matrix4x4 ViewProj, RectangleF Bounds);

    /// <summary>
    /// Where a point of the window is in the world, the way the mouse says where it is (pixels from the top left
    /// corner of the window). For a camera that looks straight at a flat world.
    /// </summary>
    public Vector2 ScreenToWorld(Vector2 screenPosition)
    {
        // Out of the window and into clip space, which goes from -1 to 1 either way with its Y going up
        Vector2 window = Engine.WindowManager.WindowSize;
        var clip = new Vector4(
            screenPosition.X / window.X * 2.0f - 1.0f,
            1.0f - screenPosition.Y / window.Y * 2.0f,
            0.0f,
            1.0f);

        // And back out through everything the camera does to get there. A camera that can't be turned round
        // (a lens of no size at all, say) has nowhere to put it
        if (!Matrix4x4.Invert(ViewProj, out Matrix4x4 back))
            return Vector2.Zero;

        Vector4 world = Vector4.Transform(clip, back);
        return new Vector2(world.X, world.Y);
    }

    /// <summary>
    /// Where a place in the world is in the window, in pixels from its top left corner, the other way round from
    /// <see cref="ScreenToWorld"/>. This used to hand back clip space under the same name, which was no use to
    /// anybody who believed the name.
    /// </summary>
    public Vector2 WorldToScreen(Vector2 worldPosition)
    {
        Vector4 clip = Vector4.Transform(new Vector4(worldPosition, 0.0f, 1.0f), ViewProj);
        if (clip.W != 0.0f && clip.W != 1.0f)
            clip /= clip.W;

        Vector2 window = Engine.WindowManager.WindowSize;
        return new Vector2((clip.X * 0.5f + 0.5f) * window.X, (0.5f - clip.Y * 0.5f) * window.Y);
    }
}
