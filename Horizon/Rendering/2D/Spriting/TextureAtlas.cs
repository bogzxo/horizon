using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Logging;

using Horizon.Engine;
using Horizon.Graphics;



namespace Horizon.Rendering.Spriting;

/// <summary>
/// Where something is in an atlas, in texels from its top left. <paramref name="Position"/> and <paramref name="Size"/> are the texels that were
/// kept. An atlas that trims (see <see cref="TextureAtlas.Trim"/>) keeps only the opaque part of a frame, and then
/// <paramref name="Offset"/> says where that part sits in the frame as it was asked for and <paramref name="FrameSize"/>
/// how big that frame was, so whoever draws it can put it back where it belongs.
/// </summary>
public readonly record struct AtlasRegion(Vector2 Position, Vector2 Size, Vector2 Offset, Vector2 FrameSize)
{
    public AtlasRegion(Vector2 position, Vector2 size) : this(position, size, Vector2.Zero, size) { }

    /// <summary>Whether less than the whole frame was kept.</summary>
    public bool Trimmed => Offset != Vector2.Zero || Size != FrameSize;
}

/// <summary>
/// The frames of one sprite in an atlas, by the names they go by, with where each of them is once the atlas has it.
/// Whatever is in an atlas stays where it is for good, so a frame is only ever looked up by its name once. After
/// that it is an array read, where it used to be the hashing of a string as long as a file path for every sprite
/// on every tick.
/// </summary>
internal sealed class AtlasFrames(string[] keys)
{
    public readonly string[] Keys = keys;

    private readonly AtlasRegion[] regions = new AtlasRegion[keys.Length];
    private readonly bool[] known = new bool[keys.Length];

    /// <summary>Where a frame is in the atlas, false while its art hasn't been put in yet.</summary>
    public bool TryGet(TextureAtlas atlas, int frame, out AtlasRegion region)
    {
        if (Volatile.Read(ref known[frame]))
        {
            region = regions[frame];
            return true;
        }

        if (!atlas.TryGet(Keys[frame], out region))
            return false;

        // The region first and then the word that it is there, for whoever reads it from the other thread
        regions[frame] = region;
        Volatile.Write(ref known[frame], true);
        return true;
    }
}

/// <summary>
/// One texture that pieces of any number of images are stitched into, so that everything showing them can be drawn
/// together (see <see cref="SpriteItem"/>) instead of once for every image.
/// Only what is asked for with <see cref="Request"/> goes in, a sheet of thousands of sprites costs nothing but the ones
/// that are used. Asking can be done from any thread and at any time, the art shows up in the atlas the next time
/// <see cref="Update"/> runs on the render thread. Whatever is in the atlas stays where it is for good, so the
/// position of a region never has to be looked up twice.
/// </summary>
public sealed class TextureAtlas : IDisposable
{
    private const int PADDING = 2;          // Texels kept clear between two regions, so neither bleeds into the other

    /// <summary>
    /// Whether what is in the atlas is smoothed when it is drawn bigger or smaller than it is. Pixel art wants
    /// this off (which it is), art that was drawn big to be shown small wants it on. Set before anything is
    /// asked of the atlas, the texture is made the way this says the first time it is needed.
    /// </summary>
    public bool Smooth { get; set; }
    private const int MAX_HEIGHT = 16384;   // As tall as it is allowed to grow, or what the card takes if that's less

    private readonly record struct Pending(string Key, string Path, int X, int Y, int Width, int Height, string? Layer, int Most = 0);

    // The top edge of what's been packed, left to right, as a run of flat segments. A new region goes on the
    // lowest segment it fits on (the skyline packer), which fills the holes rows would leave
    private readonly record struct Segment(int X, int Y, int Width);

    private readonly ConcurrentQueue<Pending> _pending = new();
    private readonly ConcurrentDictionary<string, byte> _requested = new();

