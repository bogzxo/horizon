using Horizon.Content;

using Silk.NET.Vulkan;

namespace Horizon.Graphics;

/// <summary>
/// How the GPU reads vertices, which buffers they come out of and what the attributes of a vertex are. Attributes
/// come out of numbered bindings, each a buffer read at a stride (<see cref="SetVertexBuffer"/>), and every
/// attribute says which binding it reads from (<see cref="SetAttribute"/>). <see cref="SetLayout{T}"/> does both
/// for a struct that describes itself, see <see cref="IVertex"/>. The index buffer is part of it too.
/// What is described here is what the pipeline of a draw is built for, so two arrays laid out the same share one.
/// </summary>
public sealed class VertexArray : GpuResource, IDisposable
{
    internal readonly record struct BindingDescription(GpuBuffer Buffer, uint Stride, nint Offset, uint Divisor);
    internal readonly record struct AttributeDescription(uint Index, uint Binding, VertexLayoutDescription Attribute);

    private readonly GraphicsDevice device;

    public Dictionary<VertexArraySlot, GpuBuffer> Buffers { get; }

    internal readonly SortedDictionary<uint, BindingDescription> Bindings = [];
    internal readonly List<AttributeDescription> Attributes = [];
    internal GpuBuffer? IndexBuffer;

    /// <summary>What tells one way of reading vertices from another, for the pipelines.</summary>
    internal ulong LayoutKey { get; private set; }

    public GpuBuffer this[VertexArraySlot slot] => Buffers[slot];

    internal VertexArray(GraphicsDevice device, Dictionary<VertexArraySlot, GpuBuffer> buffers)
    {
        this.device = device;
        Buffers = buffers;

        if (buffers.TryGetValue(VertexArraySlot.Indices, out GpuBuffer? indices))
            IndexBuffer = indices;
    }

    /// <summary>Makes an array and the buffers a description says go with it.</summary>
    public static VertexArray Create(in VertexArrayDescription description) =>
        ObjectManager.Instance.VertexArrays.TryCreate(description, out var result)
            ? result.Asset
            : throw new InvalidOperationException(result.Message);

    public void Bind() => device.BindVertexArray(this);

    public void Unbind() => device.BindVertexArray(null);

    /// <summary>Makes a buffer the indices of the array, 32 bit ones.</summary>
    public void SetIndexBuffer(GpuBuffer buffer) => IndexBuffer = buffer;

    /// <summary>Has a binding read a buffer, one vertex (or instance) every so many bytes.</summary>
    /// <param name="binding">The binding, which attributes refer to.</param>
    /// <param name="stride">How many bytes apart the vertices are.</param>
    /// <param name="offset">Where in the buffer the first one starts.</param>
    /// <param name="divisor">0 to step through the buffer once per vertex, 1 once per instance.</param>
    public void SetVertexBuffer(uint binding, GpuBuffer buffer, uint stride, nint offset = 0, uint divisor = 0)
    {
        Bindings[binding] = new BindingDescription(buffer, stride, offset, divisor);
        Rekey();
    }

    /// <summary>Describes one attribute, what it is made of, where it is in a vertex and which binding its vertices come out of.</summary>
    public void SetAttribute(uint index, uint binding, in VertexLayoutDescription attribute)
    {
        Attributes.RemoveAll(existing => existing.Index == index);
        Attributes.Add(new AttributeDescription(index, binding, attribute));
        Attributes.Sort((a, b) => a.Index.CompareTo(b.Index));
        Rekey();
    }

