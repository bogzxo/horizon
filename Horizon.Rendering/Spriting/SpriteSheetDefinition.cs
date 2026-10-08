using System.Numerics;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Horizon.Rendering.Spriting;

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
/// <para>
/// An image can be an Aseprite file (.ase or .aseprite), and then the file itself says most of it: a theme is one of
/// its layers, a state is one of its frames, a sprite can be one of its slices (which brings its nine-slice border
/// along) and an animation one of its tags. See <see cref="ReadAsepriteImage"/> for how that is written down, and
/// <see cref="FromAseprite"/> for a definition straight off a file with no sheet file at all.
/// </para>
/// </summary>
public sealed partial class SpriteSheetDefinition
{
    private const char FRAME_SEPARATOR = '#';
    private const char THEME_SEPARATOR = '@';

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

        // For an image that is an Aseprite file
        public AsepriteDocument? Document;
        public Dictionary<string, string> ThemeLayers = [];     // The layer (or group) every theme is drawn on
        public Dictionary<string, int> StateFrames = [];        // Which frame of the file the other states of every sprite are on
        public string[] Hidden = [];                            // Layers that are never drawn, whatever the file says
    }

    private readonly record struct SpriteDefinition(
        ImageDefinition Image, int Block, int X, int Y, int Width, int Height, Vector4 Border, Vector4 Content, int Frames, float FrameTime,
        (int X, int Y) Step, (int X, int Y)[]? FramePositions,
        int ImageFrame = 0, int[]? ImageFrames = null, float[]? FrameTimes = null,
        bool Trim = false, bool Crop = false, bool Loops = true, Vector2? Pivot = null, string? Layer = null);

    private readonly Dictionary<string, SpriteDefinition> _sprites = [];
    private readonly HashSet<string> _themes = [];

    // What the sprites that are cropped to their art came to, which takes painting their frames to find out
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Name, string Layers), (int X, int Y, int Width, int Height)> _cropped = new();

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
            Shift(sprite.FramePositions, block * image.Block.X, block * image.Block.Y),
            LayersOf(image, theme, sprite.Layer),
            sprite.ImageFrame,
            sprite.ImageFrames,
            sprite.FrameTimes,
            sprite.Trim,
            sprite.Loops,
            sprite.Pivot);

        if (sprite.Crop) whole = Crop(name, whole, image);

        if (frame < 0)
        {
            source = whole;
            return true;
        }

        // One frame on its own, as a sprite of one frame
        (int x, int y) = whole.FrameAt(frame);
        source = whole with
        {
            X = x, Y = y, Frames = 1, FramePositions = null,
            ImageFrame = whole.ImageFrameAt(frame), ImageFrames = null, FrameTime = whole.TimeOf(frame), FrameTimes = null
        };
        return true;
    }

    /// <summary>
    /// How many frames a sprite has, 0 for a sprite the definition doesn't have.
    /// </summary>
    public int FrameCount(string name) => _sprites.TryGetValue(name, out var sprite) ? sprite.Frames : 0;

    /// <summary>
    /// Test if there is a sprite by this name.
    /// </summary>
    public bool Has(string name) => _sprites.ContainsKey(name);

    /// <summary>
    /// Helper method to say which layers of an Aseprite image a theme is drawn with: the layer of the theme (or of the
    /// image's fallback) and none of the other themes' layers. Everything else is drawn the way the file was saved.
    /// </summary>
    /// <param name="own">The one layer a sprite is drawn on whatever the theme, null for a sprite that goes by the theme.</param>
    private static LayerSelection LayersOf(ImageDefinition image, string? theme, string? own)
    {
        if (image.Document is not { } document) return default;

        string? spec = null;
        if (own is not null)
        {
            // That layer and none of the ones next to it, whatever they are
            spec = LayerSelection.Only(own, document.Layers.Where(layer => layer.Depth == 0).Select(layer => layer.Name)).Spec;
        }
        else if (image.ThemeLayers.Count > 0)
        {
            if (theme is null || !image.ThemeLayers.TryGetValue(theme, out string? layer))
                image.ThemeLayers.TryGetValue(image.Fallback, out layer);

            if (layer is not null) spec = LayerSelection.Only(layer, image.ThemeLayers.Values).Spec;
        }

        if (image.Hidden.Length > 0)
        {
            string hidden = LayerSelection.Hiding(image.Hidden).Spec!;
            spec = spec is null ? hidden : spec + "," + hidden;
        }

        return new LayerSelection(spec);
    }

    /// <summary>
    /// Helper method to shrink a sprite to what is actually drawn of it, over all of its frames. Worked out once for every
    /// theme, the colours of a pack aren't promised to be drawn the same to the pixel.
    /// </summary>
    private SpriteSource Crop(string name, in SpriteSource whole, ImageDefinition image)
    {
        if (image.Document is not { } document) return whole;

        var key = (name, whole.Layers.Spec ?? string.Empty);
        if (!_cropped.TryGetValue(key, out var bounds))
        {
            int[] frames = whole.ImageFrames ?? [whole.ImageFrame];
            bounds = _cropped[key] = document.BoundsOf(frames, whole.Layers, whole.X, whole.Y, whole.Width, whole.Height);
        }

        // Nothing drawn at all, it stays the size it was given
        return bounds.Width == 0 ? whole : whole with { X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height };
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

        if (AsepriteDocument.IsAseprite(image.Path))
        {
            ReadAsepriteImage(name, image, properties);
            return;
        }

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

    private static SpriteDefinition ReadSprite(ImageDefinition image, string name, Dictionary<string, IRuntimeValue> properties)
    {
        if (image.Document is not null)
            return ReadAsepriteSprite(image, name, properties);

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

        ReadFrames(name, properties, out int frames, out (int X, int Y)[]? positions, out (int X, int Y) step);

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

    /// <summary>
    /// Helper method to read the frames of a sprite that are laid out on its image. A number of them laid out evenly (to
    /// the right unless a step says otherwise), or a list of where every frame after the first is, for frames that are
    /// scattered about the image.
    /// </summary>
    private static void ReadFrames(string name, Dictionary<string, IRuntimeValue> properties, out int frames, out (int X, int Y)[]? positions, out (int X, int Y) step)
    {
        frames = 1;
        positions = null;
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

        step = default;
        if (properties.TryGetValue("step", out var stepValue))
        {
            Vector2 by = Pair(stepValue, $"{name}.step");
            step = ((int)by.X, (int)by.Y);
        }
    }

    // A number for all four sides, vec(horizontal, vertical) or vec(left, top, right, bottom). Null if the sprite doesn't say
    private static Vector4? EdgesOf(string name, Dictionary<string, IRuntimeValue> properties, string key) =>
        !properties.TryGetValue(key, out var value) ? null : value switch
        {
            NumberValue number => new Vector4(number.Value),
            Vector2Value vector => new Vector4(vector.Value.X, vector.Value.Y, vector.Value.X, vector.Value.Y),
            Vector4Value vector => vector.Value,
            _ => throw new Exception($"{name}.{key} has to be a number, a vec(horizontal, vertical) or a vec(left, top, right, bottom).")
        };

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
