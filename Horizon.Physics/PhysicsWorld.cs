using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Physics.Debug;
using Horizon.Physics.Fixtures;
using Horizon.Rendering;

namespace Horizon.Physics;

public class PhysicsWorld : IGameComponent
{
    public bool RenderDebug { get; set; } = true;
    public ConcurrentBag<PhysicsBodyComponent2D> StaticBodies { get; init; } = new();
    public ConcurrentBag<PhysicsBodyComponent2D> DynamicBodies { get; init; } = new();

    public bool Enabled { get; set; }
    public string Name { get; set; } = "Physics World";
    public Entity Parent { get; set; }

    private PhysicsWorldDebugRenderer debugRenderer = new();

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
        // TODO: implement dynamics
    }
    public void UpdateState(float dt)
    {

    }
    public void Render(float dt, object? obj = null)
    {
        void drawBody(in PhysicsBodyComponent2D body, Vector3 colour)
        {
            foreach (var fixture in body.Fixtures)
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
                drawBody(body, new System.Numerics.Vector3(1, 0, 0));
            }
            foreach (var body in DynamicBodies)
            {
                drawBody(body, new System.Numerics.Vector3(0, 0, 1));
            }


            debugRenderer.Render(dt);
        }
    }
}
