using System.Drawing;
using System.Numerics;

using Horizon.Core;
using Horizon.Core.Threading;

namespace Horizon.Engine;

/// <summary>
/// What the world is seen through: where from, which way, and through what lens.
/// <para>
/// There are two of everything it works out. The simulation's (<see cref="View"/>, <see cref="Bounds"/> and the rest,
/// as read from the updates) is worked out from where the camera is at the end of every tick, so a game that asks what
/// is on screen or where the pointer is in the world gets the same answer all tick long. A frame that is drawn
/// alongside the simulation (see <see cref="RenderFrame.IsDecoupled"/>) gets the camera as it was at the moment the
/// frame shows instead, between two ticks, out of what was published: the same properties read while drawing give that.
/// Whatever draws reads the camera the way it always did, and whatever updates the game does too.
/// </para>
/// </summary>
public abstract class Camera : GameObject
{
    protected Vector3 CameraFront = -Vector3.UnitZ;
    protected Vector3 CameraUp = Vector3.UnitY;

    // The simulation's, which is also what a frame drawn in turns with it goes by
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
    /// frames let it: rounded before it is shown between two ticks it would stand still and jump. Drawn in turns with the
    /// simulation it is rounded where it is. <see cref="Position"/> itself is never rounded.
    /// </summary>
    public float PixelSnap { get; set; }

    /// <summary>
    /// What the camera is rounded from (see <see cref="PixelSnap"/>): it is shown a whole number of steps away from
    /// here. Nothing for the steps of the world itself. A camera that follows somebody can be kept a whole number of
    /// pixels from them instead, so whoever it follows sits still on screen while the world steps by under them,
    /// rather than wobbling a pixel every time the camera steps at a different moment than they do, which looks shit.
    /// Shown between two ticks the way the camera is.
    /// </summary>
    public Vector2 PixelSnapAnchor { get; set; }

    private readonly MotionEstimator motion = new();

    /// <summary>
    /// How fast the camera is moving across the world, in units a second. Worked out from where it is every update
    /// (see <see cref="MotionEstimator"/>), this is what everything that is drawn measures its own motion on screen
    /// against: what stands still in the world goes by at this speed the other way.
    /// </summary>
    public Vector2 Velocity => TryShow(out Shown frame) ? frame.Velocity : motion.Velocity;

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

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);

        motion.Update(new Vector2(Position.X, Position.Y), dt);
    }

    public override void Render(float dt)
    {
        // Drawn in turns with the simulation (or while a scene is set up) the camera is there to be read as it is,
        // moved since the last tick or not. Drawn alongside it, it isn't: the frame goes by what was published
        if (!RenderFrame.Active.IsDecoupled)
            UpdateMatrices();

        base.Render(dt);
    }

    public override void Capture()
    {
        UpdateMatrices();

        Vector3 look = LookDirection;
        pose.Publish(new Pose(Position, look == Vector3.Zero ? CameraFront : look, CameraUp, projection, motion.Velocity, PixelSnapAnchor));

        base.Capture();
    }

    /// <summary>
    /// Has the camera not be shown on its way from where it was to where it is from now on: it was put there, a cut.
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
    /// wasn't published (one that isn't in the scene): the simulation's is what there is then.
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
        shown = new Shown(lookAt, at.Projection, lookAt * at.Projection, BoundsAt(position, at.Projection), at.Velocity);
        (shownCurrent, shownPrevious, shownAlpha) = (current.CurrentSequence, current.PreviousSequence, current.Alpha);

        frame = shown;
        return true;
    }

    /// <summary>Where the camera was at the end of a tick, and how it looked from there.</summary>
    private readonly record struct Pose(Vector3 Position, Vector3 Front, Vector3 Up, Matrix4x4 Projection, Vector2 Velocity, Vector2 Anchor) : IBlendable<Pose>
    {
        public static Pose Blend(in Pose from, in Pose to, float amount) => new(
            Interpolate.Linear(from.Position, to.Position, amount),
            Vector3.Normalize(Interpolate.Linear(from.Front, to.Front, amount)),
            Interpolate.Hold(from.Up, to.Up, amount),
            Matrix4x4.Lerp(from.Projection, to.Projection, amount),
            Interpolate.Linear(from.Velocity, to.Velocity, amount),
            Interpolate.Linear(from.Anchor, to.Anchor, amount));
    }

    /// <summary>The camera as a frame shows it.</summary>
    private readonly record struct Shown(Matrix4x4 View, Matrix4x4 Projection, Matrix4x4 ViewProj, RectangleF Bounds, Vector2 Velocity);

    /// <summary>
    /// Projects a screen space position to world space.
    /// </summary>
    /// <param name="screenPosition">The screen position.</param>
    /// <returns></returns>
    public Vector2 ScreenToWorld(Vector2 screenPosition)
    {
        // Normalize the screen position from [0, 1] to [-1, 1]
        Vector2 normalizedScreenPosition = new Vector2(
            (screenPosition.X / Engine.WindowManager.WindowSize.X) * 2.0f - 1.0f,
            1.0f - (screenPosition.Y / Engine.WindowManager.WindowSize.Y) * 2.0f
        );

        // Calculate the inverse view-projection matrix
        Matrix4x4 inverseViewProj;
        if (Matrix4x4.Invert(ViewProj, out inverseViewProj))
        {
            // Transform the normalized screen position into world coordinates
            Vector4 worldPosition4D = Vector4.Transform(
                new Vector4(normalizedScreenPosition, 0.0f, 1.0f),
                inverseViewProj
            );
            Vector3 worldPosition = new Vector3(
                worldPosition4D.X,
                worldPosition4D.Y,
                worldPosition4D.Z
            );

            return new Vector2(worldPosition.X, worldPosition.Y);
        }

        // Return a default value if the inverse matrix is not valid
        return Vector2.Zero;
    }

    /// <summary>
    /// Projects a screen space position to world space.
    /// </summary>
    /// <param name="screenPosition">The screen position.</param>
    /// <returns></returns>
    public Vector2 WorldToScreen(Vector2 screenPosition)
    {
        // Calculate the inverse view-projection matrix

        // Transform the normalized screen position into world coordinates
        Vector4 worldPosition4D = Vector4.Transform(
            new Vector4(screenPosition, 0.0f, 1.0f),
            ViewProj
        );
        Vector3 worldPosition = new Vector3(
            worldPosition4D.X,
            worldPosition4D.Y,
            worldPosition4D.Z
        );

        return new Vector2(worldPosition.X, worldPosition.Y);
    }
}
