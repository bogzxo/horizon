using System.Numerics;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Horizon.Rendering.Spriting;

// The half of the definition that knows about Aseprite files, and the definitions that aren't read from a sheet file at all.
public sealed partial class SpriteSheetDefinition
{
    private const string LEGACY_DEFINITION = "definition.hor";

    /// <summary>
    /// Makes a definition straight off an Aseprite file, with no sheet file in between. Every tag of the file is a sprite
    /// by the name of the tag whose frames are the frames of the tag (played the way the tag says, for as long as each
    /// frame says), and every slice is a sprite by the name of the slice, nine-slice border and all. Tags win where a tag
    /// and a slice are called the same. Throws (saying what is wrong) if the file can't be read.
    /// </summary>
    /// <param name="trim">
    /// Whether the see-through edges of every frame of a tag are left out of the atlas (see <see cref="SpriteSource.Trim"/>),
    /// which is what a character wants. Slices are never trimmed, they are the size somebody drew them.
    /// </param>
    public static SpriteSheetDefinition FromAseprite(string path, bool trim = true)
    {
        var document = AsepriteDocument.Load(path);
        var definition = new SpriteSheetDefinition(path);
        var image = new ImageDefinition { Path = document.Path, Document = document };

        foreach (var tag in document.Tags)
            definition._sprites.TryAdd(tag.Name, TagSprite(image, tag, 0, 0, document.Width, document.Height) with { Trim = trim });

        foreach (var slice in document.Slices)
            definition._sprites.TryAdd(slice.Name, SliceSprite(image, slice));

        return definition;
    }

    /// <summary>
    /// Makes a definition out of a folder with a sprite sheet and a definition.hor, the kind <see cref="SpriteSheet.LoadSpriteSheetFromDirectory"/>
    /// loads: a grid of cells with an animation to a row. Every animation is a sprite by its name, its frames following
    /// each other to the right. For drawing an old sheet out of an atlas like everything else. Throws if the folder makes no sense.
    /// </summary>
    public static SpriteSheetDefinition FromSpriteSheetDirectory(string directory, string file = LEGACY_DEFINITION)
    {
        string path = System.IO.Path.Combine(directory, file);
        if (!File.Exists(path))
            throw new FileNotFoundException($"The sprite definition '{path}' doesn't exist.");

        HIDLRuntime runtime = new() { BaseDirectory = directory, Output = null };
        var (success, message) = runtime.Evaluate(File.ReadAllText(path));
        if (!success)
            throw new Exception($"'{path}': {message}");

        if (runtime.UserScope.Lookup("sprite") is not ObjectValue { Properties: { } sprite })
            throw new Exception($"'{path}' has to declare an object called 'sprite'.");

        string sheet = sprite.TryGetValue("sprite_file", out var sheetFile) && sheetFile is StringValue named ? named.Value : "spritesheet.png";

        if (!sprite.TryGetValue("sprite_size", out var sizeValue) || sizeValue is not ObjectValue { Properties: { } size })
            throw new Exception($"'{path}': the sprite has to say its sprite_size.");

        int width = (int)Number(size.GetValueOrDefault("w")!, "sprite_size.w");
        int height = (int)Number(size.GetValueOrDefault("h")!, "sprite_size.h");

        var definition = new SpriteSheetDefinition(path);
        var image = new ImageDefinition { Path = System.IO.Path.Combine(directory, sheet), Size = new Vector2(width, height) };

        if (sprite.TryGetValue("animations", out var animationsValue) && animationsValue is ObjectValue { Properties: { } animations })
        {
            foreach (var (name, value) in animations)
            {
                if (value is not ObjectValue { Properties: { } animation }) continue;

                float Optional(string key, float otherwise) =>
                    animation.TryGetValue(key, out var number) ? Number(number, $"{name}.{key}") : otherwise;

                // A frame can go on over several cells of the sheet, the next one starts after them
                int span = 1 + (int)Optional("span", 0);

                definition._sprites.TryAdd(name, new SpriteDefinition(
                    image, 0,
                    (int)Optional("x", 0) * width,
                    (int)Optional("y", 0) * height,
                    width * span,
                    height,
                    Vector4.Zero, Vector4.Zero,
                    Math.Max(1, (int)Optional("length", 1)),
                    Optional("time", 0.1f),
                    default, null));
            }
        }

        return definition;
    }

