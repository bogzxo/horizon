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
/// <param name="Frames">How many frames there are. Where each one is comes out of <see cref="FrameAt"/>.</param>
/// <param name="FrameTime">How long every frame of an animation is shown for, in seconds.</param>
/// <param name="Step">How far the next frame is from the one before, when the frames are laid out evenly. One sprite's width to the right unless the sheet says.</param>
/// <param name="FramePositions">Where every frame after the first is, for frames that aren't laid out evenly. Null when they are.</param>
public readonly record struct SpriteSource(
    string Path, int X, int Y, int Width, int Height, Vector4 Border, Vector4 Content, int Frames, float FrameTime,
    (int X, int Y) Step = default, (int X, int Y)[]? FramePositions = null)
{
    /// <summary>
    /// The pixels of one frame, RGBA from the top left, for whoever wants to look at the art itself (tracing boxes
    /// off it, say). Null if the image can't be read, which has been logged. Read off disk the first time, so not
    /// something to do every frame.
    /// </summary>
    public SpritePixels? ReadFrame(int frame)
    {
        (int x, int y) = FrameAt(Math.Clamp(frame, 0, Math.Max(Frames - 1, 0)));
        return SpriteImages.Read(Path, x, y, Width, Height);
    }

    /// <summary>
    /// The top left corner of a frame, the first being where the sprite is.
    /// </summary>
    public (int X, int Y) FrameAt(int frame)
    {
        if (frame <= 0) return (X, Y);

        if (FramePositions is { } positions)
            return frame - 1 < positions.Length ? positions[frame - 1] : (X, Y);

        (int stepX, int stepY) = Step == default ? (Width, 0) : Step;
        return (X + frame * stepX, Y + frame * stepY);
    }
}

/// <summary>
/// The names of the sprites in a set of images, as written down in a HIDL file (see Assets/uix/dead_revolver/sprites.hor
/// for one that explains itself). It only says where everything is, nothing is loaded: pair it with a
/// <see cref="TextureAtlas"/> to have the sprites that are actually used stitched together.
/// Sheets that hold the same art in a number of colours can say so by naming their themes. A sprite is then written down
/// once, for the first copy of the art, and looked up in whichever theme is wanted.
/// The same goes for art that is repeated for its states (a button, the same button hovered, the same button pressed):
/// an image names its states and every sprite in it can be asked for as "name_hover" without being written down again.
/// A sheet file is a program, so what is written down over and over (a key for every key of a keyboard) can be written
/// as a loop instead, see the keyboard of the Dead Revolver pack.
/// </summary>
public sealed class SpriteSheetDefinition
{
    private const char FRAME_SEPARATOR = '#';
    private const char THEME_SEPARATOR = '@';

    /// <summary>What a folder of sprites calls its definition, for <see cref="Open"/>.</summary>
    public const string DEFAULT_FILE = "definition.hor";

    private sealed class ImageDefinition
    {
        public string Path = string.Empty;
        public (int X, int Y) Block;                            // How far one copy of the art is from the next, zero for images with one copy
        public Dictionary<string, int> Themes = [];             // Which copy every theme starts at
        public string Fallback = string.Empty;                  // The theme to show for themes the image doesn't have
        public Vector2 Size;                                    // How big a sprite is unless it says so itself, zero if they all have to
        public Vector2 Cell;                                    // How big a cell of the grid is, for sprites written by their cell. Zero for no grid
        public Vector2 Origin;                                  // Where the grid starts
        public Dictionary<string, int> States = [];             // The other states every sprite comes in, and how many blocks along they are
    }

    private readonly record struct SpriteDefinition(
        ImageDefinition Image, int Block, int X, int Y, int Width, int Height, Vector4 Border, Vector4 Content, int Frames, float FrameTime,
        (int X, int Y) Step, (int X, int Y)[]? FramePositions);

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

    /// <summary>Whether there is a sprite of that name.</summary>
    public bool Has(string name) => _sprites.ContainsKey(name);

    /// <summary>How many frames a sprite has, 0 for one there isn't.</summary>
    public int FrameCount(string name) => _sprites.TryGetValue(name, out var sprite) ? sprite.Frames : 0;

