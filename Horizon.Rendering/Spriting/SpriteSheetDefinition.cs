using System.Numerics;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Horizon.Rendering.Spriting;

/// <summary>
/// Where a named sprite is to be found: which image it is in and which part of it.
/// </summary>
/// <param name="Path">The image file.</param>
/// <param name="X">The left edge of the first frame, in pixels from the left of the image.</param>
/// <param name="Y">The top edge, in pixels from the top of the image.</param>
/// <param name="Border">
/// How much of every edge (left, top, right, bottom) keeps its size when the sprite is stretched as a nine-slice,
/// zero for sprites that are simply scaled.
/// </param>
/// <param name="Content">
/// How far in from every edge (left, top, right, bottom) whatever is put on top of the sprite goes, like the label of a
/// button whose art has a lip along the bottom. Zero for sprites that leave it to whoever draws them.
/// </param>
/// <param name="Frames">How many frames there are, they follow each other to the right.</param>
/// <param name="FrameTime">How long every frame of an animation is shown for, in seconds.</param>
public readonly record struct SpriteSource(
    string Path, int X, int Y, int Width, int Height, Vector4 Border, Vector4 Content, int Frames, float FrameTime);

/// <summary>
/// The names of the sprites in a set of images, as written down in a HIDL file (see Assets/uix/dead_revolver/sprites.hor
/// for one that explains itself). It only says where everything is, nothing is loaded: pair it with a
/// <see cref="TextureAtlas"/> to have the sprites that are actually used stitched together.
/// Sheets that hold the same art in a number of colours can say so by naming their themes. A sprite is then written down
/// once, for the first copy of the art, and looked up in whichever theme is wanted.
/// The same goes for art that is repeated for its states (a button, the same button hovered, the same button pressed):
/// an image names its states and every sprite in it can be asked for as "name_hover" without being written down again.
/// </summary>
public sealed class SpriteSheetDefinition
{
    private const char FRAME_SEPARATOR = '#';
    private const char THEME_SEPARATOR = '@';

    private sealed class ImageDefinition
    {
        public string Path = string.Empty;
        public int Block;                                       // How wide one copy of the art is, 0 for images without themes
        public Dictionary<string, int> Themes = [];             // Which copy every theme starts at
        public string Fallback = string.Empty;                  // The theme to show for themes the image doesn't have
        public Vector2 Size;                                    // How big a sprite is unless it says so itself, zero if they all have to
        public Dictionary<string, int> States = [];             // The other states every sprite comes in, and how many blocks along they are
    }

    private readonly record struct SpriteDefinition(
        ImageDefinition Image, int Block, int X, int Y, int Width, int Height, Vector4 Border, Vector4 Content, int Frames, float FrameTime);

    private readonly Dictionary<string, SpriteDefinition> _sprites = [];
    private readonly HashSet<string> _themes = [];

    /// <summary>
    /// The file this was loaded from, for telling definitions apart.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Every theme at least one of the images comes in.
    /// </summary>
    public IReadOnlyCollection<string> Themes => _themes;

    /// <summary>
    /// The name of every sprite.
    /// </summary>
    public IReadOnlyCollection<string> Sprites => _sprites.Keys;

    private SpriteSheetDefinition(string path)
    {
        Path = path;
    }

    /// <summary>
    /// Finds a sprite by name. "name#3" is the fourth frame of a sprite that has frames, on its own, and "name@white"
    /// is the sprite in the white theme whatever theme is asked for (both at once is written "name@white#3").
    /// </summary>
    /// <param name="theme">The theme to look for it in, null (or a theme its image doesn't come in) gives the image's fallback.</param>
    public bool TryGetSprite(string name, string? theme, out SpriteSource source)
    {
        source = default;

        // Test if a single frame is asked for
        int frame = -1;
        int separator = name.LastIndexOf(FRAME_SEPARATOR);
        if (separator > 0)
        {
            if (!int.TryParse(name.AsSpan(separator + 1), out frame)) return false;
            name = name[..separator];
        }

        // Test if the name brings a theme of its own
        int themeSeparator = name.IndexOf(THEME_SEPARATOR);
        if (themeSeparator > 0)
        {
            theme = name[(themeSeparator + 1)..];
            name = name[..themeSeparator];
        }

        if (!_sprites.TryGetValue(name, out var sprite)) return false;
        if (frame >= sprite.Frames) return false;

        var image = sprite.Image;
        int block = sprite.Block;

        if (image.Block > 0)
        {
            if (theme is null || !image.Themes.TryGetValue(theme, out int themeBlock))
            {
                image.Themes.TryGetValue(image.Fallback, out themeBlock);
            }
            block += themeBlock;
        }

        source = new SpriteSource(
            image.Path,
            block * image.Block + sprite.X + Math.Max(frame, 0) * sprite.Width,
            sprite.Y,
            sprite.Width,
            sprite.Height,
            sprite.Border,
            sprite.Content,
            frame >= 0 ? 1 : sprite.Frames,
            sprite.FrameTime);
        return true;
    }

