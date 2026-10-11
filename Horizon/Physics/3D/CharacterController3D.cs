using System.Numerics;

using Horizon.Core;
using Horizon.Core.Components;

namespace Horizon.Physics.Colliders;

/// <summary>
/// Somebody walking about a world with depth, first person or third, a box on feet that the
/// <see cref="CollisionWorld3D"/> stops at every wall. Walks, sprints, jumps, steps up a slab without being asked,
/// swims in whatever the world says is wet, flies when told to, and crouched won't walk off an edge. Add it to the
/// scene, say where they want to go every update, and put the camera at <see cref="Eye"/>.
/// <code>
/// player = AddComponent(new CharacterController3D(collision, spawn));
///
/// // every update, from the keys and the way the camera faces
/// player.Move = forward * keyboard.Axis(Key.S, Key.W) + right * keyboard.Axis(Key.A, Key.D);
/// player.Jumping = keyboard.IsDown(Key.Space);
/// camera.Position = player.Eye;
/// </code>
/// <para>
/// It is moved the way every block game since the first one has moved people, one axis at a time, up and down first,
/// clipped against every box in the way. Never through a wall however fast, never stuck on the seam between two
/// blocks of a floor, and sliding along a wall it is pushed into rather than stopping dead. A key is a push and not
/// a speed (<see cref="GroundAcceleration"/>), so they get going over a moment and skid a little when they let go,
/// less of either in the air (<see cref="AirAcceleration"/>).
/// </para>
/// Simulation thread, in the physics step.
/// </summary>
public sealed class CharacterController3D(CollisionWorld3D world, Vector3 feet) : GameComponent
{
    // How far apart two things can be and still count as touching, so standing on the floor stays standing on it
    private const float SKIN = 1e-4f;

    // How far below the feet the ground is looked for, for whether they are on it
    private const float GROUND_PROBE = 0.02f;

    private readonly List<Box3> solids = [];

    public CollisionWorld3D World { get; } = world;

    /// <summary>Where their feet are, the middle of the bottom of the box.</summary>
    public Vector3 Position { get; set; } = feet;

    /// <summary>How fast they are going, units a second.</summary>
    public Vector3 Velocity { get; set; }

    /// <summary>How big they are, wide, tall and deep. A Minecraft player, unless it says.</summary>
    public Vector3 Size { get; set; } = new(0.6f, 1.8f, 0.6f);

    /// <summary>How far above their feet their eyes are.</summary>
    public float EyeHeight { get; set; } = 1.62f;

    /// <summary>Where the camera goes for first person.</summary>
    public Vector3 Eye => Position + new Vector3(0.0f, Crouching ? EyeHeight - 0.25f : EyeHeight, 0.0f);

    /// <summary>The box of them where they are now.</summary>
    public Box3 Bounds => Box3.FromFeet(Position, Size);

    /* What they want, set every update */

    /// <summary>
    /// Which way they want to go, flat (Y is ignored unless flying), up to a length of one. Usually the keys turned
    /// the way the camera faces.
    /// </summary>
    public Vector3 Move { get; set; }

    /// <summary>Whether jump is held. On the ground it jumps, in water it swims up, flying it goes up.</summary>
    public bool Jumping { get; set; }

    /// <summary>Whether they are going down on purpose, which is down when flying and swimming.</summary>
    public bool Descending { get; set; }

    public bool Sprinting { get; set; }

    /// <summary>Crouched, slower, a bit shorter in the eye and they won't walk off an edge.</summary>
    public bool Crouching { get; set; }

    /// <summary>Flying, no gravity, the space bar and <see cref="Descending"/> go up and down. And through nothing still.</summary>
    public bool Flying { get; set; }

    /// <summary>Through walls as well, for looking at a world rather than playing in it.</summary>
    public bool NoClip { get; set; }

    /* How they move */

    /// <summary>Units a second, walking.</summary>
    [Inspect(0.0f, 20.0f)]
    public float WalkSpeed { get; set; } = 4.3f;

    [Inspect(0.0f, 20.0f)]
    public float SprintSpeed { get; set; } = 5.6f;

