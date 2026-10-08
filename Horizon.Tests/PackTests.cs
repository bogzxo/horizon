using Horizon.Rendering.Spriting;

namespace Horizon.Tests;

/// <summary>
/// The UI packs that come with the engine, checked for the mistakes that are easy to make in a sheet file and only
/// show up as a wrong bit of art on screen. Every sprite and every frame of it has to lie inside of its image, and
/// the art that comes out of the pack's Aseprite files has to actually be drawn on the layer and the frame it names.
/// </summary>
public class PackTests
{
    private const string PACK = "Horizon.Rendering.UIX/Assets/uix/dead_revolver";

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
    /// Helper method to get the size of an image of the pack, a PNG or the canvas of an Aseprite file.
    /// </summary>
    private static (int Width, int Height) ImageSize(string path)
    {
        var size = ImagePixels.SizeOf(path);
        Assert.True(size.X > 0 && size.Y > 0, $"'{path}' can't be read.");

        return ((int)size.X, (int)size.Y);
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

    /// <summary>
    /// Helper method to count the pixels of a frame of a sprite that have anything on them.
    /// </summary>
    private static int Drawn(in SpriteSource sprite, int frame = 0)
    {
        byte[] pixels = (sprite.ReadFrame(frame) ?? throw new Exception($"A frame of '{sprite.Path}' can't be read.")).Data;

        int drawn = 0;
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) drawn++;

        return drawn;
    }

    [Fact]
    public void DeadRevolverArtIsDrawnInEveryTheme()
    {
        var sheet = SpriteSheetDefinition.Load(Path.Combine(Repo(), PACK), "sprites.hor");
        var empty = new List<string>();

        foreach (string name in sheet.Sprites)
        {
            foreach (string? theme in sheet.Themes.Append(null))
            {
                Assert.True(sheet.TryGetSprite(name, theme, out var sprite));

                // A layer that was misspelt or a frame that is one off comes out as nothing at all. The first frame of
                // an effect can well be nothing, so any frame with something on it will do
                bool drawn = false;
                for (int frame = 0; frame < sprite.Frames && !drawn; frame++) drawn = Drawn(sprite, frame) > 0;

                if (!drawn) empty.Add($"{name}@{theme ?? "default"}");
            }
        }

        Assert.True(empty.Count == 0, "Nothing is drawn of: " + string.Join(", ", empty));
    }

    [Fact]
    public void DeadRevolverThemesAndStatesComeOffTheAsepriteFiles()
    {
        var sheet = SpriteSheetDefinition.Load(Path.Combine(Repo(), PACK), "sprites.hor");

        // A theme is a layer of the file: the same rectangle, other pixels
        Assert.True(sheet.TryGetSprite("button", "blue", out var blue));
        Assert.True(sheet.TryGetSprite("button", "red", out var red));
        Assert.EndsWith(".aseprite", blue.Path);
        Assert.Equal((blue.X, blue.Y, blue.Width, blue.Height), (red.X, red.Y, red.Width, red.Height));
        Assert.NotEqual(blue.Layers, red.Layers);
        Assert.False(blue.ReadFrame(0)!.Value.Data.AsSpan().SequenceEqual(red.ReadFrame(0)!.Value.Data));

        // A state is a frame of it
        Assert.True(sheet.TryGetSprite("button_pressed", "blue", out var pressed));
        Assert.Equal((0, 2), (blue.ImageFrame, pressed.ImageFrame));
        Assert.Equal(blue.Layers, pressed.Layers);

        // A sprite can say which theme it wants whatever theme is asked for
        Assert.True(sheet.TryGetSprite("button@red", "blue", out var asked));
        Assert.Equal(red.Layers, asked.Layers);

        // An animation is a tag, and a heart is on a layer of its own whatever the theme
        Assert.True(sheet.TryGetSprite("star", "blue", out var star));
        Assert.Equal(4, star.Frames);
        Assert.Equal([2, 3, 4, 5], star.ImageFrames!);

        Assert.True(sheet.TryGetSprite("heart", "blue", out var heart));
        Assert.True(sheet.TryGetSprite("heart", "red", out var sameHeart));
        Assert.Equal(heart.Layers, sameHeart.Layers);
        Assert.True(Drawn(heart) > 0);
    }
}
