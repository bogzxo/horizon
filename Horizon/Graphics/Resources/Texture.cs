using System.Numerics;

using Horizon.Content;
using Horizon.Graphics.Vulkan;

using Silk.NET.Vulkan;

using SixLabors.ImageSharp.PixelFormats;

using Image = Silk.NET.Vulkan.Image;

namespace Horizon.Graphics;

/// <summary>
/// A picture on the GPU. Loading one is a single line, and it is freed along with the scene that asked for it.
/// <code>
/// Texture backdrop = Texture.Load("Assets/backgrounds/dojo.png");
/// backdrop.Bind(0);    // on the unit the shader's BIND_TEXTURE(0) sampler reads
/// </code>
/// </summary>
public sealed class Texture : GpuResource, IDisposable
{
    // What tells a smoothed copy of a file apart from the crisp one in the cache
    private const string SMOOTH_SUFFIX = "|smooth";

    private readonly GraphicsDevice? device;

    public uint Width { get; }
    public uint Height { get; }
    public TextureDefinition Definition { get; }

    public Vector2 Size => new(Width, Height);

    /* What the device keeps about the image */

    internal Image Image;
    internal ImageView View;
    internal VulkanMemory.Allocation Memory;
    internal Format VkFormat;
    internal uint MipLevels = 1;
    internal ImageAspectFlags Aspect = ImageAspectFlags.ColorBit;
    internal ImageLayout Layout = ImageLayout.Undefined;
    internal PipelineStageFlags2 LastStage = PipelineStageFlags2.TopOfPipeBit;
    internal AccessFlags2 LastAccess = AccessFlags2.None;
    internal Sampler DefaultSampler;
    internal ulong LastUse;
    internal Dictionary<uint, uint>? BindlessVariants;

    /// <summary>
    /// Where the texture is in the bindless table, which is how a sprite item names it. <see cref="Graphics.Vulkan.BindlessTextures.NONE"/>
    /// for a texture that can't be sampled, or isn't there.
    /// </summary>
    public uint BindlessIndex { get; internal set; } = Graphics.Vulkan.BindlessTextures.NONE;

    /// <summary>The texture's slot in the bindless table when read through a sampler of its own (0 for its default one).</summary>
    public uint Bindless(uint sampler = 0) => sampler == 0 ? BindlessIndex : GraphicsDevice.Current.BindlessSlot(this, sampler);

    /// <summary>The render target the texture is an attachment of, if it is one.</summary>
    internal RenderTarget? Owner;

    /// <summary>The Vulkan image, for whoever goes underneath.</summary>
    public Image Native => Image;

    /// <summary>The view of the whole image, for whoever goes underneath.</summary>
    public ImageView NativeView => View;

    /// <summary>The layout the image is in right now, as far as the device knows. Use <see cref="GraphicsDevice.Transition"/> to change it.</summary>
    public ImageLayout CurrentLayout => Layout;

    /// <summary>Whether there is a picture here at all. What failed to load is not valid, and draws as nothing.</summary>
    public override bool IsValid => base.IsValid && Image.Handle != 0;

    /// <summary>The texture that stands for none.</summary>
    public static Texture Invalid { get; } = new();

    private Texture() : base(invalid: true)
    {
        Definition = TextureDefinition.RgbaUnsignedByte;
    }

    internal Texture(GraphicsDevice device, uint width, uint height, in TextureDefinition definition, ReadOnlySpan<byte> pixels)
    {
        this.device = device;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Definition = definition;
        device.CreateTexture(this, pixels);
    }

    /// <summary>
    /// Loads a picture from a file. Asking for the same file again hands back the same texture, it is only read once.
    /// </summary>
    /// <param name="smooth">Whether it is smoothed when it is drawn bigger or smaller than it is. Pixel art wants this off.</param>
    /// <returns>The texture, or <see cref="Invalid"/> if the file is fucked or missing (which is logged).</returns>
    public static Texture Load(string path, bool smooth = false) =>
        Load(
            smooth ? path + SMOOTH_SUFFIX : path,
            path,
            smooth ? TextureDefinition.RgbaUnsignedByte : TextureDefinition.RgbaUnsignedByteNearest);

