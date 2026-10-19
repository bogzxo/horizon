using System.Numerics;

using Horizon.Rendering.Lighting;
using Horizon.Rendering.Particles;
using Horizon.Rendering.Particles.Simulation;
using Horizon.Rendering.Tiling;

namespace Horizon.Testing.Examples.Showcase;

// The clock, and what the light does as it goes round. The sun and the moon are two directional lights the
// clock turns, the ambient follows them, the lamps of the maps are read off their objects and the windows only
// come on at night, and the fireflies come out over the pond.
public partial class VillageExample
{
    /// <summary>A light a map put down, as it was asked for, so the clock and the room it is in can scale it.</summary>
    private sealed class Lamp
    {
        public required Light2D Light;
        public required TileMapObject Object;
        public required TileMapRoom Room;
        public required float Intensity;
        public required bool Night;
    }

    // How many seconds of play a game hour takes, and how much faster with T held down
    private const float SECONDS_AN_HOUR = 9.0f;
    private const float HURRY = 12.0f;

    // How many fireflies come out a second over the pond at night
    private const float FIREFLY_RATE = 7.0f;

    // The sky and the sun at noon come to about 1 between them, which is the art as it was painted. With a sky
    // of nearly 1 and a sun of nearly 1 on top noon was twice what the screen can show and came out as milk
    private static readonly Vector3 DayAmbient = new(0.5f, 0.52f, 0.5f);
    private static readonly Vector3 NightAmbient = new(0.14f, 0.16f, 0.32f);
    private static readonly Vector3 NoonSun = new(1.0f, 0.97f, 0.9f);
    private static readonly Vector3 LowSun = new(1.0f, 0.62f, 0.34f);
    private static readonly Vector3 Moonlight = new(0.5f, 0.6f, 0.95f);

    private readonly List<Lamp> _lamps = [];

    private Light2D _sun = null!, _moon = null!;
    private ParticleRenderer2D _fireflies = null!;
    private (Vector2 Min, Vector2 Max) _pond;

    // The time of day in hours, and whether it is going by in a hurry
    private float _hour = 16.75f;
    private bool _hurry;
    private float _fireflyDue;

    private void MakeSky()
    {
        // The sun. Which way its rays go and how far they look for something in the way is the clock's to say,
        // see LightTheSky. The pines are what it is mostly blocked by, and a person only takes most of it
        _sun = _world.AddLight(new Light2D
        {
            Type = LightType.Directional,
            Size = 3.0f,
            Height = 60.0f,
            SpriteShadow = 0.8f
        });

        // The moon, low and cold and never very bright
        _moon = _world.AddLight(new Light2D
        {
            Type = LightType.Directional,
            Direction = -MathF.PI * 0.62f,
            Color = Moonlight,
            Reach = 60.0f,
            Size = 6.0f,
            Height = 80.0f,
            SpriteShadow = 0.6f,
            Enabled = false
        });

        // The lamps every map has in it. The same properties World.AddLights reads, and four of this
        // example's own, light_glow for the halo round a window, light_shadows for one that doesn't throw any
        // (a window lights the wall it is in, and a wall blocks everything from a light that is inside it),
        // light_height for how high over the floor it hangs, which with the heights the renderer was given is
        // how long the shadows under it are, and night for one that is only on in the dark
        foreach (TileMapRoom room in _rooms.Rooms)
        {
            room.Map.DispatchObjects(objects => objects.OfClass("light", found =>
            {
                TileMapProperties says = found.Properties;
                Vector4 colour = says.GetColor("light_colour", Vector4.One);

                var light = _world.AddLight(new Light2D
                {
                    Position = found.Position,
                    Color = new Vector3(colour.X, colour.Y, colour.Z),
                    Radius = says.GetFloat("light_radius", 100.0f),
                    Flicker = says.GetFloat("light_flicker"),
                    Glow = says.GetFloat("light_glow"),
                    CastsShadows = says.GetBool("light_shadows", true),
                    Height = says.GetFloat("light_height", 48.0f),
                    Size = 4.0f,
                    SpriteShadow = 0.7f,
                    Enabled = false
                });

                _lamps.Add(new Lamp
                {
                    Light = light,
                    Object = found,
                    Room = room,
                    Intensity = says.GetFloat("light_intensity", 1.0f),
                    Night = says.GetBool("night")
                });
            }));
        }

        _rooms.Outside.Map.DispatchObjects(objects => objects.Named("pond", found => _pond = (found.Min, found.Min + found.Size)));
    }

    private void MakeFireflies()
    {
        // They glow, and under the tracer a thing that glows is a lamp, so each one lights a bit of the pond
        _fireflies = _world.AddEntity(new ParticleRenderer2D(512, new ComputeParticleSimulator2D())
        {
            StartColor = new Vector3(0.75f, 1.0f, 0.4f),
            EndColor = new Vector3(0.3f, 0.6f, 0.15f),
            ParticleSize = 1.0f,
            MaxAge = 3.2f,
            Gravity = new Vector2(0.0f, 3.0f),
            Emissive = 1.0f
        });
    }

    private void PassTheTime(float dt)
    {
        _hour += dt / SECONDS_AN_HOUR * (_hurry ? HURRY : 1.0f);
        if (_hour >= 24.0f) _hour -= 24.0f;
    }