    /// <summary>
    /// Loads a definition, the images it names are looked for next to it. Throws (saying what is wrong) if it can't be read.
    /// </summary>
    public static SpriteSheetDefinition Load(string directory, string file)
    {
        string path = System.IO.Path.Combine(directory, file);
        if (!File.Exists(path))
            throw new FileNotFoundException($"The sprite definition '{path}' doesn't exist.");

        HIDLRuntime runtime = new();
        var (success, message) = runtime.Evaluate(File.ReadAllText(path));
        if (!success)
            throw new Exception($"'{path}': {message}");

        if (runtime.UserScope.Lookup("sheet") is not ObjectValue sheet)
            throw new Exception($"'{path}' has to declare an object called 'sheet'.");

        if (!sheet.Properties.TryGetValue("images", out var imagesValue) || imagesValue is not ObjectValue images)
            throw new Exception($"'{path}': the sheet has to have an object called 'images'.");

        var definition = new SpriteSheetDefinition(path);
        foreach (var (name, value) in images.Properties)
        {
            if (value is not ObjectValue image)
                throw new Exception($"'{path}': image '{name}' has to be an object.");

            definition.ReadImage(directory, name, image.Properties);
        }

        return definition;
    }

    private void ReadImage(string directory, string name, Dictionary<string, IRuntimeValue> properties)
    {
        if (!properties.TryGetValue("file", out var file) || file is not StringValue fileName)
            throw new Exception($"Image '{name}' has to name its file.");

        var image = new ImageDefinition { Path = System.IO.Path.Combine(directory, fileName.Value) };

        if (properties.TryGetValue("block", out var block))
            image.Block = (int)Number(block, $"{name}.block");

        if (properties.TryGetValue("themes", out var themesValue))
        {
            if (themesValue is not ObjectValue themes)
                throw new Exception($"{name}.themes has to be an object.");
            if (image.Block < 1)
                throw new Exception($"Image '{name}' has themes, so it has to say how wide a block of it is.");

            foreach (var (theme, index) in themes.Properties)
            {
                image.Themes[theme] = (int)Number(index, $"{name}.themes.{theme}");
                _themes.Add(theme);

                // Unless told otherwise the first one listed stands in for the themes that are missing
                if (image.Fallback.Length == 0) image.Fallback = theme;
            }
        }

        if (properties.TryGetValue("fallback", out var fallback))
        {
            if (fallback is not StringValue fallbackName || !image.Themes.ContainsKey(fallbackName.Value))
                throw new Exception($"{name}.fallback has to be one of the themes of the image.");

            image.Fallback = fallbackName.Value;
        }

        if (properties.TryGetValue("size", out var size))
        {
            image.Size = size switch
            {
                NumberValue number => new Vector2(number.Value),
                Vector2Value vector => vector.Value,
                _ => throw new Exception($"{name}.size has to be a number or a vec(width, height).")
            };
        }

        if (properties.TryGetValue("states", out var statesValue))
        {
            if (statesValue is not ObjectValue states)
                throw new Exception($"{name}.states has to be an object.");
            if (image.Block < 1)
                throw new Exception($"Image '{name}' has states, so it has to say how wide a block of it is.");

            foreach (var (state, index) in states.Properties)
                image.States[state] = (int)Number(index, $"{name}.states.{state}");
        }

        if (!properties.TryGetValue("sprites", out var spritesValue) || spritesValue is not ObjectValue sprites)
            throw new Exception($"Image '{name}' has to have an object called 'sprites'.");

        foreach (var (spriteName, value) in sprites.Properties)
        {
            if (value is not ObjectValue sprite)
                throw new Exception($"Sprite '{spriteName}' has to be an object.");

            SpriteDefinition definition = ReadSprite(image, spriteName, sprite.Properties);
            Add(spriteName, definition);

            // The same sprite in every other state of the image, so many blocks further along
            foreach (var (state, offset) in image.States)
                Add($"{spriteName}_{state}", definition with { Block = definition.Block + offset });
        }
    }

    private void Add(string name, SpriteDefinition sprite)
    {
        if (!_sprites.TryAdd(name, sprite))
            throw new Exception($"There are two sprites called '{name}'.");
    }

    private static SpriteDefinition ReadSprite(ImageDefinition image, string name, Dictionary<string, IRuntimeValue> properties)
    {
        float Required(string key) =>
            properties.TryGetValue(key, out var value)
                ? Number(value, $"{name}.{key}")
                : throw new Exception($"Sprite '{name}' is missing its {key}.");

        float Optional(string key, float otherwise) =>
            properties.TryGetValue(key, out var value) ? Number(value, $"{name}.{key}") : otherwise;

        // A number for all four sides, vec(horizontal, vertical) or vec(left, top, right, bottom)
        Vector4 Edges(string key) =>
            !properties.TryGetValue(key, out var value) ? Vector4.Zero : value switch
            {
                NumberValue number => new Vector4(number.Value),
                Vector2Value vector => new Vector4(vector.Value.X, vector.Value.Y, vector.Value.X, vector.Value.Y),
                Vector4Value vector => vector.Value,
                _ => throw new Exception($"{name}.{key} has to be a number, a vec(horizontal, vertical) or a vec(left, top, right, bottom).")
            };

        return new SpriteDefinition(
            image,
            (int)Optional("block", 0),
            (int)Required("x"),
            (int)Required("y"),
            (int)(image.Size.X > 0 ? Optional("w", image.Size.X) : Required("w")),
            (int)(image.Size.Y > 0 ? Optional("h", image.Size.Y) : Required("h")),
            Edges("border"),
            Edges("content"),
            Math.Max(1, (int)Optional("frames", 1)),
            Optional("time", 0.1f));
    }

    private static float Number(IRuntimeValue value, string what) =>
        value is NumberValue number ? number.Value : throw new Exception($"{what} has to be a number.");
}
