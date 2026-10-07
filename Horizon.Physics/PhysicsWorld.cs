using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Physics.Debug;
using Horizon.Physics.Fixtures;
using Horizon.Physics.Simulation;
using Horizon.Rendering;

namespace Horizon.Physics;

/// <summary>
/// What a force applied to an area of the world acts on.
/// </summary>
[Flags]
public enum PhysicsRadialTargets
{
    Bodies = 1 << 0,        // the dynamic bodies
    Particles = 1 << 1,     // the particles of every particle group
    All = Bodies | Particles
}

public class PhysicsWorld : GameComponent
{
    public bool RenderDebug { get; set; } = false;
    public List<PhysicsBodyComponent2D> StaticBodies { get; init; } = [];
    public List<PhysicsBodyComponent2D> DynamicBodies { get; init; } = new();

    public Vector2 Gravity { get; set; }

    private readonly PhysicsWorldDebugRenderer debugRenderer = new();

    // Particles are simulated by the world too, but as something a lot lighter than a body
    private readonly List<PhysicsParticleGroup> particleGroups = [];

    // The particles simulated on the GPU can't be stepped from here, they are told what there is to run into instead
    private readonly List<PhysicsParticleFeed> particleFeeds = [];
    private readonly PhysicsStaticGrid staticGrid = new();
    private PhysicsShape[] dynamicShapes = new PhysicsShape[16];

    // Forces act over time, so they wait here to be applied during the next step
    private readonly List<(Vector2 Centre, float Radius, float Force, PhysicsRadialTargets Targets, PhysicsBodyComponent2D? Except)> radialForces = [];

    public PhysicsBodyComponent2D CreateBody(PhysicsBodySimulationType simulationType, Vector2 initialPosition)
    {
        return AddBody(new PhysicsBodyComponent2D() { 
            SimulationType = simulationType,
            Position = initialPosition,
        });
    }
    public PhysicsBodyComponent2D CreateBody(PhysicsBodySimulationType simulationType)
        => CreateBody(simulationType, Vector2.Zero);

    public PhysicsBodyComponent2D AddBody(in PhysicsBodyComponent2D body)
    {
        switch (body.SimulationType)
        {
            case PhysicsBodySimulationType.Static:
                this.StaticBodies.Add(body);
                break;
            case PhysicsBodySimulationType.Dynamic:
                this.DynamicBodies.Add(body);
                break;
        }

        return body;
    }

    /// <summary>
    /// Creates a set of particles which are simulated as small dynamic bodies, see <see cref="PhysicsParticleGroup"/>.
    /// </summary>
    /// <param name="capacity">How many particles there can be at once.</param>
    public PhysicsParticleGroup CreateParticleGroup(int capacity)
    {
        var group = new PhysicsParticleGroup(capacity);
        particleGroups.Add(group);
        return group;
    }

    /// <summary>
    /// Creates a set of particles which also collide with one another, so they pile up and run off to fill
    /// whatever they are poured into, see <see cref="PhysicsFluidParticleGroup"/>.
    /// </summary>
    /// <param name="capacity">How many particles there can be at once.</param>
    public PhysicsFluidParticleGroup CreateFluidParticleGroup(int capacity)
    {
        var group = new PhysicsFluidParticleGroup(capacity);
        particleGroups.Add(group);
        return group;
    }

    public bool RemoveParticleGroup(PhysicsParticleGroup group) => particleGroups.Remove(group);

    /// <summary>
    /// Creates a feed, which is kept up to date with what particles simulated outside of the world can run into.
    /// </summary>
    internal PhysicsParticleFeed CreateParticleFeed()
    {
        var feed = new PhysicsParticleFeed();
        particleFeeds.Add(feed);
        return feed;
    }

    internal bool RemoveParticleFeed(PhysicsParticleFeed feed) => particleFeeds.Remove(feed);

