namespace Horizon.Graphics;

/// <summary>
/// Anything that lives on the GPU and has a number the asset managers can tell it by. Vulkan has no small integers
/// for its objects, so every resource gets one from a counter when it is made. 0 is never handed out, it is what an
/// invalid resource has.
/// </summary>
public interface IGpuObject
{
    public uint Handle { get; }
}

/// <summary>
/// The base of every resource (buffers, textures, render targets, vertex arrays, shaders). Freed by whoever owns it
/// (the asset managers, mostly, see <see cref="ObjectManager"/>), which calls <see cref="Destroy"/> on the render
/// thread. The device doesn't free anything the GPU may still be reading, so what is destroyed mid-frame goes once the
/// frame is done with.
/// </summary>
public abstract class GpuResource : IGpuObject
{
    private static int nextHandle;

    /// <summary>The engine's number for the resource, 0 for one that isn't there.</summary>
    public uint Handle { get; }

    /// <summary>Whether the resource is there to be used. One that failed to be made, or was freed, isn't.</summary>
    public virtual bool IsValid => Handle != 0 && !IsDestroyed;

    /// <summary>What it is called in the log (and in the debugger, see <see cref="Named"/>), if anybody said.</summary>
    public string? Name
    {
        get => name;
        set
        {
            name = value;
            Named();
        }
    }

    private string? name;

    /// <summary>Called when the resource is given a name, for whoever tells the driver. Nothing by default.</summary>
    protected virtual void Named()
    { }

    /// <summary>Whether <see cref="Destroy"/> has been called.</summary>
    public bool IsDestroyed { get; private set; }

    protected GpuResource()
    {
        Handle = (uint)Interlocked.Increment(ref nextHandle);
    }

    /// <summary>For the placeholder resources that stand for nothing.</summary>
    protected GpuResource(bool invalid)
    {
        Handle = 0;
    }

    /// <summary>
    /// Frees what the resource has on the GPU. Render thread. Called once by whoever owns the resource.
    /// </summary>
    public void Destroy()
    {
        if (IsDestroyed || Handle == 0) return;

        IsDestroyed = true;
        DestroyCore();
    }

    protected abstract void DestroyCore();
}
