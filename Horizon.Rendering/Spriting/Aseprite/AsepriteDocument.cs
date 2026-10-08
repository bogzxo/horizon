using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;

using AsepriteDotNet.Aseprite;
using AsepriteDotNet.Aseprite.Types;
using AsepriteDotNet.IO;

using Bogz.Logging;

namespace Horizon.Rendering.Spriting;

/// <summary>
/// The way the frames of a tag are played, as Aseprite has it.
/// </summary>
public enum AsepriteDirection
{
    Forward,
    Reverse,
    PingPong,
    PingPongReverse
}

/// <summary>
/// A tag of an Aseprite file, which is a stretch of its frames with a name. This is what an animation is.
/// </summary>
/// <param name="From">The first frame of the tag, counted from 0.</param>
/// <param name="To">The last frame of the tag.</param>
/// <param name="Repeat">How many times it plays, 0 for round and round until somebody stops it.</param>
public readonly record struct AsepriteTag(string Name, int From, int To, AsepriteDirection Direction, int Repeat)
{
    /// <summary>Whether the animation goes round and round rather than playing so many times and stopping.</summary>
    public bool Loops => Repeat == 0;

    /// <summary>
    /// The frames of the file in the order the tag plays them. A tag that plays there and back again (ping pong) is
    /// written out the whole way, without showing the frames at either end twice.
    /// </summary>
    public int[] Sequence()
    {
        int count = To - From + 1;
        if (count < 1) return [];

        var forward = new int[count];
        for (int i = 0; i < count; i++) forward[i] = From + i;

        switch (Direction)
        {
            case AsepriteDirection.Reverse:
                Array.Reverse(forward);
                return forward;

            case AsepriteDirection.PingPong or AsepriteDirection.PingPongReverse when count > 2:
                if (Direction == AsepriteDirection.PingPongReverse) Array.Reverse(forward);

                // There, and back again without the two ends
                var both = new int[count * 2 - 2];
                forward.CopyTo(both, 0);
                for (int i = 1; i < count - 1; i++) both[count - 1 + i] = forward[count - 1 - i];
                return both;

            case AsepriteDirection.PingPongReverse:
                Array.Reverse(forward);
                return forward;

            default:
                return forward;
        }
    }
}

/// <summary>
/// A slice of an Aseprite file, which is a rectangle of its canvas with a name. This is what a sprite of a sheet is.
/// </summary>
/// <param name="Border">
/// How much of every edge (left, top, right, bottom) keeps its size when the slice is stretched, for a slice that was
/// given a centre in Aseprite (a nine-slice). Zero for one that wasn't.
/// </param>
/// <param name="Pivot">The point the slice turns around, in pixels from its top left. Null for a slice that has none.</param>
public readonly record struct AsepriteSlice(string Name, int X, int Y, int Width, int Height, Vector4 Border, Vector2? Pivot);

/// <summary>
/// A layer of an Aseprite file.
/// </summary>
/// <param name="Path">The name of the layer after the names of the groups it is in, "White/Main".</param>
/// <param name="IsVisible">Whether the layer was showing when the file was saved.</param>
/// <param name="IsGroup">Whether this is a group, which draws nothing itself.</param>
/// <param name="Depth">How many groups the layer is inside of.</param>
public readonly record struct AsepriteLayer(string Name, string Path, bool IsVisible, bool IsGroup, int Depth);

/// <summary>
/// An Aseprite file (.ase or .aseprite), read once and kept. It says what the file has (frames, tags, layers and slices)
/// and paints any of its frames with any of its layers. Nothing in here touches the GPU, so it can be used from any thread
/// and before the engine is up: pair it with a <see cref="TextureAtlas"/> or a <see cref="SpriteSheetDefinition"/> to draw it.
/// Files are read with AsepriteDotNet (https://github.com/AristurtleDev/AsepriteDotNet).
/// </summary>
public sealed class AsepriteDocument
{
    // How many painted frames are kept around. Every sprite of a sheet comes off the same frame, no point painting it for each
    private const int RENDER_CACHE = 4;

    private static readonly ConcurrentDictionary<string, AsepriteDocument> cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly AsepriteFile _file;
    private readonly DateTime _written;