    /// <summary>
    /// Has a binding read a buffer of a struct, with every attribute the struct describes. The stride is the size
    /// of the struct, which has to be exactly what its attributes add up to.
    /// </summary>
    /// <param name="divisor">0 for a buffer of vertices, 1 for a buffer of instances.</param>
    public unsafe void SetLayout<T>(uint binding, GpuBuffer buffer, uint divisor = 0) where T : unmanaged, IVertex
    {
        int stride = sizeof(T);
        if (stride % 4 != 0)
            throw new InvalidOperationException($"The size of {typeof(T).Name} ({stride} bytes) isn't a multiple of four.");

        int described = 0;
        foreach (ref readonly VertexLayoutDescription attribute in T.GetLayout())
        {
            SetAttribute(attribute.Index, binding, in attribute);
            described += attribute.Size;
        }

        if (described != stride)
            throw new InvalidOperationException($"The attributes of {typeof(T).Name} add up to {described} bytes, the struct is {stride}: check the offsets of GetLayout().");

        SetVertexBuffer(binding, buffer, (uint)stride, 0, divisor);
    }

    private void Rekey()
    {
        ulong key = 0x564552544558UL;
        foreach (var (binding, description) in Bindings)
            key = key * 1000003 + binding * 97 + description.Stride * 13 + description.Divisor;
        foreach (var attribute in Attributes)
            key = key * 1000003 + attribute.Index * 1009 + attribute.Binding * 131 + (ulong)FormatOf(attribute.Attribute) * 17 + (ulong)attribute.Attribute.Offset;
        LayoutKey = key;
    }

    /// <summary>The Vulkan format of an attribute.</summary>
    internal static Format FormatOf(in VertexLayoutDescription attribute) => (attribute.Type, attribute.Count, attribute.Normalized) switch
    {
        (VertexAttributeType.Float, 1, _) => Format.R32Sfloat,
        (VertexAttributeType.Float, 2, _) => Format.R32G32Sfloat,
        (VertexAttributeType.Float, 3, _) => Format.R32G32B32Sfloat,
        (VertexAttributeType.Float, 4, _) => Format.R32G32B32A32Sfloat,
        (VertexAttributeType.UInt, 1, _) => Format.R32Uint,
        (VertexAttributeType.UInt, 2, _) => Format.R32G32Uint,
        (VertexAttributeType.UInt, 3, _) => Format.R32G32B32Uint,
        (VertexAttributeType.UInt, 4, _) => Format.R32G32B32A32Uint,
        (VertexAttributeType.Int, 1, _) => Format.R32Sint,
        (VertexAttributeType.Int, 2, _) => Format.R32G32Sint,
        (VertexAttributeType.Int, 3, _) => Format.R32G32B32Sint,
        (VertexAttributeType.Int, 4, _) => Format.R32G32B32A32Sint,
        (VertexAttributeType.UnsignedByte, 4, true) => Format.R8G8B8A8Unorm,
        (VertexAttributeType.UnsignedByte, 4, false) => Format.R8G8B8A8Uint,
        (VertexAttributeType.Byte, 4, true) => Format.R8G8B8A8SNorm,
        _ => throw new NotSupportedException($"No vertex format for {attribute.Count} of {attribute.Type}.")
    };

    public void Dispose()
    {
        ObjectManager.Instance.VertexArrays.Remove(this);
        GC.SuppressFinalize(this);
    }

    /// <summary>Makes an array and its buffers the way a description says. What the asset manager calls.</summary>
    internal static bool TryCreate(in VertexArrayDescription description, out AssetCreationResult<VertexArray> result)
    {
        var manager = ObjectManager.Instance;
        var buffers = new Dictionary<VertexArraySlot, GpuBuffer>();

        foreach (var (slot, bufferDescription) in description.Buffers)
        {
            if (!manager.Buffers.TryCreate(bufferDescription, out var buffer))
            {
                foreach (var made in buffers.Values) manager.Buffers.Remove(made);

                result = new AssetCreationResult<VertexArray> { Status = AssetCreationStatus.Failed, Message = $"The {slot} buffer of a vertex array couldn't be made: {buffer.Message}" };
                return false;
            }

            buffers[slot] = buffer.Asset;
        }

        result = new AssetCreationResult<VertexArray> { Asset = new VertexArray(GraphicsDevice.Current, buffers), Status = AssetCreationStatus.Success, Message = string.Empty };
        return true;
    }

    protected override void DestroyCore() => device.DestroyVertexArray(this);
}
