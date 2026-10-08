using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Horizon.Graphics.Vulkan;

/// <summary>
/// A buffer the CPU writes into from the front and the GPU reads from, started over every time its frame comes
/// round. It holds the uniform blocks of every draw of a frame (the params, the camera) and the staging of what is uploaded.
/// It grows when a frame wants more than it has, the old one going once the GPU is done with it.
/// </summary>
internal sealed unsafe class LinearArena
{
    private readonly VulkanContext context;
    private readonly VulkanMemory memory;
    private readonly BufferUsageFlags usage;

    public Buffer Buffer { get; private set; }
    public VulkanMemory.Allocation Memory { get; private set; }
    public ulong Capacity { get; private set; }
    public ulong Used { get; private set; }
    public byte* Mapped => Memory.Mapped;

    /// <summary>Goes up every time the buffer is made anew, so whoever wrote a descriptor for it knows to do it again.</summary>
    public int Generation { get; private set; }

    // What is to be let go of once the frame this arena belongs to is done
    public readonly List<(Buffer Buffer, VulkanMemory.Allocation Memory)> Retired = [];

    private readonly string name;

    public LinearArena(VulkanContext context, VulkanMemory memory, BufferUsageFlags usage, ulong capacity, string name)
    {
        this.context = context;
        this.memory = memory;
        this.usage = usage;
        this.name = name;
        Make(capacity);
    }

    private void Make(ulong capacity)
    {
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = capacity,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };

        VulkanContext.Check(context.Vk.CreateBuffer(context.Device, in info, null, out Buffer buffer), "making an arena");
        context.Name(ObjectType.Buffer, buffer.Handle, $"{name} of {capacity} bytes");
        context.Vk.GetBufferMemoryRequirements(context.Device, buffer, out MemoryRequirements requirements);

        var allocation = memory.Allocate(requirements, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPropertyFlags.DeviceLocalBit, forImage: false);
        VulkanContext.Check(context.Vk.BindBufferMemory(context.Device, buffer, allocation.Memory, allocation.Offset), "binding an arena's memory");

        Buffer = buffer;
        Memory = allocation;
        Capacity = capacity;
        Used = 0;
        Generation++;
    }

    /// <summary>Room for so many bytes at an alignment, growing if there isn't any. Where it starts, in the buffer.</summary>
    public ulong Take(ulong bytes, ulong alignment, ulong slack = 0)
    {
        ulong offset = (Used + alignment - 1) / alignment * alignment;
        if (offset + bytes + slack > Capacity)
        {
            // Twice what is wanted, so this doesn't happen again this frame
            Retired.Add((Buffer, Memory));
            Make(Math.Max(Capacity * 2, (offset + bytes + slack) * 2));
            offset = 0;
        }

        Used = offset + bytes;
        return offset;
    }

    public void Reset() => Used = 0;

    /// <summary>Frees what was retired, once the frame is known to be done.</summary>
    public void FreeRetired()
    {
        foreach (var (buffer, allocation) in Retired)
        {
            context.Vk.DestroyBuffer(context.Device, buffer, null);
            memory.Free(allocation);
        }

        Retired.Clear();
    }

    public void Dispose()
    {
        FreeRetired();
        context.Vk.DestroyBuffer(context.Device, Buffer, null);
        memory.Free(Memory);
    }
}

/// <summary>
/// Everything one frame in flight owns, its command pool and buffers, the semaphores that tie it to the swapchain,
/// its descriptor sets, its arenas, and whatever was destroyed while it was being drawn (which can't go until the
/// GPU is done with the frame). Two of these take turns.
/// </summary>
internal sealed unsafe class FrameResources : IDisposable
{
    private readonly VulkanContext context;

    public CommandPool CommandPool { get; }
    public Semaphore ImageAvailable { get; }
    public Semaphore RenderFinished { get; }
    public DescriptorAllocator Descriptors { get; }
    public LinearArena Uniforms { get; }
    public LinearArena Staging { get; }

