using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Physics.Fixtures;

namespace Horizon.Physics;

public class PhysicsBodyComponent2D : IGameComponent
{
    public PhysicsBodySimulationType SimulationType { get; init; }
    public List<IPhysicsFixture> KinematicFixtures { get; init; } = [];
    public List<IPhysicsFixture> DynamicFixtures { get; init; } = [];

    // TODO: this should not be publicly mutable, the physics world simulation loop should change it
    public Vector2 Position { get; set; }

    public Vector2 Velocity { get; internal set; }
    public Vector2 Force { get; internal set; }
    public float Mass { get; set; } = 1.0f;
    public float Restitution { get; set; } = 0.3f;
    public float LinearDrag { get; set; } = 0.0f;

    public bool Enabled { get; set; }
    public string Name { get; set; } = "Physics Body";
    public Entity Parent { get; set; } = null!;

    private TransformComponent2D? parentTransform;

    public PhysicsBodyComponent2D(Vector2 initialPosition)
    {
        this.Position = initialPosition;
    }
    public PhysicsBodyComponent2D() : this(Vector2.Zero) { }

    public void Initialize()
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

    public void Render(float dt, object? obj = null)
    {
        // physics bodies do not render directly; transform is updated in state.
    }

    public void UpdatePhysics(float dt)
    {
        
    }

    public void UpdateState(float dt)
    {
        if (parentTransform is null)
            return;

        parentTransform.Position = Position;
    }
}