    // The frames of every sprite that was asked for, so asking again is one look up, see Frames
    private readonly ConcurrentDictionary<SpriteSource, AtlasFrames> _frames = new();
    private readonly List<Segment> _skyline = [];
    private long _used;
    private int _placed;

    // Replaced wholesale whenever regions are added, so it can be read from any thread without a lock
    private volatile Dictionary<string, AtlasRegion> _regions = [];

    // A piece of art cut out of its picture and given a place, waiting to go up. Only its own pixels, RGBA from the
    // top left, nothing between the rows
    private readonly record struct Cut(int X, int Y, int Width, int Height, byte[] Pixels);

    private readonly int _width;
    private int _height;
    private readonly bool _shared;

    // The texture a grown atlas left behind. Whoever drew with it this frame still points at it, so it goes the
    // next time round rather than from under them
    private Texture _retired = Texture.Invalid;

    // What has been packed (Pack) and not yet sent up, the pieces themselves, the regions they came to and whether
    // the atlas grew for them. Everything that packs or sends up holds the lock, the cuts and the skyline are one
    // thing. There is no copy of the atlas on the CPU, the texture is the atlas, a piece lives here from being cut
    // until it has gone up and then it's garbage, so an atlas with its art up costs nothing but its texture
    private readonly object _packing = new();
    private readonly List<Cut> _cuts = [];
    private Dictionary<string, AtlasRegion>? _packed;
    private bool _grew;

    // The worker packing what was asked for, for an atlas that PacksOnWorker
    private Task? _worker;

    /// <summary>
    /// The texture everything is stitched into. It is replaced by a bigger one when the atlas runs out of room,
    /// so ask for it every time rather than keeping it.
    /// </summary>
    public Texture Texture { get; private set; } = Texture.Invalid;

    /// <summary>
    /// Whether frames are cut down to their opaque pixels before they go in. A character drawn in the middle of a
    /// 128 by 128 frame is mostly air, and the air takes room. What's cut off is remembered on the region
    /// (<see cref="AtlasRegion.Offset"/>), whoever draws through <see cref="Sprite"/> or the UI's image gets it
    /// back where it was. Not for art that's drawn by its edges (nine slices), those want the whole frame.
    /// </summary>
    public bool Trim { get; init; }

    /// <summary>
    /// Whether what is asked for is packed on a worker instead of on the render thread as it's drawn, and sent up
    /// the next time round once it's done. Asking for a few hundred frames at once (a fighter's portrait, all of a
    /// character) is then no hitch, the price being that they turn up a frame or few late. Whoever has to have them
    /// on the first frame calls <see cref="Pack"/> as well.
    /// </summary>
    public bool PacksOnWorker { get; init; }

    /// <summary>How much of the atlas holds something, 0 to 1, padding included as in use.</summary>
    public float Occupancy => _width * (long)_height == 0 ? 0.0f : (float)_used / (_width * (long)_height);

    /// <summary>How many regions are in it.</summary>
    public int Count => _regions.Count;

    /// <summary>
    /// The size of the atlas in texels, only its height ever changes.
    /// </summary>
    public Vector2 Size => new(_width, _height);

    /// <param name="shared">
    /// Whether the atlas outlives the scene that made it. One that's shared belongs to the engine and is only let
    /// go of by <see cref="Dispose"/>, for art that several scenes show (the fighters of a menu, a select screen and a fight).
    /// </param>
    /// <param name="height">How tall it starts out. It grows, doubling, as it fills (see <see cref="TryPlace"/>), so this is only what an atlas with little in it costs.</param>
    public TextureAtlas(int width = 1024, int height = 256, bool shared = false)
    {
        _width = width;
        _height = height;
        _shared = shared;
        _skyline.Add(new Segment(0, 0, width));
    }

