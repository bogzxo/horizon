using System.Numerics;

using Horizon.Physics;
using Horizon.Physics.Fixtures;
using Horizon.Physics.Simulation;
using Horizon.Rendering.Lighting;
using Horizon.Rendering.Particles;
using Horizon.Rendering.Spriting;

namespace Horizon.Testing.Examples.Showcase;

// What gets thrown and what it leaves behind, the sparks, the embers and the rest of the particles.
public partial class DungeonExample
{
    private sealed class Spark
    {
        public required Sprite Sprite;
        public required Light2D Light;
        public Vector2 Position, Velocity;
        public float Life, TrailDue;
    }

    // How many can be in the air at once, how fast they go, how long before one gives up and how long between throws
    private const int SPARKS = 6;
    private const float SPARK_SPEED = 215.0f;
    private const float SPARK_LIFE = 1.3f;
    private const float SPARK_EVERY = 0.26f;

    // How far round where one goes off things are shoved, lit and burst
    private const float BLAST = 46.0f;
    private const float BLAST_PUSH = 150.0f;
    private const float KINDLE = 24.0f;
    private const float BURST = 20.0f;

    private readonly List<Spark> _sparks = [];

    // What a spark is tested against the map with, a small box that is nobody's
    private readonly RectanglePhysicsFixture _sparkProbe = new(new Vector2(-2.0f), new Vector2(4.0f));
    private float _sparkDue;

    /// <summary>
    /// Helper method to make the sparks there can be, put away until they are thrown, and the three kinds of
    /// particle. Every particle in here glows, so every one of them is a lamp the size of a pixel, and they are
    /// stopped by the walls and the bodies of the world (a <see cref="PhysicsParticleSimulator2D"/> each), an
    /// ember that lands by a crate lights the side of the crate.
    /// </summary>
    private void MakeSparks()
    {
        for (int i = 0; i < SPARKS; i++)
        {
            Sprite sprite = Make(_actors, "spark", 0, 1, 2, 0.06f);
            sprite.Enabled = false;
            Glow(sprite, new Vector3(1.0f, 0.82f, 0.45f), 1.0f);

            // Its glow is what the path tracer lights the passage with. This is for the shadows it throws on the
            // way, and for when the tracing is off
            var light = _world.AddLight(new Light2D
            {
                Color = new Vector3(1.0f, 0.75f, 0.4f),
                Radius = 62.0f,
                Intensity = 0.9f,
                Size = 2.0f,
                Enabled = false
            });

            _sparks.Add(new Spark { Sprite = sprite, Light = light });
        }

        _embers = Particles(new Vector3(1.0f, 0.78f, 0.3f), new Vector3(0.7f, 0.12f, 0.02f), 1.1f);
        _goo = Particles(new Vector3(0.55f, 1.0f, 0.6f), new Vector3(0.05f, 0.4f, 0.15f), 1.4f);
        _motes = Particles(new Vector3(0.85f, 0.95f, 1.0f), new Vector3(0.2f, 0.45f, 0.9f), 1.8f);
    }

    private ParticleRenderer2D Particles(Vector3 from, Vector3 to, float age) =>
        _world.AddEntity(new ParticleRenderer2D(4096, new PhysicsParticleSimulator2D(_physics)
        {
            Radius = 1.0f,
            Restitution = 0.55f,
            Friction = 3.0f,

            // The floor, as far as something that was sent flying over it is concerned
            LinearDrag = 2.6f
        })
        {
            StartColor = from,
            EndColor = to,
            ParticleSize = 1.5f,
            MaxAge = age,
            Emissive = 1.0f
        });

    /// <summary>Helper method to throw a spark a way, if there is one to be had and the last one was a moment ago.</summary>
    private void Throw(Vector2 way)
    {
        if (_sparkDue > 0.0f || _won || way.LengthSquared() < 0.0001f)
            return;

        foreach (Spark spark in _sparks)
        {
            if (spark.Life > 0.0f)
                continue;

            way = Vector2.Normalize(way);

            spark.Position = _walker.Position + way * 8.0f;
            spark.Velocity = way * SPARK_SPEED + _walker.Velocity * 0.35f;
            spark.Life = SPARK_LIFE;
            spark.Sprite.Enabled = true;
            spark.Light.Enabled = true;

            _sparkDue = SPARK_EVERY;
            return;
        }
    }

    /// <summary>
    /// Helper method to fly the sparks. A spark is not a body, it goes in a straight line and is asked every tick
    /// whether it is in the map yet (<see cref="PhysicsWorld.OverlapsStatic"/>) or near enough to a slime or a
    /// crate, and goes off when it is.
    /// </summary>
    private void MoveSparks(float dt)
    {
        foreach (Spark spark in _sparks)
        {
            if (spark.Life <= 0.0f)
                continue;

            spark.Life -= dt;
            spark.Position += spark.Velocity * dt;
            spark.Sprite.Transform.Position = spark.Position;
            spark.Light.Position = spark.Position;

            // A few embers shed on the way, they light the floor behind it for a moment
            if ((spark.TrailDue -= dt) <= 0.0f)
            {
                spark.TrailDue = 0.03f;
                _embers.AddCone(spark.Position, -Vector2.Normalize(spark.Velocity), 0.9f, 1, 30);
            }

            if (spark.Life <= 0.0f || _physics.OverlapsStatic(_sparkProbe, spark.Position) || HitsSomebody(spark.Position))
                GoOff(spark);
        }
    }

    private bool HitsSomebody(Vector2 at)
    {
        foreach (Slime slime in _slimes)
        {
            if (slime.Alive && Vector2.DistanceSquared(slime.Body.Position, at) < 8.0f * 8.0f) return true;
        }

        foreach (Crate crate in _crates)
        {
            Vector2 off = Vector2.Abs(crate.Body.Position - at);
            if (off.X < 9.0f && off.Y < 9.0f) return true;
        }

        return false;
    }

    /// <summary>
    /// Helper method for a spark going off. A flash, a burst of embers, and a shove to everything round it that is
    /// a body or a particle, which is one call to the world. The crates slide off, the slimes that are too far
    /// to burst are knocked back, the embers that were lying about are blown away. Then whatever was right next
    /// to it, a brazier catches and a slime does not get up again.
    /// </summary>
    private void GoOff(Spark spark)
    {
        Vector2 at = spark.Position;

        spark.Life = 0.0f;
        spark.Sprite.Enabled = false;
        spark.Light.Enabled = false;

        _world.AddFlash(new Light2D { Position = at, Radius = 150.0f, Intensity = 2.2f, Color = new Vector3(1.0f, 0.8f, 0.5f), Glow = 0.3f }, 0.35f);
        _embers.AddBurst(at, 46, 85);

        // Him excepted, it is his spark
        _physics.ApplyRadialImpulse(at, BLAST, BLAST_PUSH, PhysicsRadialTargets.All, _walker.Body);

        foreach (Brazier brazier in _braziers)
        {
            if (!brazier.Lit && Vector2.DistanceSquared(brazier.Position, at) < KINDLE * KINDLE)
            {
                Kindle(brazier);
                Say(Lit() == _braziers.Count ? "every brazier is burning, there is no dark left to be in" : "a brazier catches", 2.5f);
            }
        }

        foreach (Slime slime in _slimes)
        {
            if (slime.Alive && Vector2.DistanceSquared(slime.Body.Position, at) < BURST * BURST)
                Burst(slime);
        }
    }
}
