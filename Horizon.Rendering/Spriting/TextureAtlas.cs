using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;

using Bogz.Logging;

using Horizon.Engine;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;

using Silk.NET.OpenGL;

using Texture = Horizon.OpenGL.Assets.Texture;

namespace Horizon.Rendering.Spriting;

/// <summary>
/// Where a piece of art ended up in a <see cref="TextureAtlas"/>, in texels from its top left.
/// </summary>
/// <param name="Position">The top left corner of the art in the atlas.</param>
/// <param name="Size">How big the art is in the atlas.</param>
/// <param name="Offset">
/// For art that was asked for trimmed: where what was kept of it sits inside of what was asked for, from its top left.
/// Zero for art that went in whole.
/// </param>
/// <param name="SourceSize">How big the art was before it was trimmed, see <see cref="FullSize"/>.</param>
public readonly record struct AtlasRegion(Vector2 Position, Vector2 Size, Vector2 Offset = default, Vector2 SourceSize = default)
{
    /// <summary>How big the art is with its see-through edges, which is the size it is drawn at. The same as <see cref="Size"/> unless it was trimmed.</summary>
    public Vector2 FullSize => SourceSize == default ? Size : SourceSize;

    /// <summary>Whether the art was trimmed down to nothing, a frame with nothing drawn on it.</summary>
    public bool IsEmpty => Size.X <= 0.0f || Size.Y <= 0.0f;
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
    private const int MAX_HEIGHT = 8192;    // As big as it is allowed to grow, older cards don't take textures beyond this

    private readonly record struct Pending(string Key, string Path, int X, int Y, int Width, int Height, int Frame, LayerSelection Layers, bool Trim);

    // Regions are packed in rows, each as tall as the first thing that was put in it
    private sealed class Shelf
    {
        public int Y, Height, Cursor;
    }

    private readonly ConcurrentQueue<Pending> _pending = new();
    private readonly ConcurrentDictionary<string, byte> _requested = new();
    private readonly List<Shelf> _shelves = [];

    // Replaced wholesale whenever regions are added, so it can be read from any thread without a lock
    private volatile Dictionary<string, AtlasRegion> _regions = [];

    // The atlas as it is in memory, RGBA from the top left. It is what the texture is (re)made from.
    private byte[] _pixels;
    private readonly int _width;
    private int _height;

    /// <summary>
    /// The texture everything is stitched into. It is replaced by a bigger one when the atlas runs out of room,
    /// so ask for it every time rather than keeping it.
    /// </summary>
    public Texture Texture { get; private set; } = Texture.Invalid;

    /// <summary>
    /// The size of the atlas in texels, only its height ever changes.
    /// </summary>
    public Vector2 Size => new(_width, _height);

    // Whether the texture is kept whatever scene is being set up when it is made (or made bigger), see AssetScope.EnterGlobal
    private readonly bool _shared;

    /// <param name="width">How wide the atlas is in texels, which it stays. Nothing wider than this fits.</param>
    /// <param name="height">How tall it starts out, it grows by itself.</param>
    /// <param name="shared">
    /// Whether the atlas outlives the scene that happens to be around when its texture is made, for an atlas that is
    /// kept and used by whatever comes after. An atlas that is made while assets are shared on purpose is shared anyway.
    /// </param>
    public TextureAtlas(int width = 1024, int height = 1024, bool shared = false)
    {
        _width = width;
        _height = height;
        _pixels = new byte[width * height * 4];
        _shared = shared || Horizon.Content.AssetScope.IsGlobal;
    }

    /// <summary>
    /// Asks for a part of an image to be put into the atlas, under a name of the caller's choosing. Thread-safe.
    /// Asking for a name again does nothing, the first one to ask decides what it is.
    /// </summary>
    /// <param name="x">The left edge of the part, in pixels from the left of the image.</param>
    /// <param name="y">The top edge of the part, in pixels from the top of the image.</param>
    /// <param name="frame">Which frame of the image, for an image that has frames of its own (an Aseprite file).</param>
    /// <param name="layers">Which layers of the image, for an image that has layers.</param>
    /// <param name="trim">
    /// Whether the see-through edges of the part are left out. What is left of it says where it was (see
    /// <see cref="AtlasRegion.Offset"/>) so it can be drawn as if they were there.
    /// </param>
    public void Request(string key, string imagePath, int x, int y, int width, int height, int frame = 0, LayerSelection layers = default, bool trim = false)
    {
        if (_requested.TryAdd(key, 0))
        {
            _pending.Enqueue(new Pending(key, imagePath, x, y, width, height, frame, layers, trim));
        }
    }

    /// <summary>
    /// The name a part of an image goes by when nobody gives it one. The same part asked for twice (by a UI and by a
    /// sprite, say) is only put in once this way.
    /// </summary>
    public static string KeyFor(string imagePath, int x, int y, int width, int height, int frame = 0, LayerSelection layers = default) =>
        frame == 0 && layers.IsDefault
            ? $"{imagePath}|{x},{y},{width},{height}"
            : $"{imagePath}|{x},{y},{width},{height}|{frame}|{layers.Spec}";

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

            int imageFrame = sprite.ImageFrameAt(frame);

            keys[frame] = KeyFor(sprite.Path, x, y, sprite.Width, sprite.Height, imageFrame, sprite.Layers);
            Request(keys[frame], sprite.Path, x, y, sprite.Width, sprite.Height, imageFrame, sprite.Layers, sprite.Trim);
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
        if (IsDisposed || (_pending.IsEmpty && Texture.Handle != 0)) return false;

        var batch = new List<Pending>();
        while (_pending.TryDequeue(out var pending))
        {
            batch.Add(pending);
        }

        // Tallest first, things of a similar height end up on the same shelf and waste the least
        batch.Sort((a, b) => b.Height.CompareTo(a.Height));

        // An Aseprite file is a different picture on every frame and with every set of layers
        var images = new Dictionary<(string Path, int Frame, string? Layers), ImagePixels?>();
        var regions = new Dictionary<string, AtlasRegion>(_regions);
        bool grown = false;

        foreach (var pending in batch)
        {
            var imageKey = (pending.Path, pending.Frame, pending.Layers.Spec);
            if (!images.TryGetValue(imageKey, out var image))
            {
                images[imageKey] = image = ImagePixels.Load(pending.Path, pending.Frame, pending.Layers);
            }

            if (image is not { } source) continue;

            if (pending.Width < 1 || pending.Height < 1 ||
                pending.X < 0 || pending.Y < 0 ||
                pending.X + pending.Width > source.Width || pending.Y + pending.Height > source.Height)
            {
                Log.Error($"[TextureAtlas] '{pending.Key}' is not inside of '{pending.Path}' ({source.Width} by {source.Height}).");
                continue;
            }

            // What is put in, which is less than what was asked for if its edges are to be left out
            var (fromX, fromY, width, height) = pending.Trim
                ? Trimmed(source, pending.X, pending.Y, pending.Width, pending.Height)
                : (pending.X, pending.Y, pending.Width, pending.Height);

            Vector2 offset = new(fromX - pending.X, fromY - pending.Y);
            Vector2 asked = new(pending.Width, pending.Height);

            if (width < 1 || height < 1)
            {
                // Nothing drawn on it at all. It is in the atlas as far as anybody asking is concerned, it just takes up no room
                regions[pending.Key] = new AtlasRegion(Vector2.Zero, Vector2.Zero, offset, asked);
                continue;
            }

            if (!TryPlace(width, height, out int x, out int y, ref grown))
            {
                Log.Error($"[TextureAtlas] There is no room left for '{pending.Key}'.");
                continue;
            }

            // Copy the part over row by row
            for (int row = 0; row < height; row++)
            {
                source.Data
                    .AsSpan(((fromY + row) * source.Width + fromX) * 4, width * 4)
                    .CopyTo(_pixels.AsSpan(((y + row) * _width + x) * 4));
            }

            regions[pending.Key] = pending.Trim
                ? new AtlasRegion(new Vector2(x, y), new Vector2(width, height), offset, asked)
                : new AtlasRegion(new Vector2(x, y), new Vector2(width, height));
        }

        Upload(grown);

        // Only now, the regions are of no use to anyone before the texture has them
        _regions = regions;
        return batch.Count > 0;
    }

    /// <summary>
    /// Helper method to find a free spot, on a shelf that fits or on a new one underneath the others.
    /// </summary>
    private bool TryPlace(int width, int height, out int x, out int y, ref bool grown)
    {
        x = y = 0;

        int paddedWidth = width + PADDING, paddedHeight = height + PADDING;
        if (paddedWidth > _width) return false;

        foreach (var shelf in _shelves)
        {
            // Not on a shelf that is a lot taller than we are, that room is better kept for something that needs it
            if (shelf.Height < paddedHeight || shelf.Height > paddedHeight * 2 || shelf.Cursor + paddedWidth > _width) continue;

            x = shelf.Cursor;
            y = shelf.Y;
            shelf.Cursor += paddedWidth;
            return true;
        }

        int bottom = _shelves.Count > 0 ? _shelves[^1].Y + _shelves[^1].Height : 0;
        while (bottom + paddedHeight > _height)
        {
            if (_height * 2 > MAX_HEIGHT) return false;

            // Twice as tall, with everything that is in it staying where it is
            _height *= 2;
            Array.Resize(ref _pixels, _width * _height * 4);
            grown = true;
        }

        _shelves.Add(new Shelf { Y = bottom, Height = paddedHeight, Cursor = paddedWidth });
        x = 0;
        y = bottom;
        return true;
    }

    /// <summary>
    /// Helper method to find the part of a part of an image that has anything drawn on it.
    /// </summary>
    private static (int X, int Y, int Width, int Height) Trimmed(in ImagePixels image, int x, int y, int width, int height)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;

        for (int row = y; row < y + height; row++)
        {
            ReadOnlySpan<byte> pixels = image.Data.AsSpan((row * image.Width + x) * 4, width * 4);
            for (int column = 0; column < width; column++)
            {
                if (pixels[column * 4 + 3] == 0) continue;

                minX = Math.Min(minX, column); maxX = Math.Max(maxX, column);
                minY = Math.Min(minY, row); maxY = Math.Max(maxY, row);
            }
        }

        return maxX < 0 ? (x, y, 0, 0) : (x + minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private unsafe void Upload(bool grown)
    {
        var engine = GameEngine.Instance;

        if (Texture.Handle == 0 || grown)
        {
            // A texture can't be made bigger, so a bigger one takes its place
            if (Texture.Handle != 0)
            {
                engine.ObjectManager.Textures.Remove(Texture);
            }

            var description = new TextureDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                Definition = TextureDefinition.RgbaUnsignedByteNearest
            };

            // An atlas that is shared is nobody's, whichever scene it happens to grow in
            bool made;
            if (_shared)
            {
                using var nobody = Horizon.Content.AssetScope.EnterGlobal();
                made = engine.ObjectManager.Textures.TryCreate(description, out var shared);
                if (made) Texture = shared.Asset;
                else Log.Error($"[TextureAtlas] {shared.Message}");
            }
            else
            {
                made = engine.ObjectManager.Textures.TryCreate(description, out var own);
                if (made) Texture = own.Asset;
                else Log.Error($"[TextureAtlas] {own.Message}");
            }

            if (!made)
            {
                Texture = Texture.Invalid;
                return;
            }
        }

        // The whole thing every time: this only happens when something new is asked for, which is rare
        fixed (byte* data = _pixels)
        {
            engine.GL.TextureSubImage2D(
                Texture.Handle, 0, 0, 0, (uint)_width, (uint)_height, PixelFormat.Rgba, PixelType.UnsignedByte, data);
        }
    }

    /// <summary>
    /// Whether the atlas has been disposed of. Nothing goes into it any more and it has no texture.
    /// </summary>
    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        IsDisposed = true;

        if (Texture.Handle != 0)
        {
            GameEngine.Instance.ObjectManager.Textures.Remove(Texture);
            Texture = Texture.Invalid;
        }
    }
}