    /// <summary>
    /// Asks for a part of an image to be put into the atlas, under a name of the caller's choosing. Thread-safe.
    /// Asking for a name again does nothing, the first one to ask decides what it is.
    /// </summary>
    /// <param name="x">The left edge of the part, in pixels from the left of the image.</param>
    /// <param name="y">The top edge of the part, in pixels from the top of the image.</param>
    /// <param name="layer">One layer of an Aseprite file on its own, null for the picture as it looks.</param>
    /// <param name="most">
    /// How many pixels a side it is put in at the most, brought down to fit (a picture shown small, a thumbnail of a
    /// picture far too big for the atlas), 0 for as it is.
    /// </param>
    public void Request(string key, string imagePath, int x, int y, int width, int height, string? layer = null, int most = 0)
    {
        if (_requested.TryAdd(key, 0))
        {
            _pending.Enqueue(new Pending(key, imagePath, x, y, width, height, layer, most));
        }
    }

    /// <summary>
    /// The name a part of an image goes by when nobody gives it one. The same part asked for twice (by a UI and by a
    /// sprite, say) is only put in once this way.
    /// </summary>
    public static string KeyFor(string imagePath, int x, int y, int width, int height, string? layer = null) =>
        layer is null ? $"{imagePath}|{x},{y},{width},{height}" : $"{imagePath}|{layer}|{x},{y},{width},{height}";

    /// <summary>
    /// Asks for a sprite of a <see cref="SpriteSheetDefinition"/> to be put into the atlas, every frame of it. Thread-safe.
    /// </summary>
    /// <returns>
    /// The names its frames go by in the atlas, in order, to look them up with <see cref="TryGet"/>. The same array
    /// every time the same sprite is asked for, so read it and leave it be.
    /// </returns>
    public string[] Request(in SpriteSource sprite) => Frames(sprite).Keys;

    /// <summary>
    /// Asks for a sprite the way <see cref="Request(in SpriteSource)"/> does and hands back its frames as something
    /// that remembers where each of them ended up. Made once a sprite. A fighter changes animation a few times a
    /// second, and every change used to write the name of every frame out again, forty odd characters apiece,
    /// to ask for art that had been in the atlas since the fight began. Thread-safe.
    /// </summary>
    internal AtlasFrames Frames(in SpriteSource sprite)
    {
        if (_frames.TryGetValue(sprite, out AtlasFrames? known))
            return known;

        string[] keys = new string[Math.Max(sprite.Frames, 1)];
        for (int frame = 0; frame < keys.Length; frame++)
        {
            // Wherever the sheet says the frame is, to the right of the one before unless it says otherwise
            (int x, int y) = sprite.FrameAt(frame);

            keys[frame] = KeyFor(sprite.Path, x, y, sprite.Width, sprite.Height, sprite.Layer);
            Request(keys[frame], sprite.Path, x, y, sprite.Width, sprite.Height, sprite.Layer);
        }

        return _frames.GetOrAdd(sprite, new AtlasFrames(keys));
    }

    /// <summary>
    /// Whether anything that was asked for is still waiting for <see cref="Update"/> to put it in. Thread-safe.
    /// </summary>
    public bool HasPending => !_pending.IsEmpty;

    /// <summary>
    /// Finds where a piece of art is in the atlas, false if it wasn't asked for or hasn't been put in yet. Thread-safe.
    /// </summary>
    public bool TryGet(string key, out AtlasRegion region) => _regions.TryGetValue(key, out region);