    /// <summary>
    /// Makes a definition out of whatever a path turns out to be: an Aseprite file (see <see cref="FromAseprite"/>), a sheet
    /// file (see <see cref="Load"/>) or a folder with an old sprite sheet in it (see <see cref="FromSpriteSheetDirectory"/>).
    /// For content that says where its art is without the game caring which kind it is.
    /// </summary>
    public static SpriteSheetDefinition Open(string path)
    {
        if (AsepriteDocument.IsAseprite(path)) return FromAseprite(path);
        if (Directory.Exists(path)) return FromSpriteSheetDirectory(path);

        return Load(System.IO.Path.GetDirectoryName(path) ?? string.Empty, System.IO.Path.GetFileName(path));
    }

    /// <summary>
    /// Reads an image of a sheet file that is an Aseprite file. The canvas of the file is the image, and the file knows
    /// most of what a plain image has to be told:
    /// <code>
    /// let buttons = {
    ///     file: "Buttons.aseprite",
    ///     themes: { white: "White", dark: "Black" },      // the layer (or group of layers) every theme is drawn on
    ///     fallback: "white",
    ///     states: { hover: "Highlighted", pressed: 1 },   // the frame the other states are on, by its tag or its number
    ///     hide: ["NormalMap"],                            // layers that are never drawn
    ///     tags: true,                                     // every tag of the file as a sprite by its own name
    ///     slices: true,                                   // and every slice
    ///     sprites: {
    ///         button: { x: 16, y: 171, w: 32, h: 21, border: 6 },    // a rectangle of the canvas
    ///         panel:  { slice: "PanelA" },                           // a slice of the file, with its border
    ///         star:   { tag: "Activate", crop: true },               // the frames of a tag
    ///         heart:  { frame: "Full", layer: "Red" }                // on one frame of the file, and on one layer whatever the theme
    ///     }
    /// };
    /// </code>
    /// A layer that isn't any theme's is drawn for all of them the way the file was saved, which is what a background is.
    /// </summary>
    private void ReadAsepriteImage(string name, ImageDefinition image, Dictionary<string, IRuntimeValue> properties)
    {
        var document = image.Document = AsepriteDocument.Load(image.Path);

        if (properties.TryGetValue("themes", out var themesValue))
        {
            if (themesValue is not ObjectValue themes)
                throw new Exception($"{name}.themes has to be an object.");

            foreach (var (theme, layer) in themes.Properties)
            {
                if (layer is not StringValue layerName)
                    throw new Exception($"{name}.themes.{theme} has to be the name of a layer, the image is an Aseprite file.");
                if (!document.HasLayer(layerName.Value))
                    throw new Exception($"{name}.themes.{theme}: '{image.Path}' has no layer called '{layerName.Value}'. It has: {string.Join(", ", document.Layers.Select(l => l.Path))}.");

                image.ThemeLayers[theme] = layerName.Value;
                _themes.Add(theme);

                if (image.Fallback.Length == 0) image.Fallback = theme;
            }
        }

        if (properties.TryGetValue("fallback", out var fallback))
        {
            if (fallback is not StringValue fallbackName || !image.ThemeLayers.ContainsKey(fallbackName.Value))
                throw new Exception($"{name}.fallback has to be one of the themes of the image.");

            image.Fallback = fallbackName.Value;
        }

        if (properties.TryGetValue("hide", out var hide))
        {
            image.Hidden = hide switch
            {
                StringValue one => [one.Value],
                ListValue many => [.. Enumerable.Range(0, many.Count).Select(i => many[i] is StringValue layer ? layer.Value : throw new Exception($"{name}.hide has to be a list of layer names."))],
                _ => throw new Exception($"{name}.hide has to be the name of a layer or a list of them.")
            };
        }

        if (properties.TryGetValue("size", out var size))
            image.Size = Pair(size, $"{name}.size");

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

            foreach (var (state, frame) in states.Properties)
                image.StateFrames[state] = FrameOf(document, frame, $"{name}.states.{state}");
        }

