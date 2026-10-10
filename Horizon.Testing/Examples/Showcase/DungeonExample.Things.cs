using System.Numerics;

using Horizon.Core.Tweening;
using Horizon.Physics;
using Horizon.Physics.Fixtures;
using Horizon.Rendering.Lighting;
using Horizon.Rendering.Spriting;
using Horizon.UI;

namespace Horizon.Testing.Examples.Showcase;

// What stands about the dungeon, the braziers, the gate, the crystals, the crates and the slimes. Each is a sprite
// off the one sheet and whatever it needs besides, a light, a body, a place to hop to.
public partial class DungeonExample
{
    private sealed class Brazier
    {
        public required Sprite Sprite;
        public required Light2D Light;
        public required Vector2 Position;
        public bool Lit;
        public float EmberDue;
    }

    private sealed class Crystal
    {
        public required Sprite Sprite;
        public required Vector4 Colour;
        public required Vector2 Position;
        public bool Carried;
    }

    private sealed class Crate
    {
        public required Sprite Sprite;
        public required PhysicsBodyComponent2D Body;
    }

    private sealed class Slime
    {
        public required Sprite Sprite;
        public required PhysicsBodyComponent2D Body;
        public bool Alive = true;
        public float HopDue;
    }

    // How near a slime has to be to go for him and to bite him, how hard it kicks off and how often
    private const float SLIME_SIGHT = 105.0f;
    private const float SLIME_BITE = 11.0f;
    private const float SLIME_HOP = 92.0f;
    private const float SLIME_REST = 0.85f;

    // How far round him the crystals he carries go, and how fast
    private const float ORBIT = 14.0f;
    private const float ORBIT_SPEED = 2.1f;

    private readonly List<Brazier> _braziers = [];
    private readonly List<Crystal> _crystals = [];
    private readonly List<Crate> _crates = [];
    private readonly List<Slime> _slimes = [];

    private readonly List<Sprite> _gateBars = [];
    private readonly List<IPhysicsFixture> _gateFixtures = [];
    private bool _gateOpen;
    private float _gateGone;

    /// <summary>
    /// Helper method to make a sprite off the sheet with one animation, a row of cells starting somewhere.
    /// </summary>
    private Sprite Make(SpriteBatch batch, string animation, int column, int row, uint frames, float frameTime = 0.15f)
    {
        // AddEntity gets it updated (its animation moves along in there), Add gets it drawn
        var sprite = batch.AddEntity(new Sprite(new Vector2(CELL)));
        sprite.ConfigureSpriteSheet(_sheet, animation);
        sprite.AddAnimation(animation, new Vector2(column, row), frames, frameTime);
        batch.Add(sprite);
        return sprite;
    }

    /// <summary>
    /// Helper method to have a sprite glow, which under the path tracer is having it be a lamp. It is painted so
    /// much of the way towards a colour and gives off that much of it, see <see cref="Sprite.FlashLights"/>.
    /// </summary>
    private static void Glow(Sprite sprite, Vector3 colour, float amount)
    {
        sprite.FlashColor = new Vector4(colour, 1.0f);
        sprite.FlashAmount = amount;
        sprite.FlashLights = amount > 0.0f;
    }

    /* Braziers. Cold until a spark goes off next to one, and then a lamp for good */

    private void MakeBraziers()
    {
        foreach (var (position, lit) in _brazierSpots)
        {
            Sprite sprite = Make(_props, "cold", 2, 1, 1);
            sprite.AddAnimation("burning", new Vector2(3, 1), 2, 0.12f);
            sprite.Transform.Position = position;

            // In the way, like anything made of iron
            _ground.CreateRectangularFixture(position - new Vector2(5.0f), new Vector2(10.0f));

            // A light as well as a glow. The glow is what the path tracer goes by and gets the bounce right, the
            // light is what throws the hard shadows of the pillars, and with the tracing off it is all there is
            var light = _world.AddLight(new Light2D
            {
                Position = position,
                Color = new Vector3(1.0f, 0.72f, 0.38f),
                Radius = 105.0f,
                Intensity = 1.0f,
                Size = 4.0f,
                Flicker = 0.14f,
                SpriteShadow = 0.7f,
                Enabled = false
            });

            var brazier = new Brazier { Sprite = sprite, Light = light, Position = position };
            _braziers.Add(brazier);

            if (lit) Kindle(brazier, quietly: true);
        }
    }

    private void Kindle(Brazier brazier, bool quietly = false)
    {
        if (brazier.Lit)
            return;

        brazier.Lit = true;
        brazier.Light.Enabled = true;
        brazier.Sprite.SetAnimation("burning");
        Glow(brazier.Sprite, new Vector3(1.0f, 0.8f, 0.45f), 0.85f);

        if (quietly)
            return;

        _embers.AddBurst(brazier.Position, 26, 55);
        brazier.Sprite.Punch(0.3f, 0.3f);
    }