    /// <summary>
    /// Puts everything that was asked for since the last time into the atlas. Has to be called on the render thread.
    /// Whatever was packed ahead (<see cref="Pack"/>) only has to be sent up.
    /// </summary>
    /// <returns>Whether anything was added.</returns>
    public bool Update()
    {
        if (_retired.Handle != 0)
        {
            GameEngine.Instance.ObjectManager.Textures.Remove(_retired);
            _retired = Texture.Invalid;
        }

        if (_pending.IsEmpty && _packed is null && Texture.Handle != 0) return false;

        // Handed to a worker, what it comes to goes up once it's done
        if (PacksOnWorker && !_pending.IsEmpty && _worker is not { IsCompleted: false }) _worker = Task.Run(Pack);
        if (PacksOnWorker && _packed is null && Texture.Handle != 0) return false;

        // Being packed on a worker right now (see Pack), what that comes to goes up next time. Waiting for it would
        // be the hitch packing on a worker is there to save, whoever can't do without it calls Pack first
        if (!Monitor.TryEnter(_packing)) return false;

        long started = Horizon.Core.Diagnostics.SlowRender.Start();
        try
        {
            bool added = (!PacksOnWorker && PackPending()) | (_packed is not null);

            Upload(_grew);
            _grew = false;

            // Only now, the regions are of no use to anyone before the texture has them
            if (_packed is { } regions) _regions = regions;
            _packed = null;

            // What was cut out of the pictures is in the atlas now, the pictures themselves can go
            if (added) SpriteImages.Trim();

            Horizon.Core.Diagnostics.SlowRender.Stop(started, "a texture atlas, packing and sending up");
            return added;
        }
        finally
        {
            Monitor.Exit(_packing);
        }
    }

    /// <summary>
    /// Packs everything that was asked for so far, each piece cut out of its picture and given its place, without
    /// sending any of it up, which is the slow part of <see cref="Update"/> (every frame read and cut down to what's in it). Any thread,
    /// for whoever asked for a few hundred frames and doesn't want the first frame that draws them to wait for it
    /// (a fighter got ready on a worker). They are drawn from the next Update on, as ever. Waits for a Pack on
    /// another thread to finish, so calling it before the first frame that has to have the lot makes sure of it.
    /// </summary>
    public void Pack()
    {
        if (_pending.IsEmpty) return;
        lock (_packing) PackPending();
    }

    // With the lock held
    private bool PackPending()
    {
        if (_pending.IsEmpty) return false;

        var batch = new List<Pending>();
        while (_pending.TryDequeue(out var pending))
        {
            batch.Add(pending);
        }

        // Tallest first, things of a similar height end up on the same shelf and waste the least
        batch.Sort((a, b) => b.Height.CompareTo(a.Height));

        var regions = _packed ?? new Dictionary<string, AtlasRegion>(_regions);
        bool grown = _grew;

        foreach (var pending in batch)
        {
            // Out of the sheet, or out of the Aseprite file's frame, either way the part that was asked for
            if (SpriteImages.Read(pending.Path, pending.X, pending.Y, pending.Width, pending.Height, pending.Layer) is not { } part) continue;
            if (pending.Most > 0 && (part.Width > pending.Most || part.Height > pending.Most))
                part = Shrink(part, pending.Most);

            // Only the pixels that are anything, if asked. A frame of nothing at all keeps one clear texel so it's drawn as nothing
            int left = 0, top = 0, width = part.Width, height = part.Height;
            if (Trim) OpaqueBounds(part, out left, out top, out width, out height);

            if (!TryPlace(width, height, out int x, out int y, ref grown))
            {
                Log.Error($"[TextureAtlas] There is no room left for '{pending.Key}'.");
                continue;
            }

            // Cut the part out row by row, it goes up on its own to where it was placed and that is the last of it
            byte[] pixels = new byte[width * height * 4];
            for (int row = 0; row < height; row++)
            {
                part.Data
                    .AsSpan(((top + row) * part.Width + left) * 4, width * 4)
                    .CopyTo(pixels.AsSpan(row * width * 4));
            }

            _cuts.Add(new Cut(x, y, width, height, pixels));
            regions[pending.Key] = new AtlasRegion(new Vector2(x, y), new Vector2(width, height), new Vector2(left, top), new Vector2(part.Width, part.Height));
        }

        _packed = regions;
        _grew = grown;
        return batch.Count > 0;
    }