        if (properties.TryGetValue("sprites", out var spritesValue))
        {
            if (spritesValue is not ObjectValue sprites)
                throw new Exception($"{name}.sprites has to be an object.");

            foreach (var (spriteName, value) in sprites.Properties)
            {
                if (value is not ObjectValue sprite)
                    throw new Exception($"Sprite '{spriteName}' has to be an object.");

                SpriteDefinition definition = ReadAsepriteSprite(image, spriteName, sprite.Properties);
                Add(spriteName, definition);

                // The same sprite in every other state of the image, which is the same place on another frame.
                // An animation is its frames already, it has no other states
                if (definition.ImageFrames is not null) continue;

                foreach (var (state, frame) in image.StateFrames)
                    Add($"{spriteName}_{state}", definition with { ImageFrame = frame });
            }
        }

        // Everything the file names itself, for whoever asks. What the sheet file wrote down by the same name stays
        string prefix = properties.TryGetValue("prefix", out var prefixValue) && prefixValue is StringValue text ? text.Value : string.Empty;

        if (properties.TryGetValue("tags", out var tags) && tags is BooleanValue { Value: true })
        {
            foreach (var tag in document.Tags)
                _sprites.TryAdd(prefix + tag.Name, TagSprite(image, tag, 0, 0, document.Width, document.Height));
        }

        if (properties.TryGetValue("slices", out var slices) && slices is BooleanValue { Value: true })
        {
            foreach (var slice in document.Slices)
                _sprites.TryAdd(prefix + slice.Name, SliceSprite(image, slice));
        }

