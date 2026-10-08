using System.Numerics;

using Horizon.Engine;
using Horizon.Graphics;

namespace Horizon.Rendering.Lighting;

/// <summary>
/// The direct lighting of a <see cref="DeferredRenderer2D"/> worked out once per lighting pixel
/// (<see cref="DeferredRenderer2D.LightingPixelSize"/>) by a compute pass (shaders/lighting/light_cells.slang), on a grid
/// over what the camera sees, for the deferred pass to read instead of lighting every pixel of the screen by
/// itself. Art drawn at three times its size is lit nine times over otherwise, every one of those pixels marching
/// the same shadows. Only with a lighting pixel size set, at 0 the deferred pass does it all. Render thread.
/// </summary>
internal sealed class LightCells : IDisposable
{
    private static readonly TextureDefinition Cells = new(PixelFormat.Rgba16F, Smooth: false, Usage: TextureUsage.Sampled | TextureUsage.Storage);

    private static Technique? shader;

    private Texture? light, extra;
    private uint width, height;

    /// <summary>What the lights throw on every cell, and what shows on top (highlights and glow). Null before the first frame.</summary>
    public Texture? Light => light;
    public Texture? Extra => extra;

    /// <summary>Where the grid starts in the world, a whole number of cells from the origin.</summary>
    public Vector2 Origin { get; private set; }

    /// <summary>How many cells across and up were worked out.</summary>
    public Vector2 Count { get; private set; }

    /// <summary>
    /// Works the lighting out for every cell over what a camera sees. Render thread, with the lights uploaded and sorted
    /// into tiles and the sprite field built, and the camera block set to the camera.
    /// </summary>
    /// <returns>False if it couldn't be, in which case the deferred pass lights every pixel itself.</returns>
    public bool Build(DeferredRenderer2D renderer, Camera camera, float pixelSize)
    {
        if (pixelSize <= 0.0f || !EnsureShader()) return false;

        // A cell either side of the view, so a camera between two cells still has every cell it shows
        var view = camera.Bounds;
        float left = MathF.Floor(view.X / pixelSize) - 1.0f, bottom = MathF.Floor(view.Y / pixelSize) - 1.0f;
        uint across = (uint)MathF.Ceiling(view.Width / pixelSize) + 3, up = (uint)MathF.Ceiling(view.Height / pixelSize) + 3;
        if (!Fit(across, up)) return false;

        Origin = new Vector2(left, bottom) * pixelSize;
        Count = new Vector2(across, up);

        var device = GraphicsDevice.Current;
        Vector2 origin = Origin, count = Count;

        shader!.Bind();
        renderer.FrameBuffer.BindAttachment(AttachmentPoint.Color1, 1);
        renderer.FrameBuffer.BindAttachment(AttachmentPoint.Color2, 2);
        renderer.BindLighting(shader);
        shader.SetUniform("uCellOrigin", in origin);
        shader.SetUniform("uCellCount", in count);
        shader.SetUniform("uPixelSize", pixelSize);
        device.BindStorageImage(0, light);
        device.BindStorageImage(1, extra);
        device.Dispatch((across + 7) / 8, (up + 7) / 8);
        device.Barrier(BarrierTargets.ShaderImages);
        shader.Unbind();
        device.BindStorageImage(0, null);
        device.BindStorageImage(1, null);
        return true;
    }

    private bool Fit(uint across, uint up)
    {
        if (light is not null && across <= width && up <= height) return true;

        Release();
        width = Math.Max(across, width);
        height = Math.Max(up, height);

        var textures = GameEngine.Instance.ObjectManager.Textures;
        if (!textures.TryCreate(new TextureDescription { Width = width, Height = height, Definition = Cells }, out var a)) return false;
        if (!textures.TryCreate(new TextureDescription { Width = width, Height = height, Definition = Cells }, out var b)) return false;

        light = a.Asset;
        extra = b.Asset;
        return true;
    }

    private static bool EnsureShader()
    {
        if (shader is not null) return true;

        if (!GameEngine.Instance.ObjectManager.Shaders.TryCreateOrGet("light_cells", ShaderDescription.FromPath("shaders/lighting", "light_cells"), out var result))
            return false;

        shader = new Technique(result.Asset);
        return true;
    }

    private void Release()
    {
        light?.Dispose();
        extra?.Dispose();
        light = extra = null;
    }

    public void Dispose() => Release();
}
