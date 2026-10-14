using System.Numerics;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Physics.Fixtures;

namespace Horizon.Physics;

/// <summary>
/// Somebody a player steers about a <see cref="PhysicsWorld"/>, seen from the side (they walk, jump and fall) or
/// from above (they go where they are pointed). A body in the world like any other, which is the point of it. The
/// world moves it, stops it at the walls and lets particles land on it, and all this does is push.
/// <code>
/// hero = AddComponent(new CharacterController2D(physics, spawn, new Vector2(10, 10)));
///
/// // every update, what the player wants
/// hero.Move = new Vector2(keyboard.Axis(Key.A, Key.D), 0.0f);
/// if (keyboard.WasPressed(Key.Space)) hero.Jump();
/// hero.HoldingJump = keyboard.IsDown(Key.Space);
///
/// sprite.Transform.Position = hero.Position;
/// </code>
/// <para>
/// What makes somebody feel like they weigh something is that a key is not a speed, it is a push. Holding one
/// brings them up to <see cref="MaxSpeed"/> over a moment (<see cref="Acceleration"/>), letting go has them slow
/// to a stop (<see cref="Deceleration"/>), and turning round is a skid (<see cref="TurnBoost"/>). Set a speed
/// outright from the keys and they start and stop on the very tick, which is how a cursor moves and nothing that
/// has feet. In the air there is less to push against (<see cref="AirControl"/>).
/// </para>
/// <para>
/// The jump is the one players have been taught to expect. It is still there for a moment after the ledge is
/// behind them (<see cref="CoyoteTime"/>), a press a moment before landing counts when they land
/// (<see cref="JumpBuffer"/>), letting go early is a short hop (<see cref="ShortHopGravity"/>) and the way down
/// is quicker than the way up (<see cref="FallGravity"/>). None of that is physics and all of it is why a jump
/// feels right, the real thing is floaty and unforgiving and nobody wants it.
/// </para>
/// <para>
/// Add it to the scene the world is in. It pushes in the physics step, the world's gravity is the gravity, and
/// everything it does to the body it does with forces and impulses, so a heavier body is as quick as a light one
/// here and still harder for anything else to shove. Simulation thread, all of it.
/// </para>
/// </summary>
public sealed class CharacterController2D : GameComponent
{
    // How far under the feet the ground is looked for, and how far in from the sides, so a wall they are stood
    // against is not taken for a floor
    private const float FEET_REACH = 1.0f;
    private const float FEET_INSET = 1.0f;

    private readonly IPhysicsFixture? feet;

    // How long ago they were on the ground, how long a press of jump is still good for, and how fast they were
    // falling when they were last in the air
    private float sinceGrounded = float.MaxValue;
    private float jumpWanted;
    private float fallSpeed;

    /// <summary>The world they are in.</summary>
    public PhysicsWorld World { get; }

    /// <summary>The body the world moves. Its mass, its group and what else it is made of are there to be set.</summary>
    public PhysicsBodyComponent2D Body { get; }

    /// <summary>The box of them that bumps into things.</summary>
    public RectanglePhysicsFixture Box { get; }

    /// <summary>Whether they are seen from above. No jumping and no ground then, and <see cref="Move"/> is both ways.</summary>
    public bool TopDown { get; }

    /* How they move */

    /// <summary>How fast they go flat out, in units a second.</summary>
    [Inspect(0.0f, 400.0f)]
    public float MaxSpeed { get; set; } = 90.0f;

    /// <summary>How quickly they get up to speed while a way is held, in units a second every second.</summary>
    [Inspect(0.0f, 4000.0f)]
    public float Acceleration { get; set; } = 620.0f;

    /// <summary>How quickly they come to a stop once nothing is held.</summary>
    [Inspect(0.0f, 4000.0f)]
    public float Deceleration { get; set; } = 760.0f;

    /// <summary>How much harder they push while going one way and asked for the other. 1 is no harder, and turning round is as slow as starting twice.</summary>
    [Inspect(1.0f, 4.0f)]
    public float TurnBoost { get; set; } = 1.8f;

    /// <summary>How much of all that they have while off the ground, from 0 (none, a jump goes where it was pointed) to 1 (as on the ground).</summary>
    [Inspect(0.0f, 1.0f)]
    public float AirControl { get; set; } = 0.6f;

    /* How they jump */