    /// <summary>
    /// Opens whatever is at a path. An Aseprite file is read as it is, every tag an animation, a folder is read
    /// through the definition.hor in it, and a .hor file is read as a definition. Throws, saying what's wrong, if
    /// it can't be.
    /// </summary>
    public static SpriteSheetDefinition Open(string path)
    {
        if (AsepriteDocument.IsAseprite(path))
            return FromAseprite(AsepriteDocument.Open(path));

        if (Directory.Exists(path))
            return Load(path, DEFAULT_FILE);

        return Load(System.IO.Path.GetDirectoryName(path) ?? string.Empty, System.IO.Path.GetFileName(path));
    }

    /// <summary>
    /// Every tag of an Aseprite file as a sprite with as many frames as the tag has. The frames lie side by side
    /// on a sheet that only exists in the mind of the atlas, frame i being at x = i * width, which is how it and
    /// <see cref="SpriteSource.ReadFrame"/> find them again.
    /// </summary>
    public static SpriteSheetDefinition FromAseprite(AsepriteDocument document)
    {
        var definition = new SpriteSheetDefinition(document.Path);
        var image = new ImageDefinition { Path = document.Path, Size = new Vector2(document.Width, document.Height) };

        foreach (var tag in document.Tags)
        {
            // The tag's own pace, or the file's if the frames don't agree with each other
            float time = 0.0f;
            for (int frame = tag.From; frame <= tag.To; frame++) time += document.Duration(frame);
            time /= Math.Max(tag.Frames, 1);

            definition.Add(tag.Name, new SpriteDefinition(
                image, 0, tag.From * document.Width, 0, document.Width, document.Height, Vector4.Zero, Vector4.Zero,
                tag.Frames, time > 0.0f ? time : 0.1f, (document.Width, 0), null));
        }

        return definition;
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

        if (image.Block != default)
        {
            if (theme is null || !image.Themes.TryGetValue(theme, out int themeBlock))
            {
                image.Themes.TryGetValue(image.Fallback, out themeBlock);
            }
            block += themeBlock;
        }

        var whole = new SpriteSource(
            image.Path,
            block * image.Block.X + sprite.X,
            block * image.Block.Y + sprite.Y,
            sprite.Width,
            sprite.Height,
            sprite.Border,
            sprite.Content,
            sprite.Frames,
            sprite.FrameTime,
            sprite.Step,
            Shift(sprite.FramePositions, block * image.Block.X, block * image.Block.Y));

        if (frame < 0)
        {
            source = whole;
            return true;
        }

        // One frame on its own, as a sprite of one frame
        (int x, int y) = whole.FrameAt(frame);
        source = whole with { X = x, Y = y, Frames = 1, FramePositions = null };
        return true;
    }

    private static (int X, int Y)[]? Shift((int X, int Y)[]? positions, int byX, int byY)
    {
        if (positions is null || (byX == 0 && byY == 0)) return positions;

        var shifted = new (int X, int Y)[positions.Length];
        for (int i = 0; i < positions.Length; i++)
            shifted[i] = (positions[i].X + byX, positions[i].Y + byY);

        return shifted;
    }

