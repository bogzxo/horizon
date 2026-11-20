using Horizon.Content;
using System.Numerics;

using Horizon.Graphics;
using Horizon.Logging;
using Horizon.Rendering;
using Horizon.Rendering.PostProcessing;

namespace Horizon.Engine;

/// <summary>
/// What the window shows from the moment it opens until the first scene has something to draw. Without it that
/// stretch was whatever the system had lying around in the window, which on a slow start (a scene with a lot to
/// load, shaders compiled for the first time) is a good long look at garbage. It is a colour with a picture over
/// it, the engine's own unless the game brings one. It doesn't move, nothing is updated while a scene is set up.
/// <code>
/// using var engine = new GameEngine(window);
/// engine.LoadingScreen.Image = "Assets/loading.png";
/// engine.LoadingScreen.Color = new Vector3(0.02f, 0.02f, 0.03f);
/// engine.Run&lt;MainMenuScene&gt;();
/// </code>
/// It is drawn once before anything of the engine or the game is set up, the window is only shown once it is in
/// it, and then under every frame until the first scene draws. All of it is set before <see cref="GameEngine.Run()"/>.
/// </summary>
public sealed class LoadingScreen
{
    /// <summary>The picture the engine brings, for a game that hasn't one of its own.</summary>
    public const string DEFAULT_IMAGE = "Assets/global/loading.aseprite";

    private const string UNIFORM_COLOR = "uColor";
    private const string UNIFORM_PLACE = "uPlace";

    /// <summary>Whether there is one at all. Off, the window opens on black.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The colour of the window while it waits, wherever the picture isn't or is see-through. The night blue at the
    /// top and the bottom of the engine's own picture unless told otherwise, so a window of another shape than it
    /// has the bars either side of it in the same sky.
    /// </summary>
    public Vector3 Color { get; set; } = new(0.05f, 0.08f, 0.13f);

    /// <summary>
    /// The picture, the path of a PNG or an Aseprite file (its first frame), null for the colour alone. One that
    /// isn't there is the colour alone as well, and says so in the log.
    /// </summary>
    public string? Image { get; set; } = DEFAULT_IMAGE;

    /// <summary>
    /// How many pixels of the screen a pixel of the picture takes, 0 (which it is unless told otherwise) for as big
    /// as fits in the window at its own shape. A picture bigger than the window is shrunk to fit whatever this says.
    /// </summary>
    public float ImageScale { get; set; }

    /// <summary>Whether the picture is smoothed when it is drawn bigger or smaller than it is. Pixel art wants this off.</summary>
    public bool Smooth { get; set; } = true;

    /// <summary>
    /// Whether it is on screen right now, from the window opening until the first scene draws (or until the first
    /// frame, for a game without a scene).
    /// </summary>
    public bool IsUp { get; internal set; }

    private ScreenTechnique? technique;
    private Texture? picture;
    private bool made, hasPicture;

    /// <summary>
    /// Helper method to put the screen in the window and nothing else, a frame of its own, before there is an
    /// engine to draw frames. Render thread.
    /// </summary>
    internal void Present(GraphicsDevice graphics, Vector2 viewport)
    {
        if (!Enabled || viewport.X < 1.0f || viewport.Y < 1.0f)
            return;

        IsUp = true;

        graphics.BeginFrame((uint)viewport.X, (uint)viewport.Y);
        graphics.SetViewport(0, 0, (uint)viewport.X, (uint)viewport.Y);

        // No picture, only its colour: a clear and nothing else, no shader made or picture read and decoded for it
        if (Image is not { Length: > 0 })
        {
            ClearToColor(graphics);
            graphics.EndFrame();
            return;
        }

        graphics.Clear(ClearTargets.All);

        CameraBlock.BeginFrame(viewport, 0.0f);
        Draw(viewport);
        CameraBlock.EndFrame();

        graphics.EndFrame();
    }

    // The window filled with the screen's colour by a clear, the clear colour put back as it was
    private void ClearToColor(GraphicsDevice graphics)
    {
        Vector4 was = graphics.ClearColor;
        graphics.ClearColor = new Vector4(Color, 1.0f);
        graphics.Clear(ClearTargets.All);
        graphics.ClearColor = was;
    }

    /// <summary>Helper method to draw the screen over the whole of the window. Render thread.</summary>
    internal void Draw(Vector2 viewport)
    {
        if (!Enabled)
            return;

        if (Image is not { Length: > 0 })
        {
            ClearToColor(GraphicsDevice.Current);
            return;
        }

        if (!made)
        {
            made = true;
            Make();
        }

        if (technique is not { IsValid: true } shader || picture is null)
            return;

        Vector4 place = default;
        if (hasPicture)
        {
            // At its own shape, as big as fits or as big as it was asked to be, and never bigger than the window
            Vector2 size = picture.Size;
            float fits = MathF.Min(viewport.X / size.X, viewport.Y / size.Y);
            size *= ImageScale > 0.0f ? MathF.Min(ImageScale, fits) : fits;

            // On whole pixels, or pixel art comes out with rows of uneven height
            Vector2 corner = new(MathF.Floor((viewport.X - size.X) * 0.5f), MathF.Floor((viewport.Y - size.Y) * 0.5f));
            place = new Vector4(corner / viewport, size.X / viewport.X, size.Y / viewport.Y);
        }

        var before = RenderState.Save();
        RenderState.Blend = false;
        RenderState.DepthTest = false;

        Vector4 color = new(Color, 1.0f);

        Renderer2D.BindOutput(null);
        shader.Bind();
        shader.SetUniform(UNIFORM_COLOR, in color);
        shader.SetUniform(UNIFORM_PLACE, in place);
        picture.Bind(0);
        ScreenTriangle.Draw();
        shader.Unbind();

        RenderState.Restore(before);
    }

