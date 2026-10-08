using System.Numerics;

using Horizon.Content;
using Horizon.Logging;

using Silk.NET.Vulkan;

namespace Horizon.Graphics;

/// <summary>
/// Somewhere to draw into that isn't the window, with a texture for every colour output of the fragment shader and, if
/// asked for, a depth and stencil. Bind it and draw, then read its textures from the next pass. Made along with its
/// textures and freed with them.
/// <code>
/// var target = RenderTarget.Create(RenderTargetDescription.Color(width, height));
/// target.Bind();
/// // draw
/// target.Color.Bind(0);
/// </code>
/// </summary>
public sealed class RenderTarget : GpuResource, IDisposable
{
    private readonly GraphicsDevice device;

    public uint Width { get; }
    public uint Height { get; }

    public Vector2 Size => new(Width, Height);

    /// <summary>Every attachment by where it sits.</summary>
    public IReadOnlyDictionary<AttachmentPoint, Texture> Attachments { get; }

    /// <summary>The first colour attachment, which is the picture for most targets.</summary>
    public Texture Color => ColorTextures.Length > 0 && ColorTextures[0] is { } first ? first : Texture.Invalid;

    /// <summary>The depth (and stencil) attachment, null for a target without one.</summary>
    public Texture? DepthStencil => DepthTexture;

    /* What the device goes by */

    internal readonly Texture?[] ColorTextures;
    internal readonly Format[] ColorFormats;
    internal readonly Texture? DepthTexture;
    internal readonly Format DepthFormat;
    internal readonly bool HasStencil;
    internal readonly ulong FormatKey;

    internal readonly Vector4?[] PendingColor;
    internal bool PendingDepth;
    internal float PendingDepthValue = 1.0f;
    internal int PendingStencilValue;

    internal RenderTarget(GraphicsDevice device, uint width, uint height, Dictionary<AttachmentPoint, Texture> attachments)
    {
        this.device = device;
        Width = width;
        Height = height;
        Attachments = attachments;

        int colors = 0;
        foreach (var point in attachments.Keys)
        {
            if (point <= AttachmentPoint.Color7) colors = Math.Max(colors, (int)point + 1);
        }

        ColorTextures = new Texture?[colors];
        ColorFormats = new Format[colors];
        PendingColor = new Vector4?[colors];

        ulong key = 0x5245414C5441524UL;
        for (int i = 0; i < colors; i++)
        {
            if (attachments.TryGetValue((AttachmentPoint)i, out Texture? texture))
            {
                ColorTextures[i] = texture;
                ColorFormats[i] = texture.VkFormat;
                texture.Owner = this;
            }

            key = key * 31 + (ulong)ColorFormats[i];
        }

        if (attachments.TryGetValue(AttachmentPoint.DepthStencil, out Texture? depthStencil))
        {
            DepthTexture = depthStencil;
            HasStencil = true;
        }
        else if (attachments.TryGetValue(AttachmentPoint.Depth, out Texture? depth))
        {
            DepthTexture = depth;
        }

        if (DepthTexture is not null)
        {
            DepthTexture.Owner = this;
            DepthFormat = DepthTexture.VkFormat;
            key = key * 31 + (ulong)DepthFormat + (HasStencil ? 7UL : 0UL);
        }

        FormatKey = key;
    }

    /// <summary>Makes a render target with everything a description says is attached to it. Throws if the GPU won't have it.</summary>
    public static RenderTarget Create(in RenderTargetDescription description) =>
        ObjectManager.Instance.RenderTargets.TryCreate(description, out var result)
            ? result.Asset
            : throw new InvalidOperationException(result.Message);

    /// <summary>The texture behind one of the attachments.</summary>
    public Texture TextureOf(AttachmentPoint point) => Attachments[point];

    /// <summary>Whether a texture is one of the attachments.</summary>
    internal bool Owns(Texture texture) => ReferenceEquals(texture.Owner, this);

    /// <summary>Binds a specified attachment to a texture unit.</summary>
    public void BindAttachment(AttachmentPoint point, uint unit) => Attachments[point].Bind(unit);

    /// <summary>Makes the target what is drawn into, with the viewport over all of it.</summary>
    public void Bind() => device.BindRenderTarget(this);

    /// <summary>Sets the viewport to the size of the target. Binding does this already.</summary>
    public void Viewport() => device.SetViewport(0, 0, Width, Height);

    /// <summary>Makes the window what is drawn into again.</summary>
    public static void Unbind() => GraphicsDevice.Current.BindWindow();

    /// <summary>
    /// Helper method to do the clears the target was asked for while bound and never got round to, because nothing was
    /// drawn into it after. For one attachment, or all of them for null.
    /// </summary>
    internal void FlushPendingClears(GraphicsDevice device, Texture? only)
    {
        for (int i = 0; i < ColorTextures.Length; i++)
        {
            if (PendingColor[i] is not { } color || ColorTextures[i] is not { } texture) continue;
            if (only is not null && !ReferenceEquals(only, texture)) continue;

            PendingColor[i] = null;
            device.ClearImageNow(texture, color);
        }

        if (PendingDepth && DepthTexture is { } depth && (only is null || ReferenceEquals(only, depth)))
        {
            PendingDepth = false;
            device.ClearDepthImageNow(depth, PendingDepthValue, PendingStencilValue);
        }
    }

    /// <summary>Frees the target and everything that is attached to it. Render thread.</summary>
    public void Dispose()
    {
        var manager = ObjectManager.Instance;

        foreach (var texture in Attachments.Values)
            manager.Textures.Remove(texture);

        manager.RenderTargets.Remove(this);
        GC.SuppressFinalize(this);
    }

    /// <summary>Makes a target and its textures the way a description says. What the asset manager calls.</summary>
    internal static bool TryCreate(in RenderTargetDescription description, out AssetCreationResult<RenderTarget> result)
    {
        var device = GraphicsDevice.Current;
        var manager = ObjectManager.Instance;
        var attachments = new Dictionary<AttachmentPoint, Texture>();

        foreach (var (point, definition) in description.Attachments)
        {
            if (!manager.Textures.TryCreate(new TextureDescription
                {
                    Width = description.Width,
                    Height = description.Height,
                    Definition = definition.AsRenderTarget()
                }, out var texture))
            {
                foreach (var made in attachments.Values) manager.Textures.Remove(made);

                Log.Error($"[RenderTarget] The attachment at {point} couldn't be made: {texture.Message}");
                result = new AssetCreationResult<RenderTarget> { Message = texture.Message, Status = AssetCreationStatus.Failed };
                return false;
            }

            attachments[point] = texture.Asset;
        }

        var target = new RenderTarget(device, description.Width, description.Height, attachments);
        result = new AssetCreationResult<RenderTarget> { Asset = target, Status = AssetCreationStatus.Success, Message = string.Empty };
        return true;
    }

    // The attachments go by the name of the target in the debugger, "ui colour 0"
    protected override void Named()
    {
        if (Name is null) return;

        for (int i = 0; i < ColorTextures.Length; i++)
            if (ColorTextures[i] is { } color) color.Name = $"{Name} colour {i}";
        if (DepthTexture is { } depth) depth.Name = $"{Name} depth";
    }

    protected override void DestroyCore() => device.DestroyRenderTarget(this);
}
