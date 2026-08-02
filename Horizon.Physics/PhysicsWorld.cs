using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

using Box2D.NetStandard.Dynamics.Bodies;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Physics.Debug;
using Horizon.Physics.Fixtures;
using Horizon.Rendering;

namespace Horizon.Physics;

public class PhysicsWorld : IGameComponent
{
    public bool RenderDebug { get; set; } = true;
    public List<PhysicsBodyComponent2D> StaticBodies { get; init; } = [];
    public List<PhysicsBodyComponent2D> DynamicBodies { get; init; } = new();

    public bool Enabled { get; set; }
    public string Name { get; set; } = "Physics World";
    public Entity Parent { get; set; }

    public Vector2 Gravity { get; set; }

    private readonly PhysicsWorldDebugRenderer debugRenderer = new();

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

    public void Initialize()
    {
        debugRenderer.Initialize();
    }
    public void UpdatePhysics(float dt)
    {
       
    }
    public void UpdateState(float dt)
    {
        if (!Enabled) return;

        var dynamicBodies = CollectionsMarshal.AsSpan<PhysicsBodyComponent2D>(this.DynamicBodies);
        var staticBodies = CollectionsMarshal.AsSpan<PhysicsBodyComponent2D>(this.StaticBodies);

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
                foreach (var other in staticBodies)
                {
                    foreach (var otherFixture in other.DynamicFixtures)
                    {
                        if (fixture.TestIntersection(otherFixture, nextPositionX, other.Position))
                        {
                            nextPositionX.X = currentPosition.X;
                            body.Velocity = new Vector2(-body.Velocity.X * body.Restitution, body.Velocity.Y);

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
                foreach (var other in staticBodies)
                {
                    foreach (var otherFixture in other.DynamicFixtures)
                    {
                        if (fixture.TestIntersection(otherFixture, nextPositionY, other.Position))
                        {
                            nextPositionY.Y = currentPosition.Y;
                            body.Velocity = new Vector2(body.Velocity.X, -body.Velocity.Y * body.Restitution);

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
                foreach (var other in staticBodies)
                {
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
    }
    public void Render(float dt, object? obj = null)
    {
        void drawBody(in PhysicsBodyComponent2D body, in List<IPhysicsFixture> fixtures, Vector3 colour)
        {
            foreach (var fixture in fixtures)
            {
                if (fixture is CirclePhysicsFixture c)
                {
                    debugRenderer.DrawCircle(body.Position + c.Position, c.Radius, colour);
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
            }


            debugRenderer.Render(dt);
        }
    }
}
