using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Logging;

using Horizon.Engine;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;

using Silk.NET.OpenGL;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using Texture = Horizon.OpenGL.Assets.Texture;

namespace Horizon.Rendering.Spriting;

/// <summary>
/// Where a piece of art ended up in a <see cref="TextureAtlas"/>, in texels from its top left.
/// </summary>
public readonly record struct AtlasRegion(Vector2 Position, Vector2 Size);

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

    private readonly record struct Pending(string Key, string Path, int X, int Y, int Width, int Height);

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

    public TextureAtlas(int width = 1024, int height = 1024)
    {
        _width = width;
        _height = height;
        _pixels = new byte[width * height * 4];
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
        if (_pending.IsEmpty && Texture.Handle != 0) return false;

        var batch = new List<Pending>();
        while (_pending.TryDequeue(out var pending))
        {
            batch.Add(pending);
        }

        // Tallest first, things of a similar height end up on the same shelf and waste the least
        batch.Sort((a, b) => b.Height.CompareTo(a.Height));

        var images = new Dictionary<string, (byte[] Pixels, int Width, int Height)?>();
        var regions = new Dictionary<string, AtlasRegion>(_regions);
        bool grown = false;

        foreach (var pending in batch)
        {
            if (!images.TryGetValue(pending.Path, out var image))
            {
                images[pending.Path] = image = LoadImage(pending.Path);
            }

            if (image is not { } source) continue;

            if (pending.Width < 1 || pending.Height < 1 ||
                pending.X < 0 || pending.Y < 0 ||
                pending.X + pending.Width > source.Width || pending.Y + pending.Height > source.Height)
            {
                Log.Error($"[TextureAtlas] '{pending.Key}' is not inside of '{pending.Path}' ({source.Width} by {source.Height}).");
                continue;
            }

            if (!TryPlace(pending.Width, pending.Height, out int x, out int y, ref grown))
            {
                Log.Error($"[TextureAtlas] There is no room left for '{pending.Key}'.");
                continue;
            }

            // Copy the part over row by row
            for (int row = 0; row < pending.Height; row++)
            {
                source.Pixels
                    .AsSpan(((pending.Y + row) * source.Width + pending.X) * 4, pending.Width * 4)
                    .CopyTo(_pixels.AsSpan(((y + row) * _width + x) * 4));
            }

            regions[pending.Key] = new AtlasRegion(new Vector2(x, y), new Vector2(pending.Width, pending.Height));
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

    private static (byte[] Pixels, int Width, int Height)? LoadImage(string path)
    {
        if (!File.Exists(path))
        {
            Log.Error($"[TextureAtlas] Failed to find image '{path}'!");
            return null;
        }

        try
        {
            using var image = Image.Load<Rgba32>(path);

            byte[] pixels = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixels);
            return (pixels, image.Width, image.Height);
        }
        catch (Exception e)
        {
            Log.Error($"[TextureAtlas] Failed to load image '{path}': {e.Message}");
            return null;
        }
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
        }

        // The whole thing every time: this only happens when something new is asked for, which is rare
        fixed (byte* data = _pixels)
            Horizon.Graphics.GraphicsDevice.Current.UploadTexels(Texture, 0, 0, (uint)_width, (uint)_height, Horizon.Graphics.TexelFormat.Rgba8, data);
    }

    public void Dispose()
    {
        if (Texture.Handle != 0)
        {
            GameEngine.Instance.ObjectManager.Textures.Remove(Texture);
            Texture = Texture.Invalid;
        }
    }
}