    /// <summary>Loads a picture from a file the way a definition says, and keeps it under a name of its own.</summary>
    public static Texture Load(string name, string path, TextureDefinition definition) =>
        ObjectManager.Instance.Textures.CreateOrGet(name, new TextureDescription { Paths = [path], Definition = definition }) ?? Invalid;

    /// <summary>Makes an empty texture to draw into or fill in later. It belongs to whoever made it, see <see cref="Dispose"/>.</summary>
    public static Texture Create(uint width, uint height, TextureDefinition? definition = null) =>
        ObjectManager.Instance.Textures.Create(new TextureDescription
        {
            Width = width,
            Height = height,
            Definition = definition ?? TextureDefinition.RgbaUnsignedByteNearest
        }) ?? Invalid;

    /// <summary>Makes a texture out of texels that are already in memory, four bytes a texel red first, top row first.</summary>
    public static Texture FromPixels(uint width, uint height, ReadOnlySpan<byte> rgba, TextureDefinition? definition = null)
    {
        var texture = new Texture(GraphicsDevice.Current, width, height, definition ?? TextureDefinition.RgbaUnsignedByteNearest, rgba);
        return ObjectManager.Instance.Textures.Add(texture);
    }

    /// <summary>Binds the texture to a unit, where the shader's <c>BIND_TEXTURE(unit)</c> sampler reads.</summary>
    public void Bind(uint unit) => GraphicsDevice.Current.BindTexture(unit, this);

    /// <summary>Frees the texture right now, for whoever doesn't want to wait for the scene to end. Render thread.</summary>
    public void Dispose()
    {
        if (IsValid) ObjectManager.Instance.Textures.Remove(this);
        GC.SuppressFinalize(this);
    }

    /// <summary>Makes a texture the way a description says, reading the file if there is one. What the asset manager calls.</summary>
    internal static bool TryCreate(in TextureDescription description, out AssetCreationResult<Texture> result)
    {
        var device = GraphicsDevice.Current;

        if (description.Paths.Length > 0 && !string.IsNullOrEmpty(description.Paths[0]))
        {
            string path = description.Paths[0];
            if (!File.Exists(path))
            {
                result = new AssetCreationResult<Texture> { Asset = Invalid, Message = $"Failed to find image '{path}'!", Status = AssetCreationStatus.Failed };
                return false;
            }

            try
            {
                using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(path);
                var pixels = new byte[image.Width * image.Height * 4];
                image.CopyPixelDataTo(pixels);

                var loaded = new Texture(device, (uint)image.Width, (uint)image.Height, description.Definition, pixels) { Name = path };
                result = new AssetCreationResult<Texture> { Asset = loaded, Status = AssetCreationStatus.Success, Message = string.Empty };
                return true;
            }
            catch (Exception e)
            {
                result = new AssetCreationResult<Texture> { Asset = Invalid, Message = $"The image '{path}' couldn't be read: {e.Message}", Status = AssetCreationStatus.Failed };
                return false;
            }
        }

        if (description.Width == 0 || description.Height == 0)
        {
            result = new AssetCreationResult<Texture> { Asset = Invalid, Message = "A texture with no size and no file to read.", Status = AssetCreationStatus.Failed };
            return false;
        }

        if (description.Width > device.MaxTextureSize || description.Height > device.MaxTextureSize)
        {
            result = new AssetCreationResult<Texture> { Asset = Invalid, Message = $"A {description.Width} by {description.Height} texture is bigger than the {device.MaxTextureSize} the card takes.", Status = AssetCreationStatus.Failed };
            return false;
        }

        var made = new Texture(device, description.Width, description.Height, description.Definition, default);
        result = new AssetCreationResult<Texture> { Asset = made, Status = AssetCreationStatus.Success, Message = string.Empty };
        return true;
    }

    protected override void Named()
    {
        if (Image.Handle != 0) device?.LabelTexture(this);
    }

    protected override void DestroyCore() => device?.DestroyTexture(this);
}