    /// <summary>Helper method to make the shader and the picture, which belong to nobody's scene.</summary>
    private void Make()
    {
        using var nobody = Horizon.Content.AssetScope.EnterGlobal();

        technique = new PostTechnique("loading");

        if (Image is { Length: > 0 } path)
            picture = Read(path, Smooth);

        hasPicture = picture is not null;

        // Something has to be bound where the shader says its picture is, picture or not
        picture ??= Texture.FromPixels(1, 1, [0, 0, 0, 0]);
        picture.Name = "loading screen";
    }

    /// <summary>
    /// Helper method to read the picture into a texture of the screen's own. Not through the textures that are
    /// shared by their file, a game that loads the same file later gets its own, filtered the way it asked.
    /// </summary>
    private static Texture? Read(string path, bool smooth)
    {
        if (!ContentFiles.Exists(path))
        {
            // The engine's own going missing is a game that didn't copy it, which is its business
            if (path != DEFAULT_IMAGE)
                Log.Warning($"[LoadingScreen] There is no '{path}', the loading screen goes without a picture.");

            return null;
        }

        try
        {
            var definition = smooth ? TextureDefinition.RgbaUnsignedByte : TextureDefinition.RgbaUnsignedByteNearest;

            if (Horizon.Rendering.Spriting.AsepriteDocument.IsAseprite(path))
            {
                // Its first frame as it was put together the last time, kept with the shaders' and fonts' cache. Put
                // together from its layers it was a seventh of a second of every launch before anything was on screen
                string? kept = KeptFrame(path);
                if (kept is not null && ReadKept(kept) is { } cached)
                    return Texture.FromPixels(cached.Width, cached.Height, cached.Pixels, definition);

                var document = Horizon.Rendering.Spriting.AsepriteDocument.Open(path);
                byte[] frame = document.ReadFrame(0).ToArray();
                Texture picture = Texture.FromPixels((uint)document.Width, (uint)document.Height, frame, definition);
                if (kept is not null)
                    Keep(kept, (uint)document.Width, (uint)document.Height, frame);

                // A whole screen of pixels, which the file would otherwise keep for the run
                document.DropRendered();
                return picture;
            }

            var image = ImageFile.Load(path);
            var pixels = image.Pixels;
            return Texture.FromPixels((uint)image.Width, (uint)image.Height, pixels, definition);
        }
        catch (Exception e)
        {
            Log.Warning($"[LoadingScreen] '{path}' couldn't be read ({e.Message}), the loading screen goes without a picture.");
            return null;
        }
    }

    // What a kept first frame starts with, and the version of how it's written
    private const uint KEPT_MAGIC = 0x4C534352;

    /// <summary>Where a picture's put together first frame is kept, by its file and when it was written, null with no cache.</summary>
    private static string? KeptFrame(string path)
    {
        if (ShaderCompiler.CacheDirectory() is not { } shaders)
            return null;
        try
        {
            var file = new FileInfo(ContentFiles.Find(path).Path);
            if (!file.Exists)
                return null;
            string said = $"{file.FullName.ToLowerInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(said)))[..24];
            return Path.Combine(Path.GetDirectoryName(shaders) ?? shaders, "Loading", key + ".rgba");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (uint Width, uint Height, byte[] Pixels)? ReadKept(string file)
    {
        try
        {
            if (!File.Exists(file))
                return null;
            using var reader = new BinaryReader(File.OpenRead(file));
            if (reader.ReadUInt32() != KEPT_MAGIC)
                return null;
            uint width = reader.ReadUInt32(), height = reader.ReadUInt32();
            byte[] pixels = reader.ReadBytes((int)(width * height * 4));
            return pixels.Length == width * height * 4 ? (width, height, pixels) : null;
        }
        catch (Exception)
        {
            // A kept frame that doesn't read is put together again and kept over
            return null;
        }
    }

    // Written to a file of its own and moved into place, so one being read never finds half of it
    private static void Keep(string file, uint width, uint height, byte[] pixels)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string writing = file + "." + Environment.ProcessId + ".tmp";
            using (var writer = new BinaryWriter(File.Create(writing)))
            {
                writer.Write(KEPT_MAGIC);
                writer.Write(width);
                writer.Write(height);
                writer.Write(pixels);
            }
            File.Move(writing, file, overwrite: true);
        }
        catch (Exception)
        {
            // Not kept, put together again the next time
        }
    }
}