    // The smallest box around the pixels that aren't fully transparent
    // A picture brought down to fit a square of so many pixels, every pixel of the small one the average of the ones it
    // covers (by their alpha, or the clear ones round a sprite go dark at its edge)
    private static SpritePixels Shrink(in SpritePixels part, int most)
    {
        float scale = MathF.Min((float)most / part.Width, (float)most / part.Height);
        int width = Math.Max(1, (int)MathF.Round(part.Width * scale)), height = Math.Max(1, (int)MathF.Round(part.Height * scale));
        var pixels = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            int fromY = y * part.Height / height, toY = Math.Max(fromY + 1, (y + 1) * part.Height / height);
            for (int x = 0; x < width; x++)
            {
                int fromX = x * part.Width / width, toX = Math.Max(fromX + 1, (x + 1) * part.Width / width);
                float r = 0, g = 0, b = 0, a = 0;
                for (int sy = fromY; sy < toY; sy++)
                {
                    for (int sx = fromX; sx < toX; sx++)
                    {
                        int at = (sy * part.Width + sx) * 4;
                        float alpha = part.Data[at + 3];
                        r += part.Data[at] * alpha;
                        g += part.Data[at + 1] * alpha;
                        b += part.Data[at + 2] * alpha;
                        a += alpha;
                    }
                }

                int to = (y * width + x) * 4;
                int count = (toX - fromX) * (toY - fromY);
                if (a > 0.0f)
                {
                    pixels[to] = (byte)(r / a);
                    pixels[to + 1] = (byte)(g / a);
                    pixels[to + 2] = (byte)(b / a);
                }
                pixels[to + 3] = (byte)(a / count);
            }
        }

