using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Logging;

using Horizon.Engine;
using Horizon.Graphics;



namespace Horizon.Rendering.Spriting;

/// <summary>
/// Where a piece of art ended up in a <see cref="TextureAtlas"/>, in texels from its top left.
/// </summary>
/// <summary>
/// Where something is in an atlas. <paramref name="Position"/> and <paramref name="Size"/> are the texels that were
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
/// One texture that pieces of any number of images are stitched into, so that everything showing them can be drawn
/// together (see <see cref="SpriteItem"/>) instead of once for every image.
/// Only what is asked for with <see cref="Request"/> goes in: a sheet of thousands of sprites costs nothing but the ones
/// that are used. Asking can be done from any thread and at any time, the art shows up in the atlas the next time
/// <see cref="Update"/> runs on the render thread. Whatever is in the atlas stays where it is for good, so the
/// position of a region never has to be looked up twice.
/// </summary>
public sealed class TextureAtlas : IDisposable
{
    private const int PADDING = 2;          // Texels kept clear between two regions, so neither bleeds into the other
    private const int MAX_HEIGHT = 16384;   // As tall as it is allowed to grow, or what the card takes if that's less

    private readonly record struct Pending(string Key, string Path, int X, int Y, int Width, int Height);

    // The top edge of what's been packed, left to right, as a run of flat segments. A new region goes on the
    // lowest segment it fits on (the skyline packer), which fills the holes rows would leave
    private readonly record struct Segment(int X, int Y, int Width);

    private readonly ConcurrentQueue<Pending> _pending = new();
    private readonly ConcurrentDictionary<string, byte> _requested = new();
    private readonly List<Segment> _skyline = [];
    private long _used;
    private int _placed;

    // Replaced wholesale whenever regions are added, so it can be read from any thread without a lock
    private volatile Dictionary<string, AtlasRegion> _regions = [];

    // The atlas as it is in memory, RGBA from the top left. It is what the texture is (re)made from.
    private byte[] _pixels;
    private readonly int _width;
    private int _height;
    private readonly bool _shared;

    // The rows that changed since the texture last saw them, so an atlas of a few hundred frames doesn't go up
    // whole every time one more is asked for
    private int _dirtyTop = int.MaxValue, _dirtyBottom = -1;

    // The texture a grown atlas left behind. Whoever drew with it this frame still points at it, so it goes the
    // next time round rather than from under them
    private Texture _retired = Texture.Invalid;

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
    public TextureAtlas(int width = 1024, int height = 1024, bool shared = false)
    {
        _width = width;
        _height = height;
        _shared = shared;
        _pixels = new byte[width * height * 4];
        _skyline.Add(new Segment(0, 0, width));
    }

    /// <summary>
    /// Asks for a part of an image to be put into the atlas, under a name of the caller's choosing. Thread-safe.
    /// Asking for a name again does nothing, the first one to ask decides what it is.
    /// </summary>
    /// <param name="x">The left edge of the part, in pixels from the left of the image.</param>
    /// <param name="y">The top edge of the part, in pixels from the top of the image.</param>
    public void Request(string key, string imagePath, int x, int y, int width, int height)
    {
        if (_requested.TryAdd(key, 0))
        {
            _pending.Enqueue(new Pending(key, imagePath, x, y, width, height));
        }
    }

    /// <summary>
    /// The name a part of an image goes by when nobody gives it one. The same part asked for twice (by a UI and by a
    /// sprite, say) is only put in once this way.
    /// </summary>
    public static string KeyFor(string imagePath, int x, int y, int width, int height) =>
        $"{imagePath}|{x},{y},{width},{height}";