    // The layers bottom to top, with who their group is (-1 for none). Cels find their layer through the map
    private readonly AsepriteDotNet.Aseprite.Types.AsepriteLayer[] _rawLayers;
    private readonly int[] _parents;
    private readonly Dictionary<AsepriteDotNet.Aseprite.Types.AsepriteLayer, int> _layerIndex = new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<string, AsepriteTag> _tagsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AsepriteSlice> _slicesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly float[] _durations;

    private readonly Lock _renderLock = new();
    private readonly List<(int Frame, string Layers, byte[] Pixels)> _rendered = [];
    private readonly Dictionary<string, bool[]> _visibility = [];

    /// <summary>The file this was read from.</summary>
    public string Path { get; }

    /// <summary>The size of the canvas in pixels, every frame is this big.</summary>
    public int Width { get; }
    public int Height { get; }

    public int FrameCount => _durations.Length;

    /// <summary>The tags of the file in the order Aseprite lists them.</summary>
    public IReadOnlyList<AsepriteTag> Tags { get; }

    /// <summary>The slices of the file. Two slices can have the same name, <see cref="TryGetSlice"/> finds the first.</summary>
    public IReadOnlyList<AsepriteSlice> Slices { get; }

    /// <summary>The layers of the file from the bottom one up, groups included.</summary>
    public IReadOnlyList<AsepriteLayer> Layers { get; }

    /// <summary>
    /// Test if a file is an Aseprite file, going by its name.
    /// </summary>
    public static bool IsAseprite(string path) =>
        path.EndsWith(".ase", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".aseprite", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads an Aseprite file, or hands back the one that was read before if the file hasn't changed since. Thread-safe.
    /// Throws (saying what is wrong) if the file isn't there or can't be read.
    /// </summary>
    public static AsepriteDocument Load(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException($"The Aseprite file '{path}' doesn't exist.");

        DateTime written = File.GetLastWriteTimeUtc(full);
        if (cache.TryGetValue(full, out var known) && known._written == written)
            return known;

        AsepriteFile file;
        try
        {
            file = AsepriteFileLoader.FromFile(full, preMultiplyAlpha: false);
        }
        catch (Exception e)
        {
            throw new Exception($"'{path}' can't be read as an Aseprite file: {e.Message}", e);
        }

        foreach (string warning in file.Warnings)
        {
            // Every file Aseprite saves has a colour profile and the reader says so every time. Nothing is done with it here either
            if (!warning.Contains("Color Profile", StringComparison.OrdinalIgnoreCase))
                Log.Warning($"[Aseprite] '{path}': {warning}");
        }

        return cache[full] = new AsepriteDocument(full, file, written);
    }

    /// <summary>
    /// Reads an Aseprite file like <see cref="Load"/> does, but logs what went wrong instead of throwing.
    /// </summary>
    public static bool TryLoad(string path, out AsepriteDocument document)
    {
        try
        {
            document = Load(path);
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"[Aseprite] {e.Message}");
            document = null!;
            return false;
        }
    }

    /// <summary>
    /// Forgets every file that was read, for when the memory is wanted back. A file that changed on disk is read again
    /// by itself, nobody has to call this for that.
    /// </summary>
    public static void Forget() => cache.Clear();

    private AsepriteDocument(string path, AsepriteFile file, DateTime written)
    {
        _file = file;
        _written = written;

        Path = path;
        Width = file.CanvasWidth;
        Height = file.CanvasHeight;

        _durations = new float[file.FrameCount];
        for (int i = 0; i < _durations.Length; i++)
            _durations[i] = (float)file.Frames[i].Duration.TotalSeconds;

        (_rawLayers, _parents, Layers) = ReadLayers(file);
        for (int i = 0; i < _rawLayers.Length; i++) _layerIndex[_rawLayers[i]] = i;

        var tags = new List<AsepriteTag>();
        foreach (var tag in file.Tags)
        {
            // Aseprite doesn't stop anybody from tagging frames that were deleted since
            int from = Math.Clamp(tag.From, 0, Math.Max(0, FrameCount - 1));
            int to = Math.Clamp(tag.To, from, Math.Max(0, FrameCount - 1));

            var read = new AsepriteTag(tag.Name, from, to, (AsepriteDirection)(int)tag.LoopDirection, Math.Max(0, tag.Repeat));
            tags.Add(read);
            _tagsByName.TryAdd(read.Name, read);
        }
        Tags = tags;

        var slices = new List<AsepriteSlice>();
        foreach (var slice in file.Slices)
        {
            if (slice.Keys.Length == 0) continue;

            var read = ReadSlice(slice);
            slices.Add(read);
            _slicesByName.TryAdd(read.Name, read);
        }
        Slices = slices;
    }

