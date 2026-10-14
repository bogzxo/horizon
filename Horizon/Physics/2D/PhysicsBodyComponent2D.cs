using System.Numerics;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Physics.Fixtures;

namespace Horizon.Physics;

public class PhysicsBodyComponent2D : GameComponent
{
    public PhysicsBodySimulationType SimulationType { get; init; }
    public List<IPhysicsFixture> KinematicFixtures { get; init; } = [];
    public List<IPhysicsFixture> DynamicFixtures { get; init; } = [];

    /// <summary>
    /// What particles run into of this body. Left empty that is its dynamic fixtures, the same things it stands on
    /// the map with. A body that has something better to offer them (the outline of its sprite, see
    /// <see cref="OutlinePhysicsFixture"/>) puts it here, and they collide with that instead.
    /// </summary>
    public List<IPhysicsFixture> ParticleFixtures { get; init; } = [];

    // TODO this should not be publicly mutable, the simulation loop of the world should be the only one moving it.
    // Everybody and their dog teleports bodies through here meanwhile
    public Vector2 Position { get; set; }

    public Vector2 Velocity { get; internal set; }
    public Vector2 Force { get; internal set; }
    public float Mass { get; set; } = 1.0f;
    public float Restitution { get; set; } = 0.3f;
    public float LinearDrag { get; set; } = 0.0f;

    /// <summary>
    /// Whether a move that would end inside of the map is taken as far as it goes, up against whatever is in the
    /// way, rather than not taken at all. Off, which is what it is unless somebody says so, a body that would have
    /// hit the floor this step stays where it was, as much as a step of falling short of it, stops dead and then
    /// falls the rest of the way from standing. Nobody sees that of a crate. Of somebody landing from a jump it
    /// is a hitch three pixels off the ground, every single landing, so a <see cref="CharacterController2D"/>
    /// has it on.
    /// </summary>
    public bool StopsFlush { get; set; }

    /// <summary>
    /// Bodies with the same group pass straight through each other, and don't count as touching either.
    /// Everything else (the map, bodies of another group or of none) they run into as usual.
    /// Zero, which is what a body has until it is told otherwise, is no group at all.
    /// </summary>
    public int CollisionGroup { get; set; }

    private TransformComponent2D? parentTransform;

    public PhysicsBodyComponent2D(Vector2 initialPosition)
    {
        this.Position = initialPosition;
    }
    public PhysicsBodyComponent2D() : this(Vector2.Zero) { }

    public override void Initialize()
    {
        parentTransform = Parent.GetComponent<TransformComponent2D>();
    }

    public RectanglePhysicsFixture CreateRectangularFixture(Vector2 position, Vector2 size, bool kinematic=false,string tag="")
    {
        var rf = new RectanglePhysicsFixture( position, size, tag);
        if (kinematic) this.KinematicFixtures.Add(rf);
        else this.DynamicFixtures.Add(rf);
        return rf;
    }

    /// <summary>
    /// Gives the body an outline for particles to collide with, empty until it is given one (<see cref="OutlinePhysicsFixture.Set"/>).
    /// </summary>
    public OutlinePhysicsFixture CreateOutlineFixture(string tag = "")
    {
        var outline = new OutlinePhysicsFixture(tag);
        this.ParticleFixtures.Add(outline);
        return outline;
    }

    public CirclePhysicsFixture CreateCircleFixture(Vector2 position, float radius, bool kinematic = false, string tag="")
    {
        var cf = new CirclePhysicsFixture( radius, position, tag);
        if (kinematic) this.KinematicFixtures.Add(cf);
        else this.DynamicFixtures.Add(cf);
        return cf;
    }

    public float InverseMass => Mass <= 0.0f ? 0.0f : 1.0f / Mass;
    public Vector2 Acceleration => InverseMass <= 0.0f ? Vector2.Zero : Force * InverseMass;

    public void ApplyForce(Vector2 force)
    {
        if (SimulationType != PhysicsBodySimulationType.Dynamic)
            return;

        Force += force;
    }

    public void ApplyImpulse(Vector2 impulse)
    {
        if (SimulationType != PhysicsBodySimulationType.Dynamic || InverseMass <= 0.0f)
            return;

        Velocity += impulse * InverseMass;
    }

    public void SetVelocity(Vector2 velocity)
    {
        Velocity = velocity;
    }

    public void ResetForces()
    {
        Force = Vector2.Zero;
    }

    public override void Render(float dt)
    {
        // physics bodies have nothing to draw, the transform is brought along in UpdateState
    }

    public override void UpdateState(float dt)
    {
        if (!Enabled || parentTransform is null)
            return;

        parentTransform.Position = Position;
    }
}