    /// <summary>
    /// Asks for a sprite of a <see cref="SpriteSheetDefinition"/> to be put into the atlas, every frame of it. Thread-safe.
    /// </summary>
    /// <returns>The names its frames go by in the atlas, in order, to look them up with <see cref="TryGet"/>.</returns>
    public string[] Request(in SpriteSource sprite)
    {
        string[] keys = new string[Math.Max(sprite.Frames, 1)];
        for (int frame = 0; frame < keys.Length; frame++)
        {
            // Wherever the sheet says the frame is, to the right of the one before unless it says otherwise
            (int x, int y) = sprite.FrameAt(frame);

            keys[frame] = KeyFor(sprite.Path, x, y, sprite.Width, sprite.Height);
            Request(keys[frame], sprite.Path, x, y, sprite.Width, sprite.Height);
        }

        return keys;
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
    /// </summary>
    /// <returns>Whether anything was added.</returns>
    public bool Update()
    {
        if (_retired.Handle != 0)
        {
            GameEngine.Instance.ObjectManager.Textures.Remove(_retired);
            _retired = Texture.Invalid;
        }

        if (_pending.IsEmpty && Texture.Handle != 0) return false;

        var batch = new List<Pending>();
        while (_pending.TryDequeue(out var pending))
        {
            batch.Add(pending);
        }

        // Tallest first, things of a similar height end up on the same shelf and waste the least
        batch.Sort((a, b) => b.Height.CompareTo(a.Height));

        var regions = new Dictionary<string, AtlasRegion>(_regions);
        bool grown = false;

        foreach (var pending in batch)
        {
            // Out of the sheet, or out of the Aseprite file's frame, either way the part that was asked for
            if (SpriteImages.Read(pending.Path, pending.X, pending.Y, pending.Width, pending.Height) is not { } part) continue;

            // Only the pixels that are anything, if asked. A frame of nothing at all keeps one clear texel so it's drawn as nothing
            int left = 0, top = 0, width = part.Width, height = part.Height;
            if (Trim) OpaqueBounds(part, out left, out top, out width, out height);

            if (!TryPlace(width, height, out int x, out int y, ref grown))
            {
                Log.Error($"[TextureAtlas] There is no room left for '{pending.Key}'.");
                continue;
            }

            // Copy the part over row by row
            for (int row = 0; row < height; row++)
            {
                part.Data
                    .AsSpan(((top + row) * part.Width + left) * 4, width * 4)
                    .CopyTo(_pixels.AsSpan(((y + row) * _width + x) * 4));
            }

            regions[pending.Key] = new AtlasRegion(new Vector2(x, y), new Vector2(width, height), new Vector2(left, top), new Vector2(part.Width, part.Height));
            _dirtyTop = Math.Min(_dirtyTop, y);
            _dirtyBottom = Math.Max(_dirtyBottom, y + height);
        }

        Upload(grown);

        // Only now, the regions are of no use to anyone before the texture has them
        _regions = regions;
        return batch.Count > 0;
    }

    /// <summary>
    /// Helper method to find a free spot, on a shelf that fits or on a new one underneath the others.
    /// </summary>
    // The smallest box around the pixels that aren't fully transparent
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

    private bool TryPlace(int width, int height, out int x, out int y, ref bool grown)
    {
        x = y = 0;

        int paddedWidth = width + PADDING, paddedHeight = height + PADDING;
        if (paddedWidth > _width) return false;

        int tallest = (int)Math.Min(MAX_HEIGHT, Horizon.Graphics.GraphicsDevice.IsAvailable ? Horizon.Graphics.GraphicsDevice.Current.MaxTextureSize : MAX_HEIGHT);

        while (!TryFit(paddedWidth, paddedHeight, out x, out y))
        {
            if (_height * 2 > tallest) return false;

            // Twice as tall, with everything that is in it staying where it is. The skyline doesn't care how tall the sky is
            Log.Info($"[TextureAtlas] {_width} by {_height} is full at {Occupancy:P0} with {_placed} regions in it, growing to {_height * 2}.");
            _height *= 2;
            Array.Resize(ref _pixels, _width * _height * 4);
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

    private unsafe void Upload(bool grown)
    {
        var engine = GameEngine.Instance;

        if (Texture.Handle == 0 || grown)
        {
            // A texture can't be made bigger, so a bigger one takes its place
            // The old one hangs on for a frame, a UI that was given it this frame draws with it still
            if (Texture.Handle != 0)
            {
                if (_retired.Handle != 0) engine.ObjectManager.Textures.Remove(_retired);
                _retired = Texture;
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
                            Definition = TextureDefinition.RgbaUnsignedByteNearest
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
        }

        // The whole thing for a new texture, otherwise only the rows that something landed in
        int top = grown || Texture.Handle == 0 || _dirtyTop == int.MaxValue ? 0 : _dirtyTop;
        int bottom = grown || _dirtyBottom < 0 ? _height : Math.Min(_dirtyBottom, _height);
        if (bottom > top)
        {
            fixed (byte* data = &_pixels[top * _width * 4])
                GraphicsDevice.Current.UploadTexels(Texture, 0, top, (uint)_width, (uint)(bottom - top), TexelFormat.Rgba8, data);
        }

        _dirtyTop = int.MaxValue;
        _dirtyBottom = -1;
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
