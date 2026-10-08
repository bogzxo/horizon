using System.Numerics;

using Horizon.Engine;
using Horizon.Graphics;

namespace Horizon.Rendering.Lighting;

/// <summary>
/// The shadows sprites cast, as sharp as their pixels. Every sprite that blocks light (<see cref="Spriting.Sprite.CastsShadows"/>)
/// leaves where it was drawn in the material attachment of the G-buffer, and this turns that into a signed distance
/// field of the picture every frame on the GPU, an exact distance transform in three passes, the edges, then down
/// the columns and then along the rows (shaders/lighting/shadow_edges.slang, shadow_columns.slang and
/// shadow_rows.slang). The lights march it alongside the distance field of the occlusion map, see direct.slang, so
/// a sprite throws a shadow and bounces light exactly where its texture has ink.
/// A second distance in the same field is to everything that blocks or glows, which is what the path traced
/// lighting stops its rays at, so whatever is drawn emissive (a lamp, sparks, a glowing sign) lights what is around
/// it with its own shape and colour. Render thread.
/// </summary>
internal sealed class SpriteShadows : IDisposable
{
    private static readonly TextureDefinition Fields = new(PixelFormat.Rg16F, Smooth: true, Usage: TextureUsage.Sampled | TextureUsage.Storage);
    private static readonly TextureDefinition Columns = new(PixelFormat.Rg16F, Smooth: false, Usage: TextureUsage.Sampled | TextureUsage.Storage);
    private static readonly TextureDefinition Edges = new(PixelFormat.Rgba8, Smooth: false, Usage: TextureUsage.Sampled | TextureUsage.Storage);

    private static Technique? edges, columns, rows;

    private Texture? edgesTexture, columnsTexture, field;
    private uint width, height;

    /// <summary>The field as it was last built, null before the first time.</summary>
    public Texture? Field => field;

    /// <summary>
    /// Builds the field of a picture. Render thread, with the G-buffer drawn and nothing rendering.
    /// </summary>
    /// <param name="material">The material attachment of the G-buffer, over 0.5 in the green where something blocks light.</param>
    /// <param name="surface">The surface attachment of the G-buffer, the blue of which is how emissive a pixel is.</param>
    /// <param name="pixelWorld">How big a pixel of the picture is in world units.</param>
    /// <returns>The field, null if it couldn't be made.</returns>
    public Texture? Build(Texture material, Texture surface, uint pictureWidth, uint pictureHeight, float pixelWorld)
    {
        if (!EnsureShaders() || !Fit(pictureWidth, pictureHeight)) return null;

        var device = GraphicsDevice.Current;
        var size = new Vector2(width, height);

        // What blocks, what glows, and the edges of each
        edges!.Bind();
        edges.SetUniform("uSize", in size);
        material.Bind(0);
        surface.Bind(1);
        device.BindStorageImage(0, edgesTexture);
        device.Dispatch((width + 7) / 8, (height + 7) / 8);
        device.Barrier(BarrierTargets.ShaderImages);

        // How far up or down every column the nearest edge is, a workgroup a column
        columns!.Bind();
        columns.SetUniform("uSize", in size);
        edgesTexture!.Bind(0);
        device.BindStorageImage(0, columnsTexture);
        device.Dispatch(width);
        device.Barrier(BarrierTargets.ShaderImages);

        // And out of those how far the nearest edge is at all, a workgroup a row
        rows!.Bind();
        rows.SetUniform("uSize", in size);
        rows.SetUniform("uPixelWorld", pixelWorld);
        columnsTexture!.Bind(0);
        edgesTexture.Bind(1);
        device.BindStorageImage(0, field);
        device.Dispatch(height);
        device.Barrier(BarrierTargets.ShaderImages);
        rows.Unbind();

        device.BindStorageImage(0, null);
        return field;
    }

    private bool Fit(uint pictureWidth, uint pictureHeight)
    {
        if (field is not null && width == pictureWidth && height == pictureHeight) return true;

        Release();
        width = pictureWidth;
        height = pictureHeight;

        var textures = GameEngine.Instance.ObjectManager.Textures;
        if (!textures.TryCreate(new TextureDescription { Width = width, Height = height, Definition = Edges }, out var e)) return false;
        if (!textures.TryCreate(new TextureDescription { Width = width, Height = height, Definition = Columns }, out var c)) return false;
        if (!textures.TryCreate(new TextureDescription { Width = width, Height = height, Definition = Fields }, out var f)) return false;

        edgesTexture = e.Asset;
        columnsTexture = c.Asset;
        field = f.Asset;
        return true;
    }

    private static bool EnsureShaders()
    {
        if (edges is not null && columns is not null && rows is not null) return true;

        var shaders = GameEngine.Instance.ObjectManager.Shaders;
        if (!shaders.TryCreateOrGet("shadow_edges", ShaderDescription.FromPath("shaders/lighting", "shadow_edges"), out var e)) return false;
        if (!shaders.TryCreateOrGet("shadow_columns", ShaderDescription.FromPath("shaders/lighting", "shadow_columns"), out var c)) return false;
        if (!shaders.TryCreateOrGet("shadow_rows", ShaderDescription.FromPath("shaders/lighting", "shadow_rows"), out var r)) return false;

        edges = new Technique(e.Asset);
        columns = new Technique(c.Asset);
        rows = new Technique(r.Asset);
        return true;
    }

    private void Release()
    {
        edgesTexture?.Dispose();
        columnsTexture?.Dispose();
        field?.Dispose();
        edgesTexture = columnsTexture = field = null;
    }

    public void Dispose() => Release();
}
