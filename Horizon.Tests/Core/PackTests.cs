using System.Buffers.Binary;

using Horizon.Rendering.Spriting;

namespace Horizon.Tests;

/// <summary>
/// The UI packs that come with the engine, checked for the mistakes that are easy to make in a sheet file and only
/// show up as a wrong bit of art on screen. Every sprite and every frame of it has to lie inside of its image.
/// </summary>
public class PackTests
{
    private const string PACK = "Horizon/Assets/uix/dead_revolver";

    /// <summary>
    /// Helper method to find the repo from wherever the tests run, by the solution file.
    /// </summary>
    private static string Repo()
    {
        for (string? at = AppContext.BaseDirectory; at is not null; at = Path.GetDirectoryName(at))
        {
            if (File.Exists(Path.Combine(at, "Horizon.sln")))
                return at;
        }

        throw new DirectoryNotFoundException("The tests have to run somewhere under the repo.");
    }

    /// <summary>
    /// Helper method to read the size of an image without decoding it. An Aseprite file says so in its header, a PNG
    /// has width and height after the signature and the first chunk's length and name.
    /// </summary>
    private static (int Width, int Height) ImageSize(string path)
    {
        if (AsepriteDocument.IsAseprite(path))
        {
            var document = AsepriteDocument.Open(path);
            return (document.Width, document.Height);
        }

        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[24];
        file.ReadExactly(header);
        return (BinaryPrimitives.ReadInt32BigEndian(header[16..]), BinaryPrimitives.ReadInt32BigEndian(header[20..]));
    }

    [Fact]
    public void DeadRevolverSpritesLieInsideTheirImages()
    {
        string directory = Path.Combine(Repo(), PACK);
        var sheet = SpriteSheetDefinition.Load(directory, "sprites.hor");
        var sizes = new Dictionary<string, (int Width, int Height)>();
        var wrong = new List<string>();

        foreach (string name in sheet.Sprites)
        {
            // In every theme the sheet has, and in none
            foreach (string? theme in sheet.Themes.Append(null))
            {
                Assert.True(sheet.TryGetSprite(name, theme, out var sprite), $"'{name}' can't be looked up.");

                if (!sizes.TryGetValue(sprite.Path, out var size))
                    sizes[sprite.Path] = size = ImageSize(sprite.Path);

                // On a layer the file has, for an image that keeps its themes on layers
                if (sprite.Layer is { } layer && !AsepriteDocument.Open(sprite.Path).HasLayer(layer))
                    wrong.Add($"{name}@{theme ?? "default"} is on the layer '{layer}', which {Path.GetFileName(sprite.Path)} hasn't got");

                for (int frame = 0; frame < sprite.Frames; frame++)
                {
                    (int x, int y) = sprite.FrameAt(frame);
                    if (x < 0 || y < 0 || x + sprite.Width > size.Width || y + sprite.Height > size.Height)
                        wrong.Add($"{name}@{theme ?? "default"}#{frame} at {x},{y} {sprite.Width}x{sprite.Height} is outside of {Path.GetFileName(sprite.Path)} ({size.Width}x{size.Height})");
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void DeadRevolverKeepsItsThemesOnLayers()
    {
        string directory = Path.Combine(Repo(), PACK);
        var sheet = SpriteSheetDefinition.Load(directory, "sprites.hor");

        // Every theme the pack comes in is a layer of one of its files
        Assert.Equal(["blue", "dark", "gold", "green", "purple", "red", "sakura", "tan", "white"], sheet.Themes.OrderBy(t => t));

        // A theme picks the layer and nothing else, the sprite is in the same place on every one
        Assert.True(sheet.TryGetSprite("button", "red", out var red));
        Assert.True(sheet.TryGetSprite("button", "white", out var white));
        Assert.Equal("red", red.Layer);
        Assert.Equal("white", white.Layer);
        Assert.Equal((red.X, red.Y), (white.X, white.Y));
        Assert.EndsWith("buttons.aseprite", red.Path);

        // And they are different art
        Assert.False(red.ReadFrame(0)!.Value.Data.AsSpan().SequenceEqual(white.ReadFrame(0)!.Value.Data));

        // The other states are blocks along the layer, as they always were
        Assert.True(sheet.TryGetSprite("button_hover", "red", out var hover));
        Assert.Equal(red.X + 208, hover.X);
        Assert.Equal("red", hover.Layer);

        // A theme the image doesn't come in is its fallback, and one of its own (name@theme) beats the one asked for
        Assert.True(sheet.TryGetSprite("button", "nonsense", out var unknown));
        Assert.Equal("blue", unknown.Layer);
        Assert.True(sheet.TryGetSprite("button@white", "red", out var own));
        Assert.Equal("white", own.Layer);

        // The form elements have no tan, so tan is their fallback
        Assert.True(sheet.TryGetSprite("round_check", "tan", out var check));
        Assert.Equal("white", check.Layer);

        // An image of one colour is read as it looks, no layer named
        Assert.True(sheet.TryGetSprite("heart", "red", out var heart));
        Assert.Null(heart.Layer);
    }

    [Fact]
    public void ALayerIsReadOnItsOwnWhetherItIsSwitchedOnOrNot()
    {
        var document = AsepriteDocument.Open(Path.Combine(Repo(), PACK, "panels.aseprite"));
        Assert.Equal(["white", "dark", "tan", "gold", "red", "purple", "blue", "sakura"], document.TopLayers);

        // Only blue is switched on in the file, so the frame as it looks is blue, and white read by name is not
        byte[] shown = document.ReadFrame(0);
        byte[] blue = document.ReadFrame(0, "blue");
        byte[] white = document.ReadFrame(0, "white");

        Assert.True(shown.AsSpan().SequenceEqual(blue));
        Assert.False(white.AsSpan().SequenceEqual(blue));
        Assert.Contains(white, b => b != 0);

        Assert.Throws<ArgumentException>(() => document.ReadFrame(0, "nonsense"));
    }

    [Fact]
    public void DeadRevolverHasTheGamepadsAndTheKeyboard()
    {
        var sheet = SpriteSheetDefinition.Load(Path.Combine(Repo(), PACK), "sprites.hor");

        foreach (string name in new[] { "xbox_a", "xbox_lt", "xbox_dpad_left", "ps_cross", "ps_options", "key_q", "key_space", "key_enter_pressed", "mouse_left" })
            Assert.Contains(name, sheet.Sprites);

        // The keys of the keyboard are stacked, every frame of a key is at the same x and further down
        Assert.True(sheet.TryGetSprite("key_q", null, out var q));
        Assert.Equal(4, q.Frames);
        for (int frame = 1; frame < q.Frames; frame++)
        {
            Assert.Equal(q.X, q.FrameAt(frame).X);
            Assert.True(q.FrameAt(frame).Y > q.FrameAt(frame - 1).Y);
        }

        // A single frame asked for by name is that frame on its own
        Assert.True(sheet.TryGetSprite("key_q#2", null, out var third));
        Assert.Equal(q.FrameAt(2), (third.X, third.Y));
        Assert.Equal(1, third.Frames);

        // Frames laid out with a step of their own
        Assert.True(sheet.TryGetSprite("key_wide", null, out var wide));
        Assert.Equal((wide.X + 32, wide.Y), wide.FrameAt(1));
    }
}