    [Inspect(0.0f, 10.0f)]
    public float CrouchSpeed { get; set; } = 1.3f;

    [Inspect(0.0f, 100.0f)]
    public float FlySpeed { get; set; } = 11.0f;

    /// <summary>How much faster flying is with sprint held.</summary>
    [Inspect(1.0f, 20.0f)]
    public float FlySprintMultiplier { get; set; } = 4.0f;

    /// <summary>How quickly they get up to speed and stop on the ground, units a second every second.</summary>
    [Inspect(0.0f, 200.0f)]
    public float GroundAcceleration { get; set; } = 60.0f;

    /// <summary>And in the air, where there is nothing to push against.</summary>
    [Inspect(0.0f, 200.0f)]
    public float AirAcceleration { get; set; } = 14.0f;

    [Inspect(0.0f, 100.0f)]
    public float Gravity { get; set; } = 32.0f;

    /// <summary>How fast they leave the ground on a jump. 9 is a jump of about a block and a quarter with the gravity above.</summary>
    [Inspect(0.0f, 30.0f)]
    public float JumpSpeed { get; set; } = 9.0f;

    /// <summary>The fastest they fall.</summary>
    public float TerminalVelocity { get; set; } = 78.0f;

    /// <summary>How tall a step they walk up without jumping. A slab, not a block.</summary>
    [Inspect(0.0f, 1.5f)]
    public float StepHeight { get; set; } = 0.6f;

    /// <summary>Whether walking into a block a block high jumps up it by itself. The thing every mobile block game has.</summary>
    public bool AutoJump { get; set; }

    /// <summary>How much of everything is left swimming, the speed and the gravity both.</summary>
    public float SwimDrag { get; set; } = 0.5f;

    /* How it went */

    /// <summary>Whether they are stood on something.</summary>
    public bool IsGrounded { get; private set; }

    /// <summary>Whether their middle is in water.</summary>
    public bool IsSwimming { get; private set; }

    /// <summary>Whether their eyes are, for the camera to go murky.</summary>
    public bool IsUnderwater { get; private set; }

    /// <summary>Whether they ran into a wall on the last step, and into the ceiling.</summary>
    public bool HitWall { get; private set; }

    public bool HitCeiling { get; private set; }

    /// <summary>How fast they were falling when they last landed, for fall damage. Read it and set it back to 0.</summary>
    public float LandingSpeed { get; set; }

    /// <summary>Called when they land, with how fast they were falling.</summary>
    public Action<float>? Landed { get; set; }

    public void Jump()
    {
        if (IsGrounded || IsSwimming) Velocity = Velocity with { Y = IsSwimming ? JumpSpeed * 0.5f : JumpSpeed };
    }

    /// <summary>Puts them somewhere, standing still.</summary>
    public void Teleport(Vector3 feet)
    {
        Position = feet;
        Velocity = Vector3.Zero;
    }

    public override void UpdatePhysics(float dt)
    {
        base.UpdatePhysics(dt);
        if (dt <= 0.0f) return;

        IsSwimming = !Flying && World.IsLiquid(Position + new Vector3(0.0f, Size.Y * 0.4f, 0.0f));
        IsUnderwater = World.IsLiquid(Eye);

        Vector3 velocity = Flying ? Fly(dt) : Walk(dt);

        Vector3 moved = NoClip ? velocity * dt : Slide(velocity * dt);
        Vector3 wanted = velocity * dt;

        // Whatever was stopped along an axis isn't going that way any more
        if (MathF.Abs(moved.X - wanted.X) > SKIN) velocity.X = 0.0f;
        if (MathF.Abs(moved.Z - wanted.Z) > SKIN) velocity.Z = 0.0f;

        bool wasGrounded = IsGrounded;
        bool falling = velocity.Y < 0.0f;
        HitCeiling = wanted.Y > 0.0f && moved.Y < wanted.Y - SKIN;
        IsGrounded = !Flying && !NoClip && falling && moved.Y > wanted.Y + SKIN || (!Flying && !NoClip && velocity.Y <= 0.0f && OnGround());

        if (IsGrounded && !wasGrounded && falling)
        {
            LandingSpeed = -velocity.Y;
            Landed?.Invoke(-velocity.Y);
        }

        if (MathF.Abs(moved.Y - wanted.Y) > SKIN) velocity.Y = 0.0f;

        Velocity = velocity;
    }