    /// <summary>
    /// Launches everything within a radius away from a point all at once (an explosion, a shockwave, a foot coming down).
    /// The push is at its hardest in the centre and fades to nothing at the edge, how fast something ends up going
    /// depends on its mass.
    /// </summary>
    /// <param name="except">A body to leave alone, usually the one causing all this.</param>
    public void ApplyRadialImpulse(Vector2 centre, float radius, float impulse,
        PhysicsRadialTargets targets = PhysicsRadialTargets.All, PhysicsBodyComponent2D? except = null)
    {
        if (radius <= 0.0f) return;

        if (targets.HasFlag(PhysicsRadialTargets.Bodies))
        {
            foreach (var body in DynamicBodies)
            {
                if (body != except && TryGetRadialPush(body.Position, centre, radius, out Vector2 push))
                {
                    body.ApplyImpulse(push * impulse);
                }
            }
        }

        if (targets.HasFlag(PhysicsRadialTargets.Particles))
        {
            foreach (var group in particleGroups)
            {
                group.ApplyRadial(centre, radius, impulse);
            }

            foreach (var feed in particleFeeds)
            {
                feed.AddImpulse(centre, radius, impulse);
            }
        }
    }

    /// <summary>
    /// Pushes everything within a radius away from a point for the length of the next step (wind, a fan, a magnet when negative).
    /// Has to be called again for every step the force should last.
    /// </summary>
    /// <param name="except">A body to leave alone, usually the one causing all this.</param>
    public void ApplyRadialForce(Vector2 centre, float radius, float force,
        PhysicsRadialTargets targets = PhysicsRadialTargets.All, PhysicsBodyComponent2D? except = null)
    {
        if (radius <= 0.0f) return;

        radialForces.Add((centre, radius, force, targets, except));
    }

    /// <summary>
    /// Helper method to work out the direction and falloff of a radial push at a position, false if it is out of reach.
    /// </summary>
    private static bool TryGetRadialPush(Vector2 position, Vector2 centre, float radius, out Vector2 push)
    {
        Vector2 away = position - centre;
        float distance = away.Length();

        if (distance >= radius)
        {
            push = Vector2.Zero;
            return false;
        }

        // Anything sat exactly on the centre goes straight up
        Vector2 direction = distance > 0.0001f ? away / distance : Vector2.UnitY;
        push = direction * (1.0f - distance / radius);
        return true;
    }