    /// <summary>
    /// Helper method to work out who the group of every layer is. The file only says how deep a layer sits, its group
    /// is the last layer before it that sits one less deep.
    /// </summary>
    private static (AsepriteDotNet.Aseprite.Types.AsepriteLayer[], int[], AsepriteLayer[]) ReadLayers(AsepriteFile file)
    {
        var raw = file.Layers.ToArray();
        var parents = new int[raw.Length];
        var described = new AsepriteLayer[raw.Length];

        // The file lists a group before the layers inside it
        var open = new List<int>();
        for (int i = 0; i < raw.Length; i++)
        {
            int depth = Math.Max(0, raw[i].ChildLevel);
            while (open.Count > depth) open.RemoveAt(open.Count - 1);

            parents[i] = open.Count > 0 ? open[^1] : -1;

            string path = parents[i] >= 0 ? described[parents[i]].Path + "/" + raw[i].Name : raw[i].Name;
            bool group = raw[i] is AsepriteGroupLayer;
            described[i] = new AsepriteLayer(raw[i].Name, path, raw[i].IsVisible, group, open.Count);

            if (group) open.Add(i);
        }

        return (raw, parents, described);
    }

    private static AsepriteSlice ReadSlice(AsepriteDotNet.Aseprite.Types.AsepriteSlice slice)
    {
        // A slice can move about from frame to frame, where it starts out is what it is
        var key = slice.Keys[0];
        var bounds = key.Bounds;

        Vector4 border = Vector4.Zero;
        if (slice.IsNinePatch)
        {
            var centre = key.CenterBounds;
            border = new Vector4(
                centre.X,
                centre.Y,
                Math.Max(0, bounds.Width - centre.X - centre.Width),
                Math.Max(0, bounds.Height - centre.Y - centre.Height));
        }

        Vector2? pivot = slice.HasPivot ? new Vector2(key.Pivot.X, key.Pivot.Y) : null;
        return new AsepriteSlice(slice.Name, bounds.X, bounds.Y, bounds.Width, bounds.Height, border, pivot);
    }

    public bool TryGetTag(string name, out AsepriteTag tag) => _tagsByName.TryGetValue(name, out tag);

    public bool TryGetSlice(string name, out AsepriteSlice slice) => _slicesByName.TryGetValue(name, out slice);