    /// <summary>Helper method to have the braziers that burn spit an ember now and then, each of which lights a bit of floor on its way.</summary>
    private void TendBraziers(float dt)
    {
        foreach (Brazier brazier in _braziers)
        {
            if (!brazier.Lit || (brazier.EmberDue -= dt) > 0.0f)
                continue;

            brazier.EmberDue = 0.12f + Random.Shared.NextSingle() * 0.3f;

            float angle = Random.Shared.NextSingle() * MathF.Tau;
            _embers.AddCone(brazier.Position, new Vector2(MathF.Cos(angle), MathF.Sin(angle)), 0.6f, 1, 22);
        }
    }

    private int Lit()
    {
        int lit = 0;
        foreach (Brazier brazier in _braziers)
        {
            if (brazier.Lit) lit++;
        }

        return lit;
    }

    /* The gate. A door that is in the way of him and of the light behind it, until he has what it wants */

    private void MakeGate()
    {
        int bars = Math.Max(1, (int)MathF.Round(_gateSpot.Size.X / CELL));
        for (int i = 0; i < bars; i++)
        {
            Vector2 corner = _gateSpot.Min + new Vector2(i * CELL, 0.0f);

            Sprite bar = Make(_props, "shut", 5, 1, 1);
            bar.Transform.Position = corner + new Vector2(CELL / 2.0f);

            // It blocks light as well as him. What is behind it is the brightest thing in the dungeon, and none
            // of that gets past a shut door
            bar.CastsShadows = true;
            _gateBars.Add(bar);

            _gateFixtures.Add(_ground.CreateRectangularFixture(corner, new Vector2(CELL)));
        }
    }

    /// <summary>
    /// Helper method to open the gate once he stands in front of it with all three, and to see whether he has got
    /// out. The door fades, and as it does it stops blocking, the light of the pool behind it comes down the
    /// passage a bit more with every frame. Nobody wrote that, a sprite blocks as much as there is of it.
    /// </summary>
    private void WatchTheGate(float dt)
    {
        Vector2 at = _walker.Position;

        if (!_gateOpen)
        {
            Vector2 middle = _gateSpot.Min + _gateSpot.Size / 2.0f;
            if (Carried() == _crystals.Count && _crystals.Count > 0 && Vector2.Distance(at, middle) < 40.0f)
            {
                _gateOpen = true;
                _gateGone = 0.9f;

                // Out of the world, the map is counted again and he can walk through
                foreach (IPhysicsFixture fixture in _gateFixtures)
                    _ground.DynamicFixtures.Remove(fixture);

                foreach (Sprite bar in _gateBars)
                    bar.TweenOpacity(0.0f, 0.9f).SetEasing(Easing.InQuad);

                _motes.AddBurst(middle, 90, 80);
                _world.AddFlash(new Light2D { Position = middle, Radius = 190.0f, Intensity = 2.4f, Color = new Vector3(0.7f, 0.9f, 1.0f) }, 0.7f);
                Say("the gate lets go", 3.0f);
            }
        }
        else if (_gateGone > 0.0f && (_gateGone -= dt) <= 0.0f)
        {
            foreach (Sprite bar in _gateBars)
                bar.Enabled = false;
        }

        if (!_won && at.X >= _exit.Min.X && at.X <= _exit.Max.X && at.Y >= _exit.Min.Y && at.Y <= _exit.Max.Y)
        {
            _won = true;
            _banner.Text = "OUT OF THE DARK";
            _banner.PopIn(0.5f);
            _motes.AddBurst(at, 160, 120);
            Say("that is all of it, esc is the way back to the menu", 60.0f);
        }
    }

    /* Crystals. They glow where they lie, and round him once he has them */

    private void MakeCrystals()
    {
        foreach (var (position, colour) in _crystalSpots)
        {
            Sprite sprite = Make(_actors, "glint", 4, 0, 4, 0.22f);
            sprite.Transform.Position = position;

            // The stone is white, the colour is its own, and it gives off most of it
            sprite.Tint = colour;
            Glow(sprite, new Vector3(colour.X, colour.Y, colour.Z), 0.8f);

            _crystals.Add(new Crystal { Sprite = sprite, Colour = colour, Position = position });
        }
    }

    /// <summary>
    /// Helper method to pick a crystal up when he walks into it, and to carry the ones he has round and round him.
    /// They are still lamps, so the more he has found the more of a lamp he is, in three colours that go round the
    /// walls as they go round him.
    /// </summary>
    private void CarryCrystals(float dt)
    {
        Vector2 middle = _walker.Position;
        int carried = 0;

        for (int i = 0; i < _crystals.Count; i++)
        {
            Crystal crystal = _crystals[i];

            if (!crystal.Carried)
            {
                // Bobbing where it lies, so it reads as something to be had
                crystal.Sprite.Transform.Position = crystal.Position + new Vector2(0.0f, MathF.Sin(_time * 3.0f + i) * 1.5f);

                if (Vector2.Distance(middle, crystal.Position) > 11.0f)
                    continue;

                crystal.Carried = true;
                crystal.Sprite.Punch(0.6f, 0.4f);
                _motes.AddBurst(crystal.Position, 50, 70);
                _world.AddFlash(new Light2D { Position = crystal.Position, Radius = 150.0f, Intensity = 2.0f, Color = new Vector3(crystal.Colour.X, crystal.Colour.Y, crystal.Colour.Z) }, 0.5f);
                Say(Carried() == _crystals.Count ? "that is all three, the gate is north of the hall" : "a crystal, and it goes on glowing", 3.5f);
            }

            // A third of the way round from the last one, and after it smoothly, not snapped into place
            float angle = _time * ORBIT_SPEED + carried++ * MathF.Tau / 3.0f;
            Vector2 wanted = middle + new Vector2(MathF.Cos(angle), MathF.Sin(angle) * 0.7f) * ORBIT;
            crystal.Position = Vector2.Lerp(crystal.Position, wanted, 1.0f - MathF.Exp(-9.0f * dt));
            crystal.Sprite.Transform.Position = crystal.Position;
        }
    }