    public override void Initialize()
    {
        debugRenderer.Initialize();
    }
    public override void UpdatePhysics(float dt)
    {
        if (!Enabled) return;

        var dynamicBodies = CollectionsMarshal.AsSpan<PhysicsBodyComponent2D>(this.DynamicBodies);

        // The map is sorted into a grid, so nothing has to be tested against every tile of it
        staticGrid.Refresh(StaticBodies);

        // 0. Turn the forces that were asked for since the last step into pushes
        foreach (var (centre, radius, force, targets, except) in radialForces)
        {
            if (targets.HasFlag(PhysicsRadialTargets.Bodies))
            {
                foreach (var body in dynamicBodies)
                {
                    if (body != except && TryGetRadialPush(body.Position, centre, radius, out Vector2 push))
                    {
                        body.ApplyForce(push * force);
                    }
                }
            }

            // Particles have no force of their own to add to, for them a force over one step is an impulse
            if (targets.HasFlag(PhysicsRadialTargets.Particles))
            {
                foreach (var group in particleGroups)
                {
                    group.ApplyRadial(centre, radius, force * dt);
                }

                foreach (var feed in particleFeeds)
                {
                    feed.AddImpulse(centre, radius, force * dt);
                }
            }
        }
        radialForces.Clear();

        // 1. Reset contact state across dynamic and kinematic fixtures
        foreach (var body in dynamicBodies)
        {
            foreach (var fixture in body.DynamicFixtures)
            {
                fixture.IsTouching = false;
                fixture.ActiveContacts.Clear();
            }
            foreach (var fixture in body.KinematicFixtures)
            {
                fixture.IsTouching = false;
                fixture.ActiveContacts.Clear();
            }
        }

        foreach (var body in dynamicBodies)
        {
            // 2. Correct Gravity Integration: Apply acceleration directly (independent of Mass)
            body.Velocity += (Gravity + (body.Force * body.InverseMass)) * dt;

            if (body.LinearDrag > 0.0f)
            {
                float dragFactor = 1.0f - body.LinearDrag * dt;
                body.Velocity *= MathF.Max(0.0f, dragFactor);
            }

            body.Force = Vector2.Zero;

            // 3. Resolve X Axis
            var currentPosition = body.Position;
            var nextPositionX = new Vector2(currentPosition.X + body.Velocity.X * dt, currentPosition.Y);

            foreach (var fixture in body.DynamicFixtures)
            {
                // Test against static bodies, the grid hands us only the ones nearby...
                foreach (int candidate in FindStaticCandidates(fixture, currentPosition, nextPositionX))
                {
                    var otherFixture = staticGrid.GetFixture(candidate);
                    if (fixture.TestIntersection(otherFixture, nextPositionX, staticGrid.GetBodyPosition(candidate)))
                    {
                        nextPositionX.X = currentPosition.X;
                        body.Velocity = new Vector2(-body.Velocity.X * body.Restitution, body.Velocity.Y);

                        fixture.IsTouching = true;
                        otherFixture.IsTouching = true;
                        fixture.ActiveContacts.Add(otherFixture);
                    }
                }

                // Test against dynamic bodies...
                foreach (var other in dynamicBodies)
                {
                    // Yeah lets not collide with ourselves, or with our own kind
                    if (other == body || SameGroup(body, other)) continue;

                    foreach (var otherFixture in other.DynamicFixtures)
                    {
                        if (fixture.TestIntersection(otherFixture, nextPositionX, other.Position))
                        {
                            nextPositionX.X = currentPosition.X;
                            body.Velocity = new Vector2(-body.Velocity.X * body.Restitution, body.Velocity.Y);

                            other.Velocity = -body.Velocity;
                            fixture.IsTouching = true;
                            otherFixture.IsTouching = true;
                            fixture.ActiveContacts.Add(otherFixture);
                        }
                    }
                }
            }

            // 4. Resolve Y Axis using updated X position
            var nextPositionY = new Vector2(nextPositionX.X, currentPosition.Y + body.Velocity.Y * dt);

            foreach (var fixture in body.DynamicFixtures)
            {
                // Test against static bodies, the grid hands us only the ones nearby...
                foreach (int candidate in FindStaticCandidates(fixture, new Vector2(nextPositionX.X, currentPosition.Y), nextPositionY))
                {
                    var otherFixture = staticGrid.GetFixture(candidate);
                    if (fixture.TestIntersection(otherFixture, nextPositionY, staticGrid.GetBodyPosition(candidate)))
                    {
                        nextPositionY.Y = currentPosition.Y;
                        body.Velocity = new Vector2(body.Velocity.X, -body.Velocity.Y * body.Restitution);

                        fixture.IsTouching = true;
                        otherFixture.IsTouching = true;
                        fixture.ActiveContacts.Add(otherFixture);
                    }
                }

                // Test against dynamic bodies...
                foreach (var other in dynamicBodies)
                {
                    // Yeah lets not collide with ourselves, or with our own kind
                    if (other == body || SameGroup(body, other)) continue;

                    foreach (var otherFixture in other.DynamicFixtures)
                    {
                        if (fixture.TestIntersection(otherFixture, nextPositionY, other.Position))
                        {
                            nextPositionY.Y = currentPosition.Y;
                            body.Velocity = new Vector2(body.Velocity.X, -body.Velocity.Y * body.Restitution);

                            other.Velocity = -body.Velocity;

                            fixture.IsTouching = true;
                            otherFixture.IsTouching = true;
                            fixture.ActiveContacts.Add(otherFixture);
                        }
                    }
                }
            }

            // 5. Update Kinematic Triggers against final position
            foreach (var fixture in body.KinematicFixtures)
            {
                foreach (int candidate in FindStaticCandidates(fixture, new Vector2(nextPositionX.X, currentPosition.Y), nextPositionY))
                {
                    var otherFixture = staticGrid.GetFixture(candidate);
                    if (fixture.TestIntersection(otherFixture, nextPositionY, staticGrid.GetBodyPosition(candidate)))
                    {
                        fixture.IsTouching = true;
                        otherFixture.IsTouching = true;
                        fixture.ActiveContacts.Add(otherFixture);
                    }
                }

                foreach (var other in dynamicBodies)
                {
                    // Yeah lets not collide with ourselves, or with our own kind
                    if (other == body || SameGroup(body, other)) continue;

                    foreach (var otherFixture in other.DynamicFixtures)
                    {
                        if (fixture.TestIntersection(otherFixture, nextPositionY, other.Position))
                        {
                            fixture.IsTouching = true;
                            otherFixture.IsTouching = true;
                            fixture.ActiveContacts.Add(otherFixture);
                        }
                    }
                }
            }

            body.Position = nextPositionY;
        }

        // 6. Step the particles against where everything ended up
        UpdateParticles(dt);
    }