    /// <summary>How fast a jump leaves the ground. How high that gets them is this squared over twice the gravity.</summary>
    [Inspect(0.0f, 600.0f)]
    public float JumpSpeed { get; set; } = 215.0f;

    /// <summary>How long (in seconds) after walking off an edge a jump still counts as one off the ground.</summary>
    [Inspect(0.0f, 0.3f)]
    public float CoyoteTime { get; set; } = 0.09f;

    /// <summary>How long (in seconds) before landing a press of jump is remembered for.</summary>
    [Inspect(0.0f, 0.3f)]
    public float JumpBuffer { get; set; } = 0.1f;

    /// <summary>How many times the gravity they fall with, 1 for the same on the way down as on the way up.</summary>
    [Inspect(1.0f, 4.0f)]
    public float FallGravity { get; set; } = 1.45f;

    /// <summary>How many times the gravity they rise against once jump is let go of, which is what makes a tap a hop.</summary>
    [Inspect(1.0f, 6.0f)]
    public float ShortHopGravity { get; set; } = 2.6f;

    /// <summary>The fastest they fall, 0 for no limit.</summary>
    [Inspect(0.0f, 1200.0f)]
    public float MaxFallSpeed { get; set; } = 420.0f;

    /* What the player wants, set every update */

    /// <summary>
    /// Which way they are being steered, each way from -1 to 1 (a stick held halfway is half speed). Seen from the
    /// side only X counts.
    /// </summary>
    public Vector2 Move { get; set; }

    /// <summary>Whether jump is being held, for telling a tap from a press. Left on, every jump is a full one.</summary>
    public bool HoldingJump { get; set; } = true;

    /* How they are doing */

    /// <summary>Where they are, their feet seen from the side and their middle seen from above.</summary>
    public Vector2 Position => Body.Position;

    public Vector2 Velocity => Body.Velocity;

    /// <summary>Whether there is ground under them. Always, seen from above.</summary>
    public bool Grounded { get; private set; }

    /// <summary>Which way they were last steered, for the art to face. Starts off to the right.</summary>
    public Vector2 Facing { get; private set; } = Vector2.UnitX;

    /// <summary>Called as they leave the ground of their own accord. Simulation thread.</summary>
    public event Action? Jumped;

    /// <summary>Called as they land, with how fast they came down. Simulation thread.</summary>
    public event Action<float>? Landed;

    /// <param name="world">The world they are a body of.</param>
    /// <param name="position">Where they start.</param>
    /// <param name="size">How big the box of them that bumps into things is.</param>
    /// <param name="topDown">Whether they are seen from above, see <see cref="TopDown"/>.</param>
    public CharacterController2D(PhysicsWorld world, Vector2 position, Vector2 size, bool topDown = false)
    {
        World = world;
        TopDown = topDown;

        Body = world.CreateBody(PhysicsBodySimulationType.Dynamic, position);

        // Nobody bounces off a wall they walked into, and a landing ends on the floor and not a step short of it
        Body.Restitution = 0.0f;
        Body.StopsFlush = true;

        // Around their middle from above, standing on their position from the side
        Vector2 corner = topDown ? -size / 2.0f : new Vector2(-size.X / 2.0f, 0.0f);
        Box = Body.CreateRectangularFixture(corner, size, tag: "character");

        if (!topDown)
        {
            feet = Body.CreateRectangularFixture(
                new Vector2(corner.X + FEET_INSET, -FEET_REACH),
                new Vector2(MathF.Max(size.X - FEET_INSET * 2.0f, 0.5f), FEET_REACH * 1.5f),
                kinematic: true,
                tag: "feet");
        }

        Grounded = topDown;
    }

    /// <summary>
    /// Jumps, or will the moment there is ground to jump off if that is within <see cref="JumpBuffer"/>. Call it
    /// when the button goes down, not while it is held.
    /// </summary>
    public void Jump() => jumpWanted = MathF.Max(JumpBuffer, 0.0001f);

    /// <summary>Puts them somewhere else, standing still.</summary>
    public void Teleport(Vector2 position)
    {
        Body.Position = position;
        Body.SetVelocity(Vector2.Zero);
        Body.ResetForces();
    }

    /// <summary>Knocks them a way, a speed added to the one they have. A hit, an explosion, a spring.</summary>
    public void Push(Vector2 velocity) => Body.ApplyImpulse(velocity * Body.Mass);