    /// <summary>
    /// Test if the file has a layer (or a group) by this name, or by this path through its groups.
    /// </summary>
    public bool HasLayer(string name)
    {
        foreach (var layer in Layers)
        {
            if (layer.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || layer.Path.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// How long a frame is shown for, in seconds.
    /// </summary>
    public float DurationOf(int frame) => frame >= 0 && frame < _durations.Length ? _durations[frame] : 0.0f;

    /// <summary>
    /// Paints a frame the way Aseprite would show it, with the layers that are asked for. Thread-safe.
    /// </summary>
    /// <returns>The whole canvas as RGBA from the top left, 4 bytes a pixel. Nobody is to write to it, it is kept and handed out again.</returns>
    public byte[] Render(int frame, LayerSelection layers = default)
    {
        frame = Math.Clamp(frame, 0, Math.Max(0, FrameCount - 1));
        string spec = layers.Spec ?? string.Empty;

        lock (_renderLock)
        {
            for (int i = 0; i < _rendered.Count; i++)
            {
                if (_rendered[i].Frame == frame && _rendered[i].Layers == spec)
                    return _rendered[i].Pixels;
            }

            byte[] pixels = Paint(frame, VisibilityFor(layers, spec));

            if (_rendered.Count == RENDER_CACHE) _rendered.RemoveAt(0);
            _rendered.Add((frame, spec, pixels));
            return pixels;
        }
    }

    /// <summary>
    /// The smallest rectangle that holds everything that is drawn on these frames inside of a part of the canvas, in
    /// pixels from the top left of the canvas. Empty (all zero) if nothing is drawn there at all.
    /// </summary>
    public (int X, int Y, int Width, int Height) BoundsOf(ReadOnlySpan<int> frames, LayerSelection layers, int x, int y, int width, int height)
    {
        int left = Math.Max(0, x), top = Math.Max(0, y);
        int right = Math.Min(Width, x + width), bottom = Math.Min(Height, y + height);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        foreach (int frame in frames)
        {
            byte[] pixels = Render(frame, layers);

            for (int row = top; row < bottom; row++)
            {
                for (int column = left; column < right; column++)
                {
                    if (pixels[(row * Width + column) * 4 + 3] == 0) continue;

                    minX = Math.Min(minX, column); maxX = Math.Max(maxX, column);
                    minY = Math.Min(minY, row); maxY = Math.Max(maxY, row);
                }
            }
        }

        return maxX < 0 ? default : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>
    /// Helper method to work out which layers are drawn for a selection. A layer is drawn if it and every group it is
    /// in is showing, where the selection gets the last word over what the file says.
    /// </summary>
    private bool[] VisibilityFor(LayerSelection layers, string spec)
    {
        if (_visibility.TryGetValue(spec, out var known)) return known;

        var own = new bool[_rawLayers.Length];
        for (int i = 0; i < own.Length; i++)
            own[i] = layers.Wants(Layers[i].Name, Layers[i].Path) ?? _rawLayers[i].IsVisible;

        var visible = new bool[own.Length];
        for (int i = 0; i < visible.Length; i++)
        {
            bool shown = own[i];
            for (int parent = _parents[i]; shown && parent >= 0; parent = _parents[parent]) shown = own[parent];

            visible[i] = shown;
        }

        return _visibility[spec] = visible;
    }

    /// <summary>
    /// Helper method to paint a frame, one cel over the other from the bottom layer up.
    /// </summary>
    private byte[] Paint(int frame, bool[] visible)
    {
        byte[] canvas = new byte[Width * Height * 4];
        if (frame >= _file.FrameCount) return canvas;

        foreach (var placed in _file.Frames[frame].Cels)
        {
            // A linked cel is another frame's cel shown again
            var cel = placed;
            while (cel is AsepriteLinkedCel linked) cel = linked.Cel;

            if (cel is not AsepriteImageCel image) continue;
            if (!_layerIndex.TryGetValue(placed.Layer, out int layer) || !visible[layer] || _rawLayers[layer].IsReferenceLayer) continue;

            // How see-through the cel and its layer are, multiplied together. The groups around it are left out of it:
            // files saved before Aseprite let a group be see-through have nothing (which reads as 0) where that would be
            int opacity = cel.Opacity * _rawLayers[layer].Opacity / 255;

            if (opacity <= 0) continue;

            Blit(canvas, MemoryMarshal.AsBytes(image.Pixels), image.Size.Width, image.Size.Height, cel.Location.X, cel.Location.Y, opacity);
        }

        return canvas;
    }

    /// <summary>
    /// Helper method to put the pixels of a cel onto the canvas, over whatever is there. Every layer is blended the plain
    /// way (the blend modes of Aseprite are for painting with, art that is drawn with them is flattened before it is used).
    /// </summary>
    private void Blit(byte[] canvas, ReadOnlySpan<byte> cel, int celWidth, int celHeight, int atX, int atY, int opacity)
    {
        int fromX = Math.Max(0, -atX), fromY = Math.Max(0, -atY);
        int toX = Math.Min(celWidth, Width - atX), toY = Math.Min(celHeight, Height - atY);

        for (int y = fromY; y < toY; y++)
        {
            int source = (y * celWidth + fromX) * 4;
            int target = ((atY + y) * Width + atX + fromX) * 4;

            for (int x = fromX; x < toX; x++, source += 4, target += 4)
            {
                int alpha = cel[source + 3] * opacity / 255;
                if (alpha == 0) continue;

                int under = canvas[target + 3];
                if (alpha == 255 || under == 0)
                {
                    canvas[target] = cel[source];
                    canvas[target + 1] = cel[source + 1];
                    canvas[target + 2] = cel[source + 2];
                    canvas[target + 3] = (byte)alpha;
                    continue;
                }

                // One see-through pixel over another, neither of them premultiplied
                int rest = under * (255 - alpha) / 255;
                int both = alpha + rest;

                canvas[target] = (byte)((cel[source] * alpha + canvas[target] * rest) / both);
                canvas[target + 1] = (byte)((cel[source + 1] * alpha + canvas[target + 1] * rest) / both);
                canvas[target + 2] = (byte)((cel[source + 2] * alpha + canvas[target + 2] * rest) / both);
                canvas[target + 3] = (byte)both;
            }
        }
    }
}