        return new SpritePixels(pixels, width, height);
    }

    private static void OpaqueBounds(in SpritePixels part, out int left, out int top, out int width, out int height)
    {
        int minX = part.Width, minY = part.Height, maxX = -1, maxY = -1;
        ReadOnlySpan<byte> data = part.Data;

        for (int y = 0; y < part.Height; y++)
        {
            int row = y * part.Width * 4;
            for (int x = 0; x < part.Width; x++)
            {
                if (data[row + x * 4 + 3] == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < 0)
        {
            left = top = 0;
            width = height = 1;
            return;
        }

        left = minX;
        top = minY;
        width = maxX - minX + 1;
        height = maxY - minY + 1;
    }

    /// <summary>
    /// Helper method to find a free spot along the skyline, growing the atlas if there is none left.
    /// </summary>
    private bool TryPlace(int width, int height, out int x, out int y, ref bool grown)
    {
        x = y = 0;

        int paddedWidth = width + PADDING, paddedHeight = height + PADDING;
        if (paddedWidth > _width) return false;

        int tallest = (int)Math.Min(MAX_HEIGHT, Horizon.Graphics.GraphicsDevice.IsAvailable ? Horizon.Graphics.GraphicsDevice.Current.MaxTextureSize : MAX_HEIGHT);

        while (!TryFit(paddedWidth, paddedHeight, out x, out y))
        {
            if (_height * 2 > tallest) return false;

            // Twice as tall, with everything that is in it staying where it is. The skyline doesn't care how tall the
            // sky is, and the texture catches up when what was packed goes up (Upload copies the old one into the new)
            Log.Info($"[TextureAtlas] {_width} by {_height} is full at {Occupancy:P0} with {_placed} regions in it, growing to {_height * 2}.");
            _height *= 2;
            grown = true;
        }

        Raise(x, y + paddedHeight, paddedWidth);
        _used += (long)paddedWidth * paddedHeight;
        _placed++;
        return true;
    }

    // The lowest spot along the skyline the box sits on, and the leftmost of those. False if it's above the top
    private bool TryFit(int width, int height, out int bestX, out int bestY)
    {
        bestX = bestY = 0;
        int best = int.MaxValue;

        for (int i = 0; i < _skyline.Count; i++)
        {
            int x = _skyline[i].X;
            if (x + width > _width) break;

            // The box spans a few segments, it rests on the highest of them
            int y = 0, covered = 0;
            for (int j = i; j < _skyline.Count && covered < width; j++)
            {
                y = Math.Max(y, _skyline[j].Y);
                covered += _skyline[j].Width;
            }

            if (y + height > _height || y >= best) continue;

            best = y;
            bestX = x;
            bestY = y;
        }

        return best != int.MaxValue;
    }

    // The skyline with a flat top of that width at that height, whatever was under it swallowed
    private void Raise(int x, int top, int width)
    {
        int right = x + width;
        var raised = new List<Segment>(_skyline.Count + 2);
        bool placed = false;

        foreach (var segment in _skyline)
        {
            int segmentRight = segment.X + segment.Width;

            if (segmentRight <= x || segment.X >= right)
            {
                // Clear of the new box, on either side
                if (segment.X >= right && !placed)
                {
                    raised.Add(new Segment(x, top, width));
                    placed = true;
                }
                raised.Add(segment);
                continue;
            }

            // Whatever of it sticks out either side of the box stays at its own height
            if (segment.X < x) raised.Add(new Segment(segment.X, segment.Y, x - segment.X));
            if (!placed)
            {
                raised.Add(new Segment(x, top, width));
                placed = true;
            }
            if (segmentRight > right) raised.Add(new Segment(right, segment.Y, segmentRight - right));
        }

        if (!placed) raised.Add(new Segment(x, top, width));

        // Neighbours at the same height are one segment, it keeps the search short
        _skyline.Clear();
        foreach (var segment in raised)
        {
            if (_skyline.Count > 0 && _skyline[^1].Y == segment.Y && _skyline[^1].X + _skyline[^1].Width == segment.X)
                _skyline[^1] = _skyline[^1] with { Width = _skyline[^1].Width + segment.Width };
            else
                _skyline.Add(segment);
        }
    }

    // With the lock held, on the render thread
    private void Upload(bool grown)
    {
        var engine = GameEngine.Instance;

        if (Texture.Handle == 0 || grown)
        {
            // A texture can't be made bigger, so a bigger one takes its place and what the old one holds is copied
            // over on the GPU, nothing of it is kept on this side.
            // The old one hangs on for a frame, a UI that was given it this frame draws with it still
            Texture old = Texture;
            if (old.Handle != 0)
            {
                if (_retired.Handle != 0) engine.ObjectManager.Textures.Remove(_retired);
                _retired = old;
            }

            // A shared atlas is the engine's, not the scene's that happened to fill it first
            Horizon.Content.AssetScope.Guard? scope = _shared ? Horizon.Content.AssetScope.EnterGlobal() : null;
            try
            {
                if (!engine.ObjectManager.Textures.TryCreate(
                        new TextureDescription
                        {
                            Width = (uint)_width,
                            Height = (uint)_height,
                            Definition = Smooth ? TextureDefinition.RgbaUnsignedByte : TextureDefinition.RgbaUnsignedByteNearest
                        },
                        out var result))
                {
                    Log.Error($"[TextureAtlas] {result.Message}");
                    return;
                }

                Texture = result.Asset;
                Texture.Name = $"atlas {_width} by {_height}";
            }
            finally
            {
                scope?.Dispose();
            }

            // Everything that was up stays where it is, the new rows under it are clear. Before the pieces go up,
            // a piece can land in a hole the skyline left among the old rows
            if (old.Handle != 0) GraphicsDevice.Current.CopyTexels(old, Texture);
        }

        // Each piece to its place, a staging strip of its own size and no more
        foreach (Cut cut in _cuts)
            GraphicsDevice.Current.UploadTexels(Texture, cut.X, cut.Y, (uint)cut.Width, (uint)cut.Height, TexelFormat.Rgba8, cut.Pixels);

        _cuts.Clear();
    }

    public void Dispose()
    {
        if (_retired.Handle != 0)
        {
            GameEngine.Instance.ObjectManager.Textures.Remove(_retired);
            _retired = Texture.Invalid;
        }

        if (Texture.Handle != 0)
        {
            GameEngine.Instance.ObjectManager.Textures.Remove(Texture);
            Texture = Texture.Invalid;
        }
    }
}
