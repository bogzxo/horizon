using System.Numerics;

using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Rendering;
using Horizon.Rendering.Lighting;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.Tiling;

namespace Horizon.Testing.Examples;

/// <summary>
/// The bits the examples that draw the town or the cellar share, so each of them is about what it is about and
/// not about loading the same sheet a fourth time. Where the files are, the blob everybody uses as their hero,
/// and turning the lamps a map has in it into lights, which is the one of the three worth reading.
/// </summary>
internal static class World
{
    /// <summary>The street at night, a map with one of everything. Open it in Tiled.</summary>
    public const string TOWN = "Assets/examples/world/town.tmx";

    /// <summary>A brick room with pillars and lamps, for the lights to have something to be blocked by.</summary>
    public const string CELLAR = "Assets/examples/world/cellar.tmx";

    /// <summary>The blob's sheet, a grid of frames 16 pixels square, a row an animation.</summary>
    public const string BLOB = "Assets/examples/sprites/blob.png";

    /// <summary>How big a frame of the blob is, in pixels.</summary>
    public const int BLOB_CELL = 16;

    /// <summary>
    /// Makes a blob off its sheet, with its animations (idle, hop and blink), and puts it in a batch. Render thread,
    /// the sheet is a texture. Its position is where its feet are.
    /// </summary>
    /// <param name="scale">How many units of the world a pixel of the art is.</param>
    public static Sprite CreateBlob(SpriteBatch batch, float scale = 1.0f)
    {
        if (!GameEngine.Instance.ObjectManager.Textures.TryCreate(
                new TextureDescription { Paths = [BLOB], Definition = TextureDefinition.RgbaUnsignedByteNearest },
                out var texture))
        {
            throw new Exception($"Couldn't make a texture out of {BLOB}: {texture.Message}");
        }

        var sheet = SpriteSheet.FromTexture(texture.Asset, new Vector2(BLOB_CELL));

        // AddEntity gets it updated (its animations move along in there), Add gets it drawn
        var blob = batch.AddEntity(new Sprite(new Vector2(BLOB_CELL * scale)));
        blob.Transform.Origin = Horizon.Rendering.Origin.Bottom;
        blob.ConfigureSpriteSheet(sheet, "idle");
        blob.AddAnimation("idle", new Vector2(0, 0), 4, 0.18f);
        blob.AddAnimation("hop", new Vector2(0, 1), 6, 0.1f);
        blob.AddAnimation("blink", new Vector2(0, 2), 8, 0.12f);
        batch.Add(blob);
        return blob;
    }

    /// <summary>
    /// Makes a light for every object of a map that says it is one (its class is "light") and adds them to a
    /// renderer. The maps put their lamps down from a template (objects/lantern.tx, objects/spot.tx) and only say
    /// what is different about each, a colour, how far it reaches, and this is where those properties are read.
    /// Nothing in the engine knows what a "light_radius" is, a game decides what its maps call things.
    /// </summary>
    /// <returns>The lights by the name their object has, to be found again (the moon, the spot that swings).</returns>
    public static Dictionary<string, Light2D> AddLights(TileMap map, DeferredRenderer2D lighting)
    {
        var lights = new Dictionary<string, Light2D>();

        map.DispatchObjects(objects => objects.OfClass("light", found =>
        {
            TileMapProperties says = found.Properties;
            Vector4 colour = says.GetColor("light_colour", Vector4.One);

            var light = new Light2D
            {
                Position = found.Position,
                Color = new Vector3(colour.X, colour.Y, colour.Z),
                Radius = says.GetFloat("light_radius", 128.0f),
                Intensity = says.GetFloat("light_intensity", 1.0f),
                Flicker = says.GetFloat("light_flicker"),
                Glow = 0.15f,

                // How big the lamp itself is, which is how soft the edge of what it throws
                Size = 5.0f,
                SpriteShadow = 0.7f
            };

            switch (says.GetString("light_type", "point"))
            {
                case "spot":
                    light.Type = LightType.Spot;
                    light.ConeAngle = says.GetFloat("light_cone", 90.0f) * MathF.PI / 180.0f;
                    light.Direction = says.GetFloat("light_direction", -90.0f) * MathF.PI / 180.0f;
                    break;

                case "directional":
                    light.Type = LightType.Directional;
                    light.Direction = says.GetFloat("light_direction", -90.0f) * MathF.PI / 180.0f;
                    light.Reach = says.GetFloat("light_reach", 400.0f);
                    light.Height = 80.0f;
                    break;
            }

            lights[found.Name] = lighting.AddLight(light);
        }));

        return lights;
    }

    /// <summary>What a map says its ambient light is (a colour property called "ambient"), or something dim if it doesn't.</summary>
    public static Vector3 AmbientOf(TileMap map)
    {
        Vector4 ambient = map.Properties.GetColor("ambient", new Vector4(0.18f, 0.2f, 0.28f, 1.0f));
        return new Vector3(ambient.X, ambient.Y, ambient.Z);
    }
}