    // Whether two bodies are of a group, in which case they don't collide (see PhysicsBodyComponent2D.CollisionGroup)
    private static bool SameGroup(PhysicsBodyComponent2D a, PhysicsBodyComponent2D b) =>
        a.CollisionGroup != 0 && a.CollisionGroup == b.CollisionGroup;

    /// <summary>
    /// Helper method to find the static fixtures a fixture could run into while its body moves from one position to another.
    /// </summary>
    private ReadOnlySpan<int> FindStaticCandidates(IPhysicsFixture fixture, Vector2 from, Vector2 to)
    {
        if (!PhysicsShape.TryCreate(fixture, Vector2.Zero, Vector2.Zero, out var local)) return default;

        return staticGrid.Query(Vector2.Min(from, to) + local.Min, Vector2.Max(from, to) + local.Max);
    }

    private void UpdateParticles(float dt)
    {
        if (particleGroups.Count == 0 && particleFeeds.Count == 0) return;

        // The bodies are few, so their fixtures are simply placed in the world once and tested by every particle
        int shapeCount = 0;
        Vector2 bodiesMin = new(float.MaxValue), bodiesMax = new(float.MinValue);
        foreach (var body in DynamicBodies)
        {
            // What the body has for particles in particular, or else what it stands on the map with
            foreach (var fixture in body.ParticleFixtures.Count > 0 ? body.ParticleFixtures : body.DynamicFixtures)
            {
                if (!PhysicsShape.TryCreate(fixture, body.Position, body.Velocity, out var shape)) continue;

                if (shapeCount == dynamicShapes.Length)
                {
                    Array.Resize(ref dynamicShapes, dynamicShapes.Length * 2);
                }
                dynamicShapes[shapeCount++] = shape;

                // Most particles are nowhere near a body, one box around all of them rules those out in a single test
                bodiesMin = Vector2.Min(bodiesMin, shape.Min);
                bodiesMax = Vector2.Max(bodiesMax, shape.Max);
            }
        }

        foreach (var group in particleGroups)
        {
            group.Step(staticGrid, dynamicShapes.AsSpan(0, shapeCount), bodiesMin, bodiesMax, dt);
        }

        foreach (var feed in particleFeeds)
        {
            feed.Publish(staticGrid, dynamicShapes.AsSpan(0, shapeCount));
        }
    }
    public override void Render(float dt)
    {
        void drawBody(in PhysicsBodyComponent2D body, in List<IPhysicsFixture> fixtures, Vector3 colour)
        {
            foreach (var fixture in fixtures)
            {
                if (fixture is CirclePhysicsFixture c)
                {
                    debugRenderer.DrawCircle(body.Position + c.Position, c.Radius, colour);
                }
                else if (fixture is OutlinePhysicsFixture outline)
                {
                    var pieces = outline.Segments;
                    for (int i = 0; i + 1 < pieces.Length; i += 2)
                    {
                        debugRenderer.DrawSegment(body.Position + pieces[i], body.Position + pieces[i + 1], colour);
                    }
                }
                else if (fixture is RectanglePhysicsFixture r)
                {
                    debugRenderer.DrawPolygon(new Vector2[] {
                            new Vector2(body.Position.X + r.Bounds.Left, body.Position.Y + r.Bounds.Top),
                            new Vector2(body.Position.X + r.Bounds.Right, body.Position.Y + r.Bounds.Top),
                            new Vector2(body.Position.X + r.Bounds.Right, body.Position.Y + r.Bounds.Bottom),
                            new Vector2(body.Position.X + r.Bounds.Left, body.Position.Y + r.Bounds.Bottom),
                        }, colour);
                }
            }
        }

        if (RenderDebug)
        {
            debugRenderer.ClearBuffers();

            foreach (var body in StaticBodies)
            {
                drawBody(body, body.DynamicFixtures, new System.Numerics.Vector3(1, 0, 0));
                drawBody(body, body.KinematicFixtures, new System.Numerics.Vector3(1, 1, 0));
            }
            foreach (var body in DynamicBodies)
            {
                drawBody(body, body.DynamicFixtures, new System.Numerics.Vector3(0, 0, 1));
                drawBody(body, body.KinematicFixtures, new System.Numerics.Vector3(0, 1, 1));
                drawBody(body, body.ParticleFixtures, new System.Numerics.Vector3(0, 1, 0));
            }

            debugRenderer.Render(dt);
        }
    }
}