    /// <summary>The command buffers recorded this frame, the last of them the one being recorded.</summary>
    public List<CommandBuffer> CommandBuffers { get; } = [];
    private int commandBuffersUsed;

    /// <summary>The value of the timeline the last submission of this frame signals, 0 before the first.</summary>
    public ulong TimelineValue { get; set; }

    /// <summary>What is to be let go of once the GPU is done with this frame.</summary>
    public List<Action> Retired { get; } = [];

    /// <summary>Whether the swapchain image's semaphore has been waited on by a submission of this frame yet.</summary>
    public bool AcquireWaited { get; set; }

    /// <summary>Whether a swapchain image was acquired for this frame at all.</summary>
    public bool HasImage { get; set; }

    // Which of the frames in flight this is, for the names
    private readonly int index;

    public FrameResources(VulkanContext context, VulkanMemory memory, int index)
    {
        this.context = context;
        this.index = index;

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = context.GraphicsFamily,
            Flags = CommandPoolCreateFlags.TransientBit
        };
        VulkanContext.Check(context.Vk.CreateCommandPool(context.Device, in poolInfo, null, out CommandPool pool), "making a command pool");
        CommandPool = pool;
        context.Name(ObjectType.CommandPool, pool.Handle, $"frame {index} commands");

        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        VulkanContext.Check(context.Vk.CreateSemaphore(context.Device, in semaphoreInfo, null, out Semaphore available), "making a semaphore");
        VulkanContext.Check(context.Vk.CreateSemaphore(context.Device, in semaphoreInfo, null, out Semaphore finished), "making a semaphore");
        ImageAvailable = available;
        RenderFinished = finished;
        context.Name(ObjectType.Semaphore, available.Handle, $"frame {index} image available");
        context.Name(ObjectType.Semaphore, finished.Handle, $"frame {index} render finished");

        Descriptors = new DescriptorAllocator(context, index);
        Uniforms = new LinearArena(context, memory, BufferUsageFlags.UniformBufferBit, 1024 * 1024, $"frame {index} uniforms");
        Staging = new LinearArena(context, memory, BufferUsageFlags.TransferSrcBit, 8 * 1024 * 1024, $"frame {index} staging");
    }

    /// <summary>A fresh command buffer, begun. The pool is reset by <see cref="Reset"/>, so the buffers are reused.</summary>
    public CommandBuffer BeginCommandBuffer()
    {
        if (commandBuffersUsed == CommandBuffers.Count)
        {
            var info = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = CommandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };

            VulkanContext.Check(context.Vk.AllocateCommandBuffers(context.Device, in info, out CommandBuffer made), "allocating a command buffer");
            context.Name(ObjectType.CommandBuffer, (ulong)made.Handle, $"frame {index} commands {CommandBuffers.Count}");
            CommandBuffers.Add(made);
        }

        CommandBuffer buffer = CommandBuffers[commandBuffersUsed++];
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        VulkanContext.Check(context.Vk.BeginCommandBuffer(buffer, in beginInfo), "beginning a command buffer");
        return buffer;
    }

    /// <summary>The frame is done on the GPU, so everything it had is free to be used again and what it retired goes.</summary>
    public void Reset()
    {
        foreach (var retired in Retired) retired();
        Retired.Clear();

        Uniforms.FreeRetired();
        Staging.FreeRetired();
        Uniforms.Reset();
        Staging.Reset();

        context.Vk.ResetCommandPool(context.Device, CommandPool, 0);
        commandBuffersUsed = 0;
        Descriptors.Reset();
        AcquireWaited = false;
        HasImage = false;
    }

    public void Dispose()
    {
        Reset();
        Descriptors.Dispose();
        Uniforms.Dispose();
        Staging.Dispose();
        context.Vk.DestroySemaphore(context.Device, ImageAvailable, null);
        context.Vk.DestroySemaphore(context.Device, RenderFinished, null);
        context.Vk.DestroyCommandPool(context.Device, CommandPool, null);
    }
}