    private int Carried()
    {
        int carried = 0;
        foreach (Crystal crystal in _crystals)
        {
            if (crystal.Carried) carried++;
        }

        return carried;
    }

    /* Crates. Bodies, heavier than he is and with the floor dragging at them, there to be leant on */

    private void MakeCrates()
    {
        foreach (Vector2 position in _crateSpots)
        {
            Sprite sprite = Make(_actors, "crate", 6, 1, 1);
            sprite.Transform.Position = position;

            // A box in the way of a lamp throws a shadow, and the shadow goes where the box is pushed
            sprite.CastsShadows = true;

            var body = _physics.CreateBody(PhysicsBodySimulationType.Dynamic, position);
            body.CreateRectangularFixture(new Vector2(-7.0f), new Vector2(14.0f));
            body.Mass = 2.5f;
            body.Restitution = 0.0f;
            body.LinearDrag = 9.0f;
            body.StopsFlush = true;

            _crates.Add(new Crate { Sprite = sprite, Body = body });
        }
    }

    private void ShowCrates()
    {
        foreach (Crate crate in _crates)
            crate.Sprite.Transform.Position = crate.Body.Position;
    }

    /* Slimes. Bodies that get about by kicking themselves off, at him when they can see him */

    private void MakeSlimes()
    {
        foreach (Vector2 position in _slimeSpots)
        {
            Sprite sprite = Make(_actors, "wobble", 0, 0, 4, 0.16f);
            sprite.Transform.Position = position;
            sprite.CastsShadows = true;

            // Faintly, and green. A slime coming up a dark passage is seen by what it lights before it is seen
            Glow(sprite, new Vector3(0.45f, 1.0f, 0.55f), 0.32f);

            var body = _physics.CreateBody(PhysicsBodySimulationType.Dynamic, position);
            body.CreateRectangularFixture(new Vector2(-5.0f, -4.0f), new Vector2(10.0f, 8.0f));
            body.Restitution = 0.3f;
            body.LinearDrag = 3.5f;
            body.StopsFlush = true;

            _slimes.Add(new Slime { Sprite = sprite, Body = body, HopDue = Random.Shared.NextSingle() * SLIME_REST });
        }
    }

    /// <summary>
    /// Helper method for the slimes. Every so often one kicks off, at him if he is near enough to be seen and any
    /// old way if not, and the floor slows it down again, which is a hop. A kick is an impulse to its body and
    /// that is all the moving there is, the world does the rest, walls, crates and each other included.
    /// </summary>
    private void MoveSlimes(float dt)
    {
        Vector2 hero = _walker.Position;

        foreach (Slime slime in _slimes)
        {
            if (!slime.Alive)
                continue;

            Vector2 at = slime.Body.Position;
            slime.Sprite.Transform.Position = at + new Vector2(0.0f, 3.0f);

            Vector2 to = hero - at;
            float distance = to.Length();

            if (distance < SLIME_BITE)
                Hurt(at);

            if ((slime.HopDue -= dt) > 0.0f)
                continue;

            slime.HopDue = SLIME_REST * (0.8f + Random.Shared.NextSingle() * 0.5f);

            Vector2 way;
            if (distance < SLIME_SIGHT && distance > 0.01f && !_won)
            {
                way = to / distance;
            }
            else
            {
                float angle = Random.Shared.NextSingle() * MathF.Tau;
                way = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 0.5f;
            }

            slime.Body.ApplyImpulse(way * (SLIME_HOP * slime.Body.Mass));
            slime.Sprite.Flipped = way.X < 0.0f;
            slime.Sprite.Punch(0.25f, 0.3f);
        }
    }

    /// <summary>Helper method to see a slime off. It goes up in goo, which glows as it did, and its body leaves the world.</summary>
    private void Burst(Slime slime)
    {
        slime.Alive = false;
        slime.Sprite.Enabled = false;
        _physics.DynamicBodies.Remove(slime.Body);

        _goo.AddBurst(slime.Body.Position, 70, 95);
        _world.AddFlash(new Light2D { Position = slime.Body.Position, Radius = 120.0f, Intensity = 1.6f, Color = new Vector3(0.45f, 1.0f, 0.55f) }, 0.4f);
    }
}
