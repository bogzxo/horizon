using System.Numerics;

using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;
using Horizon.Rendering.Spriting;

using ValueType = Horizon.HIDL.Runtime.ValueType;

namespace Horizon.UI.Drawing;

/// <summary>
/// A picture made of frames for a UI to draw, an Aseprite file (its tags its animations) or a sheet with its definition,
/// read once and kept by its path. A <see cref="Components.DrawingPanel"/> draws its frames, <see cref="Mask"/> says what is
/// drawn on one. To a script it is <c>{ width, height, animations }</c> with <c>frames(name)</c>, <c>has(name)</c> and
/// <c>mask(name, frame)</c>, which is what an editor's extension gets out of <c>hex.sprite(path)</c>.
/// </summary>
public sealed class UISprite : IHostValue
{
    // Big enough for a character's every frame, trimmed to what is drawn on each
    private const int ATLAS_WIDTH = 2048, ATLAS_HEIGHT = 2048;

    private static readonly Dictionary<string, UISprite> opened = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock opening = new();

    private readonly Dictionary<string, string[]> keys = [];
    private readonly Dictionary<(string, int, int), UISpriteMask> masks = [];

    public ValueType Type { get; init; } = ValueType.Host;

    public string TypeName => "sprite";

    /// <summary>Where it was read from.</summary>
    public string Path { get; }

    /// <summary>The animations and every other sprite in it, by name.</summary>
    public SpriteSheetDefinition Sheet { get; }

    /// <summary>What its frames are drawn out of.</summary>
    public TextureAtlas Atlas { get; } = new(ATLAS_WIDTH, ATLAS_HEIGHT, shared: true) { Trim = true };

    /// <summary>How big a frame is in pixels, the first animation's.</summary>
    public Vector2 FrameSize { get; }

    private UISprite(string path, SpriteSheetDefinition sheet)
    {
        Path = path;
        Sheet = sheet;
        if (sheet.Sprites.FirstOrDefault() is { } first && sheet.TryGetSprite(first, null, out SpriteSource source))
            FrameSize = new Vector2(source.Width, source.Height);
    }

    /// <summary>The picture at a path, read the first time and kept. Throws with what went wrong.</summary>
    public static UISprite Open(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        lock (opening)
        {
            if (opened.TryGetValue(full, out UISprite? known))
                return known;

            if (!File.Exists(full))
                throw new HidlRuntimeException($"There is no '{path}' to draw.");

            return opened[full] = new UISprite(full, SpriteSheetDefinition.Open(full));
        }
    }

    /// <summary>Forgets every picture that was read, the next one asked for is read again (the art changed on disk).</summary>
    public static void ForgetAll()
    {
        lock (opening)
        {
            opened.Clear();
        }

        AsepriteDocument.Forget();
    }

    /// <summary>How many frames an animation has, 0 for one it hasn't got.</summary>
    public int FrameCount(string animation) => Sheet.FrameCount(animation);

    /// <summary>The key of a frame in the atlas, asked for the first time, null for an animation it hasn't got.</summary>
    public string? Key(string animation, int frame)
    {
        if (!keys.TryGetValue(animation, out string[]? found))
        {
            found = Sheet.TryGetSprite(animation, null, out SpriteSource source) ? Atlas.Request(source) : [];
            keys[animation] = found;
        }

        return found.Length == 0 ? null : found[Math.Clamp(frame, 0, found.Length - 1)];
    }

    /// <summary>What is drawn on a frame, a pixel solid where its alpha is at least <paramref name="threshold"/>. Kept.</summary>
    public UISpriteMask? Mask(string animation, int frame, int threshold = 32)
    {
        if (masks.TryGetValue((animation, frame, threshold), out UISpriteMask? known))
            return known;

        if (!Sheet.TryGetSprite(animation, null, out SpriteSource source) || source.ReadFrame(frame) is not { } pixels)
            return null;

        return masks[(animation, frame, threshold)] = new UISpriteMask(pixels, threshold);
    }