    /// <summary>Helper method for how fast they go on foot (and swimming) this step.</summary>
    private Vector3 Walk(float dt)
    {
        Vector3 velocity = Velocity;
        Vector3 wish = new(Move.X, 0.0f, Move.Z);
        if (wish.LengthSquared() > 1.0f) wish = Vector3.Normalize(wish);

        float speed = Crouching ? CrouchSpeed : Sprinting ? SprintSpeed : WalkSpeed;
        if (IsSwimming) speed *= SwimDrag + 0.25f;

        // Up to speed over a moment, a key is a push
        float acceleration = IsGrounded ? GroundAcceleration : AirAcceleration;
        if (IsSwimming) acceleration = GroundAcceleration * 0.5f;
        Vector2 flat = MoveTowards(new Vector2(velocity.X, velocity.Z), new Vector2(wish.X, wish.Z) * speed, acceleration * dt);
        velocity.X = flat.X;
        velocity.Z = flat.Y;

        if (IsSwimming)
        {
            // Sinking slowly, up with jump and down with descend, and out of the top with a hop so a ledge can be climbed
            velocity.Y -= Gravity * SwimDrag * 0.25f * dt;
            if (Jumping) velocity.Y = MathF.Min(velocity.Y + Gravity * dt, 4.0f);
            if (Descending) velocity.Y = MathF.Max(velocity.Y - Gravity * 0.5f * dt, -4.0f);
            velocity.Y = Math.Clamp(velocity.Y, -3.0f, 4.0f);
            if (Jumping && HitWall) velocity.Y = MathF.Max(velocity.Y, JumpSpeed * 0.6f);
        }
        else
        {
            velocity.Y = MathF.Max(velocity.Y - Gravity * dt, -TerminalVelocity);
            if (Jumping && IsGrounded) velocity.Y = JumpSpeed;
            else if (AutoJump && IsGrounded && HitWall && wish.LengthSquared() > 0.01f && CanAutoJump(wish)) velocity.Y = JumpSpeed;
        }

        return velocity;
    }

    /// <summary>Helper method for how fast they go flying this step, no gravity and stopping quickly when let go.</summary>
    private Vector3 Fly(float dt)
    {
        Vector3 wish = new(Move.X, 0.0f, Move.Z);
        if (wish.LengthSquared() > 1.0f) wish = Vector3.Normalize(wish);
        wish.Y = (Jumping ? 1.0f : 0.0f) - (Descending ? 1.0f : 0.0f);

        float speed = FlySpeed * (Sprinting ? FlySprintMultiplier : 1.0f);
        return MoveTowards(Velocity, wish * speed, speed * 6.0f * dt);
    }

    /// <summary>
    /// Helper method to move the box as far along a step as it goes, up and down first and then the two flat ways,
    /// stepping up onto a slab when a wall stops it and there is room above. Returns how far it went.
    /// </summary>
    private Vector3 Slide(Vector3 step)
    {
        Box3 box = Bounds;

        // Everything that could be in the way anywhere along the step, and room for a step up
        solids.Clear();
        Box3 reach = box.Swept(step).Inflate(0.01f);
        reach = new Box3(reach.Min, reach.Max + new Vector3(0.0f, StepHeight, 0.0f));
        World.CollectSolids(reach, solids);

        Vector3 moved = SlideThrough(box, step, out _);

        // Crouched on the ground, an edge is a wall. Each flat way is undone if it would leave nothing under the feet
        if (Crouching && IsGrounded && !Flying)
        {
            moved = KeepOnEdge(box, moved);
        }

        // Stopped by something low enough to step onto, and standing, so try going over it and take whichever went further
        bool blocked = MathF.Abs(moved.X - step.X) > SKIN || MathF.Abs(moved.Z - step.Z) > SKIN;
        HitWall = blocked;

        if (blocked && StepHeight > 0.0f && (IsGrounded || step.Y <= 0.0f && OnGround()) && !IsSwimming)
        {
            Vector3 up = new(step.X, StepHeight, step.Z);
            Vector3 over = SlideThrough(box, up, out Box3 raised);

            // And back down onto whatever it stepped onto
            float down = -StepHeight;
            foreach (Box3 solid in solids) down = solid.ClipY(raised, down);
            over.Y += down;

            float flatBefore = moved.X * moved.X + moved.Z * moved.Z;
            float flatAfter = over.X * over.X + over.Z * over.Z;
            if (flatAfter > flatBefore + SKIN)
            {
                moved = over;
                HitWall = MathF.Abs(over.X - step.X) > SKIN || MathF.Abs(over.Z - step.Z) > SKIN;
            }
        }

        Position += moved;
        return moved;
    }