    /// <summary>
    /// Loads a definition, the images it names are looked for next to it. Throws (saying what is wrong) if it can't be read.
    /// </summary>
    public static SpriteSheetDefinition Load(string directory, string file)
    {
        string path = System.IO.Path.Combine(directory, file);
        if (!File.Exists(path))
            throw new FileNotFoundException($"The sprite definition '{path}' doesn't exist.");

        HIDLRuntime runtime = new() { BaseDirectory = directory, Output = null };
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

        // How far the next copy of the art is. A number is so many pixels to the right, a vector goes any way
        if (properties.TryGetValue("block", out var block))
        {
            image.Block = block switch
            {
                NumberValue number => ((int)number.Value, 0),
                Vector2Value vector => ((int)vector.Value.X, (int)vector.Value.Y),
                _ => throw new Exception($"{name}.block has to be a number or a vec(right, down).")
            };
        }

        if (properties.TryGetValue("themes", out var themesValue))
        {
            if (themesValue is not ObjectValue themes)
                throw new Exception($"{name}.themes has to be an object.");
            if (image.Block == default)
                throw new Exception($"Image '{name}' has themes, so it has to say how far apart its blocks are.");

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
            image.Size = Pair(size, $"{name}.size");

        // A grid the sprites sit in, so they can be written by their cell rather than in pixels
        if (properties.TryGetValue("cell", out var cell))
        {
            image.Cell = Pair(cell, $"{name}.cell");
            if (image.Size == default) image.Size = image.Cell;
        }

        if (properties.TryGetValue("origin", out var origin))
            image.Origin = Pair(origin, $"{name}.origin");

        if (properties.TryGetValue("states", out var statesValue))
        {
            if (statesValue is not ObjectValue states)
                throw new Exception($"{name}.states has to be an object.");
            if (image.Block == default)
                throw new Exception($"Image '{name}' has states, so it has to say how far apart its blocks are.");

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

    /// <summary>
    /// Takes the sprites of another definition that this one hasn't got, for art that comes in more than one file.
    /// </summary>
    public void AddMissing(SpriteSheetDefinition other)
    {
        foreach (var (name, sprite) in other._sprites) _sprites.TryAdd(name, sprite);
        foreach (string theme in other._themes) _themes.Add(theme);
    }

    private static SpriteDefinition ReadSprite(ImageDefinition image, string name, Dictionary<string, IRuntimeValue> properties)
    {
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

        // Where it is. In pixels, or by its cell of the grid the image has
        int x, y;
        if (properties.TryGetValue("cell", out var cellValue))
        {
            if (image.Cell == default)
                throw new Exception($"Sprite '{name}' is written by its cell, but its image has no cell size.");

            Vector2 cell = Pair(cellValue, $"{name}.cell");
            x = (int)(image.Origin.X + cell.X * image.Cell.X);
            y = (int)(image.Origin.Y + cell.Y * image.Cell.Y);
        }
        else
        {
            x = (int)(properties.TryGetValue("x", out var xValue) ? Number(xValue, $"{name}.x") : throw new Exception($"Sprite '{name}' is missing its x."));
            y = (int)(properties.TryGetValue("y", out var yValue) ? Number(yValue, $"{name}.y") : throw new Exception($"Sprite '{name}' is missing its y."));
        }

        int width = (int)(image.Size.X > 0 ? Optional("w", image.Size.X) : properties.ContainsKey("w") ? Optional("w", 0) : throw new Exception($"Sprite '{name}' is missing its w."));
        int height = (int)(image.Size.Y > 0 ? Optional("h", image.Size.Y) : properties.ContainsKey("h") ? Optional("h", 0) : throw new Exception($"Sprite '{name}' is missing its h."));

        // The frames. A number of them laid out evenly (to the right unless a step says otherwise), or a list of
        // where every frame after the first is, for frames that are scattered about the image
        int frames = 1;
        (int X, int Y)[]? positions = null;
        if (properties.TryGetValue("frames", out var framesValue))
        {
            switch (framesValue)
            {
                case NumberValue number:
                    frames = Math.Max(1, (int)number.Value);
                    break;

                case ListValue list:
                    positions = new (int X, int Y)[list.Count];
                    for (int i = 0; i < list.Count; i++)
                    {
                        Vector2 at = Pair(list[i], $"{name}.frames[{i}]");
                        positions[i] = ((int)at.X, (int)at.Y);
                    }
                    frames = positions.Length + 1;
                    break;

                default:
                    throw new Exception($"{name}.frames has to be a number or a list of vec(x, y).");
            }
        }

        (int X, int Y) step = default;
        if (properties.TryGetValue("step", out var stepValue))
        {
            Vector2 by = Pair(stepValue, $"{name}.step");
            step = ((int)by.X, (int)by.Y);
        }

        return new SpriteDefinition(
            image,
            (int)Optional("block", 0),
            x,
            y,
            width,
            height,
            Edges("border"),
            Edges("content"),
            frames,
            Optional("time", 0.1f),
            step,
            positions);
    }

    private static float Number(IRuntimeValue value, string what) =>
        value is NumberValue number ? number.Value : throw new Exception($"{what} has to be a number.");

    // A number for both ways, or a vec(x, y)
    private static Vector2 Pair(IRuntimeValue value, string what) => value switch
    {
        NumberValue number => new Vector2(number.Value),
        Vector2Value vector => vector.Value,
        _ => throw new Exception($"{what} has to be a number or a vec(x, y).")
    };
}