        if (!properties.ContainsKey("sprites") && !properties.ContainsKey("tags") && !properties.ContainsKey("slices"))
            throw new Exception($"Image '{name}' names no sprites. Give it an object called 'sprites', or set tags or slices to true.");
    }

    /// <summary>
    /// Helper method to read a sprite of an Aseprite image, see <see cref="ReadAsepriteImage"/>.
    /// </summary>
    private static SpriteDefinition ReadAsepriteSprite(ImageDefinition image, string name, Dictionary<string, IRuntimeValue> properties)
    {
        var document = image.Document!;

        float Optional(string key, float otherwise) =>
            properties.TryGetValue(key, out var value) ? Number(value, $"{name}.{key}") : otherwise;

        // Where it is. A slice says, anything the sprite says itself goes over that
        int x = 0, y = 0;
        int width = image.Size.X > 0 ? (int)image.Size.X : document.Width;
        int height = image.Size.Y > 0 ? (int)image.Size.Y : document.Height;
        Vector4 border = Vector4.Zero;
        Vector2? pivot = null;

        if (properties.TryGetValue("slice", out var sliceValue))
        {
            if (sliceValue is not StringValue sliceName || !document.TryGetSlice(sliceName.Value, out var slice))
                throw new Exception($"{name}.slice has to be the name of a slice of '{image.Path}'.");

            (x, y, width, height, border, pivot) = (slice.X, slice.Y, slice.Width, slice.Height, slice.Border, slice.Pivot);
        }

        if (properties.TryGetValue("cell", out var cellValue))
        {
            if (image.Cell == default)
                throw new Exception($"Sprite '{name}' is written by its cell, but its image has no cell size.");

            Vector2 cell = Pair(cellValue, $"{name}.cell");
            x = (int)(image.Origin.X + cell.X * image.Cell.X);
            y = (int)(image.Origin.Y + cell.Y * image.Cell.Y);
        }

        x = (int)Optional("x", x);
        y = (int)Optional("y", y);
        width = (int)Optional("w", width);
        height = (int)Optional("h", height);

        ReadFrames(name, properties, out int frames, out (int X, int Y)[]? positions, out (int X, int Y) step);

        var sprite = new SpriteDefinition(
            image, 0, x, y, width, height,
            EdgesOf(name, properties, "border") ?? border,
            EdgesOf(name, properties, "content") ?? Vector4.Zero,
            frames,
            Optional("time", 0.1f),
            step,
            positions,
            Crop: properties.TryGetValue("crop", out var crop) && crop is BooleanValue { Value: true },
            Trim: properties.TryGetValue("trim", out var trim) && trim is BooleanValue { Value: true },
            Pivot: pivot);

        if (properties.TryGetValue("layer", out var layerValue))
        {
            if (layerValue is not StringValue layerName || !document.HasLayer(layerName.Value))
                throw new Exception($"{name}.layer has to be the name of a layer of '{image.Path}'. It has: {string.Join(", ", document.Layers.Select(l => l.Path))}.");

            sprite = sprite with { Layer = layerName.Value };
        }

        if (properties.TryGetValue("frame", out var frameValue))
            sprite = sprite with { ImageFrame = FrameOf(document, frameValue, $"{name}.frame") };

        if (properties.TryGetValue("tag", out var tagValue))
        {
            if (tagValue is not StringValue tagName || !document.TryGetTag(tagName.Value, out var tag))
                throw new Exception($"{name}.tag has to be the name of a tag of '{image.Path}'. It has: {string.Join(", ", document.Tags.Select(t => t.Name))}.");

            var tagged = TagSprite(image, tag, x, y, width, height, Math.Max(0, (int)Optional("skip", 0)));
            sprite = sprite with
            {
                Frames = tagged.Frames,
                ImageFrames = tagged.ImageFrames,
                Loops = tagged.Loops,
                Step = default,
                FramePositions = null,

                // A sprite that says how long its frames take overrules what the file says
                FrameTime = properties.ContainsKey("time") ? sprite.FrameTime : tagged.FrameTime,
                FrameTimes = properties.ContainsKey("time") ? null : tagged.FrameTimes
            };
        }

        return sprite;
    }

    /// <summary>
    /// Helper method to make a sprite out of a tag: a part of the canvas over the frames of the tag, in the order and for
    /// as long as the tag plays them.
    /// </summary>
    /// <param name="skip">How many frames at the start of the tag are left out, as long as that leaves any.</param>
    private static SpriteDefinition TagSprite(ImageDefinition image, AsepriteTag tag, int x, int y, int width, int height, int skip = 0)
    {
        var document = image.Document!;
        int[] sequence = tag.Sequence();
        if (skip > 0 && skip < sequence.Length) sequence = sequence[skip..];

        float[] times = new float[sequence.Length];
        bool even = true;
        for (int i = 0; i < times.Length; i++)
        {
            times[i] = document.DurationOf(sequence[i]);
            even &= times[i] == times[0];
        }

        return new SpriteDefinition(
            image, 0, x, y, width, height, Vector4.Zero, Vector4.Zero,
            Math.Max(1, sequence.Length),
            times.Length > 0 ? times[0] : 0.1f,
            default, null,
            ImageFrame: sequence.Length > 0 ? sequence[0] : 0,
            ImageFrames: sequence,
            FrameTimes: even ? null : times,
            Loops: tag.Loops);
    }

    private static SpriteDefinition SliceSprite(ImageDefinition image, AsepriteSlice slice) => new(
        image, 0, slice.X, slice.Y, slice.Width, slice.Height, slice.Border, Vector4.Zero, 1, 0.1f, default, null, Pivot: slice.Pivot);

    /// <summary>
    /// Helper method to read a frame of an Aseprite file, written as its number (from 0) or as the name of a tag, which
    /// is the first frame of the tag.
    /// </summary>
    private static int FrameOf(AsepriteDocument document, IRuntimeValue value, string what)
    {
        switch (value)
        {
            case NumberValue number when number.Value >= 0 && number.Value < document.FrameCount:
                return (int)number.Value;

            case StringValue tagName when document.TryGetTag(tagName.Value, out var tag):
                return tag.From;

            default:
                throw new Exception($"{what} has to be a frame of '{document.Path}' (it has {document.FrameCount}) or the name of one of its tags ({string.Join(", ", document.Tags.Select(t => t.Name))}).");
        }
    }
}