    /// <summary>Helper method to clip a step against what is in the way, Y, then X, then Z.</summary>
    private Vector3 SlideThrough(Box3 box, Vector3 step, out Box3 after)
    {
        float dy = step.Y;
        foreach (Box3 solid in solids) dy = solid.ClipY(box, dy);
        box = box.Offset(new Vector3(0.0f, dy, 0.0f));

        float dx = step.X;
        foreach (Box3 solid in solids) dx = solid.ClipX(box, dx);
        box = box.Offset(new Vector3(dx, 0.0f, 0.0f));

        float dz = step.Z;
        foreach (Box3 solid in solids) dz = solid.ClipZ(box, dz);
        box = box.Offset(new Vector3(0.0f, 0.0f, dz));

        after = box;
        return new Vector3(dx, dy, dz);
    }

    /// <summary>Helper method to undo whatever flat way would take a crouched somebody off the edge they are stood on.</summary>
    private Vector3 KeepOnEdge(Box3 box, Vector3 moved)
    {
        if (moved.X != 0.0f && !HasGroundUnder(box.Offset(new Vector3(moved.X, 0.0f, 0.0f)))) moved.X = 0.0f;
        if (moved.Z != 0.0f && !HasGroundUnder(box.Offset(new Vector3(moved.X, 0.0f, moved.Z)))) moved.Z = 0.0f;
        return moved;
    }

    private bool HasGroundUnder(Box3 box)
    {
        Box3 below = new(box.Min - new Vector3(0.0f, 0.6f, 0.0f), new Vector3(box.Max.X, box.Min.Y - SKIN, box.Max.Z));
        foreach (Box3 solid in solids)
        {
            if (solid.Intersects(below)) return true;
        }

        return World.Overlaps(below);
    }

    /// <summary>Helper method for whether there is something right under the feet.</summary>
    private bool OnGround()
    {
        Box3 box = Bounds;
        Box3 below = new(new Vector3(box.Min.X, box.Min.Y - GROUND_PROBE, box.Min.Z), new Vector3(box.Max.X, box.Min.Y - SKIN * 0.5f, box.Max.Z));
        return World.Overlaps(below);
    }

    /// <summary>Helper method for whether what is in the way is one block high with room to stand on top of it.</summary>
    private bool CanAutoJump(Vector3 wish)
    {
        Vector3 ahead = Vector3.Normalize(wish) * 0.3f;
        Box3 box = Bounds;

        // Something there at the feet, and nothing a block up
        Box3 feet = new(box.Min + ahead + new Vector3(0.0f, 0.1f, 0.0f), new Vector3(box.Max.X, box.Min.Y + 0.9f, box.Max.Z) + ahead);
        Box3 head = box.Offset(ahead + new Vector3(0.0f, 1.05f, 0.0f));
        return World.Overlaps(feet) && !World.Overlaps(head);
    }

    private static Vector2 MoveTowards(Vector2 from, Vector2 to, float by)
    {
        Vector2 gap = to - from;
        float length = gap.Length();
        return length <= by || length < 1e-6f ? to : from + gap / length * by;
    }

    private static Vector3 MoveTowards(Vector3 from, Vector3 to, float by)
    {
        Vector3 gap = to - from;
        float length = gap.Length();
        return length <= by || length < 1e-6f ? to : from + gap / length * by;
    }
}
