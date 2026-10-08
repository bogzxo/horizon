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
    /// Helper method to read the size of a PNG without decoding it. Width and height follow the signature and the first chunk's length and name.
    /// </summary>
    private static (int Width, int Height) PngSize(string path)
    {
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
                    sizes[sprite.Path] = size = PngSize(sprite.Path);

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
}