    public IRuntimeValue Get(string member) => member switch
    {
        "width" => new NumberValue(FrameSize.X),
        "height" => new NumberValue(FrameSize.Y),
        "path" => new StringValue(Path),
        "animations" => Natives.Object([.. Sheet.Sprites.Select(name => (name, (IRuntimeValue)new NumberValue(Sheet.FrameCount(name))))]),
        "frames" => Natives.Function("frames", args => new NumberValue(FrameCount(args.Text(0)))),
        "has" => Natives.Function("has", args => Values.Bool(Sheet.Has(args.Text(0)))),
        "mask" => Natives.Function("mask", args => (IRuntimeValue?)Mask(args.Text(0), args.Integer(1, 0), args.Integer(2, 32)) ?? Values.Null),
        _ => Values.Null
    };

    public void Set(string member, IRuntimeValue value) => throw new HidlRuntimeException("A sprite can't be changed.");

    public override string ToString() => $"sprite {System.IO.Path.GetFileName(Path)}";
}

/// <summary>
/// What is drawn on one frame of a <see cref="UISprite"/>, a pixel solid or not, in pixels from the top left corner of the
/// frame going down (the way pictures count). What a box tracer is made of without it touching a pixel itself, how many
/// solid pixels every column and row has and the box round what is solid, all of it or inside a part.
/// </summary>
public sealed class UISpriteMask : IHostValue
{
    private readonly bool[] solid;
    private readonly int[] columns, rows;

    public ValueType Type { get; init; } = ValueType.Host;

    public string TypeName => "mask";

    public int Width { get; }

    public int Height { get; }

    public UISpriteMask(SpritePixels pixels, int threshold)
    {
        (Width, Height) = (pixels.Width, pixels.Height);
        solid = new bool[Width * Height];
        columns = new int[Width];
        rows = new int[Height];

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (pixels.Data[(y * Width + x) * 4 + 3] < threshold) continue;
                solid[y * Width + x] = true;
                columns[x]++;
                rows[y]++;
            }
        }
    }

    public bool IsSolid(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height && solid[y * Width + x];

    /// <summary>The box round what is solid inside a part (inclusive, in pixels), and how many pixels, null for nothing there.</summary>
    public (int X0, int Y0, int X1, int Y1, int Count)? Bounds(int x0, int y0, int x1, int y1)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        x1 = Math.Min(Width - 1, x1); y1 = Math.Min(Height - 1, y1);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
        for (int y = y0; y <= y1; y++)
        {
            if (rows[y] == 0) continue;
            for (int x = x0; x <= x1; x++)
            {
                if (!solid[y * Width + x]) continue;
                count++;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        }

        return count == 0 ? null : (minX, minY, maxX, maxY, count);
    }

    private static IRuntimeValue Box((int X0, int Y0, int X1, int Y1, int Count)? box) => box is { } found
        ? Natives.Object(("x0", new NumberValue(found.X0)), ("y0", new NumberValue(found.Y0)), ("x1", new NumberValue(found.X1)),
            ("y1", new NumberValue(found.Y1)), ("count", new NumberValue(found.Count)))
        : Values.Null;

    public IRuntimeValue Get(string member) => member switch
    {
        "width" => new NumberValue(Width),
        "height" => new NumberValue(Height),
        "columns" => Natives.Function("columns", _ => Natives.List(columns.Select(count => (float)count))),
        "rows" => Natives.Function("rows", _ => Natives.List(rows.Select(count => (float)count))),
        "solid" => Natives.Function("solid", args => Values.Bool(IsSolid(args.Integer(0), args.Integer(1)))),
        "bounds" => Natives.Function("bounds", _ => Box(Bounds(0, 0, Width - 1, Height - 1))),
        "bounds_in" => Natives.Function("bounds_in", args => Box(Bounds(args.Integer(0), args.Integer(1), args.Integer(2), args.Integer(3)))),
        _ => Values.Null
    };

    public void Set(string member, IRuntimeValue value) => throw new HidlRuntimeException("A mask can't be changed.");

    public override string ToString() => $"mask {Width} x {Height}";
}