    /// <summary>Helper method to jump the clock on to dawn, noon, dusk or midnight, whichever is next.</summary>
    private void SkipAhead()
    {
        float next = _hour < 5.5f ? 5.5f : _hour < 12.0f ? 12.0f : _hour < 17.5f ? 17.5f : _hour < 23.0f ? 23.0f : 5.5f;
        _hour = next;
        Say($"skipped to {Daytime()}", 2.5f);
    }

    private string Daytime() => _hour switch
    {
        < 5.0f => "night",
        < 7.5f => "dawn",
        < 11.0f => "morning",
        < 14.0f => "midday",
        < 17.0f => "afternoon",
        < 19.5f => "dusk",
        < 21.5f => "evening",
        _ => "night"
    };

    /// <summary>
    /// Helper method to set every light by the clock and by the rooms. How high the sun is comes out of the
    /// hour, its rays go from the left at dawn round to the right at dusk and point down the screen at noon,
    /// looking a long way for something in the way when it is low and hardly any when it is high, which is
    /// the shadows of the pines swinging round and shrinking through the day. The moon takes over as it goes
    /// down, the ambient goes from day to night, and the lamps of a room are as bright as the room is shown.
    /// Indoors there is no sun to speak of, the street under the room keeps what little of it its dimness lets.
    /// </summary>
    private void LightTheSky()
    {
        // 0 all night, 1 at noon, and a share of the day that comes in fast at dawn for the ambient and the sky
        float daylight = MathF.Max(0.0f, MathF.Sin((_hour - 6.0f) / 12.0f * MathF.PI));
        float day = Smooth(0.0f, 0.28f, daylight);
        float night = 1.0f - Smooth(0.0f, 0.1f, daylight);

        // How much of outdoors there is to see, all of it in the street, none of it from inside a house
        TileMap street = _rooms.Outside.Map;
        float outdoors = _rooms.Current == _rooms.Outside ? 1.0f : Math.Clamp((street.Opacity - _rooms.Dim) / (1.0f - _rooms.Dim), 0.0f, 1.0f);

        _sun.Direction = -MathF.PI + MathF.PI * Math.Clamp((_hour - 6.0f) / 12.0f, 0.0f, 1.0f);
        _sun.Reach = Lerp(150.0f, 26.0f, daylight);
        _sun.Intensity = MathF.Pow(daylight, 0.55f) * 0.6f * outdoors;
        _sun.Color = Vector3.Lerp(LowSun, NoonSun, daylight);
        _sun.Enabled = _sun.Intensity > 0.01f;

        _moon.Intensity = 0.3f * night * outdoors;
        _moon.Enabled = _moon.Intensity > 0.01f;

        // The sky's ambient outdoors, the room's own (a property of its map) indoors, and between the two as
        // the room comes up
        Vector3 sky = Vector3.Lerp(NightAmbient, DayAmbient, day);
        Vector3 ambient = _rooms.Current == _rooms.Outside ? sky : Vector3.Lerp(World.AmbientOf(_rooms.Current.Map), sky, outdoors);
        _world.Ambient = ambient;
        _world.PathTracing.AmbientScale = Lerp(0.6f, 0.9f, day);

        // The haze is the night's, a lamp in the dark. Under the sun the air is clear, and the sun's own bounce
        // laid over the whole picture as haze was a good part of the washout
        if (_hazy) _world.PathTracing.Fog = HAZE * Lerp(1.0f, 0.15f, day);

        foreach (Lamp lamp in _lamps)
        {
            // Where the map has it now, the room may have been moved under it
            lamp.Light.Position = lamp.Object.Position;

            float shown = lamp.Room.Map.Opacity;
            if (lamp.Room != _rooms.Current && lamp.Room == _rooms.Outside) shown = outdoors;
            if (lamp.Night) shown *= night;

            lamp.Light.Intensity = lamp.Intensity * shown;
            lamp.Light.Enabled = lamp.Light.Intensity > 0.01f;
        }

        _fireflyShare = night * outdoors;
    }

    private float _fireflyShare;

    /// <summary>Helper method to let the fireflies out over the pond, a few a second once it is dark, each drifting a little way.</summary>
    private void TendFireflies(float dt)
    {
        if (_fireflyShare <= 0.05f || _pond.Max == Vector2.Zero) return;

        _fireflyDue += FIREFLY_RATE * _fireflyShare * dt;
        while (_fireflyDue >= 1.0f)
        {
            _fireflyDue -= 1.0f;

            var at = new Vector2(
                Lerp(_pond.Min.X, _pond.Max.X, Random.Shared.NextSingle()),
                Lerp(_pond.Min.Y, _pond.Max.Y, Random.Shared.NextSingle()));
            float angle = Random.Shared.NextSingle() * MathF.Tau;
            _fireflies.AddCone(at, new Vector2(MathF.Cos(angle), MathF.Sin(angle)), 0.5f, 1, 5.0f);
        }
    }

    private static float Lerp(float from, float to, float amount) => from + (to - from) * amount;

    private static float Smooth(float from, float to, float value)
    {
        float t = Math.Clamp((value - from) / (to - from), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }
}