    /// <summary>Takes them out of the world, for when they are done with. The body is nobody's after this.</summary>
    public void Leave() => World.DynamicBodies.Remove(Body);

    public override void UpdatePhysics(float dt)
    {
        if (!Enabled || dt <= 0.0f)
            return;

        if (TopDown) SteerFromAbove(dt);
        else SteerFromTheSide(dt);
    }

    /// <summary>
    /// Helper method to bring the body's speed nearer to the one being asked for, by as much as a step allows, both ways at once.
    /// </summary>
    private void SteerFromAbove(float dt)
    {
        Vector2 velocity = Body.Velocity;

        // A stick can only ask for all of it, in whatever direction. Two keys at once are not half again as fast
        Vector2 move = Move;
        if (move.LengthSquared() > 1.0f) move = Vector2.Normalize(move);

        bool steering = move.LengthSquared() > 0.0001f;
        if (steering) Facing = Vector2.Normalize(move);

        Vector2 wanted = move * MaxSpeed;
        float rate = !steering ? Deceleration : Vector2.Dot(velocity, wanted) < 0.0f ? Acceleration * TurnBoost : Acceleration;

        Vector2 change = wanted - velocity;
        float most = rate * dt, asked = change.Length();
        if (asked > most) change *= most / asked;

        // As a force, which is a mass times how much faster in how long
        Body.ApplyForce(change * (Body.Mass / dt));
        Grounded = true;
    }

    /// <summary>
    /// Helper method for the same seen from the side, where only left and right are steered, and for the jump and the fall.
    /// </summary>
    private void SteerFromTheSide(float dt)
    {
        Vector2 velocity = Body.Velocity;
        float mass = Body.Mass;

        // On the ground if there is something under the feet and they aren't on their way up through it. The feet
        // are still near the floor for a tick or two after a jump has left it
        bool wasGrounded = Grounded;
        Grounded = feet is { IsTouching: true } && velocity.Y <= 0.01f;

        if (Grounded)
        {
            sinceGrounded = 0.0f;
            if (!wasGrounded) Landed?.Invoke(fallSpeed);
            fallSpeed = 0.0f;
        }
        else
        {
            if (sinceGrounded < float.MaxValue) sinceGrounded += dt;
            fallSpeed = MathF.Max(0.0f, -velocity.Y);
        }

        Vector2 force = Vector2.Zero;

        /* Left and right */

        float move = Math.Clamp(Move.X, -1.0f, 1.0f);
        bool steering = MathF.Abs(move) > 0.01f;
        if (steering) Facing = new Vector2(MathF.Sign(move), 0.0f);

        float wanted = move * MaxSpeed;
        float rate = !steering ? Deceleration : velocity.X * wanted < 0.0f ? Acceleration * TurnBoost : Acceleration;
        if (!Grounded) rate *= AirControl;

        force.X = mass * Math.Clamp(wanted - velocity.X, -rate * dt, rate * dt) / dt;

        /* The jump */

        if (jumpWanted > 0.0f)
        {
            if (sinceGrounded <= CoyoteTime)
            {
                // Up at the speed of a jump whatever they were doing, a jump off the last of a ledge is a whole one
                Body.ApplyImpulse(new Vector2(0.0f, (JumpSpeed - velocity.Y) * mass));
                velocity.Y = JumpSpeed;

                jumpWanted = 0.0f;
                sinceGrounded = float.MaxValue;
                Grounded = false;
                Jumped?.Invoke();
            }
            else
            {
                jumpWanted -= dt;
            }
        }

        /* The way down, and the way up with the button let go */

        Vector2 gravity = World.Gravity;
        if (!Grounded && gravity != Vector2.Zero)
        {
            // Along the gravity is falling, whichever way the world has it
            float falling = Vector2.Dot(velocity, Vector2.Normalize(gravity));
            float times = falling > 0.0f ? FallGravity : !HoldingJump ? ShortHopGravity : 1.0f;
            force += gravity * (mass * (MathF.Max(times, 1.0f) - 1.0f));

            // No faster than this, by pushing back with whatever it takes
            if (MaxFallSpeed > 0.0f && falling > MaxFallSpeed)
                force -= Vector2.Normalize(gravity) * (mass * (falling - MaxFallSpeed) / dt);
        }

        Body.ApplyForce(force);
    }
}
