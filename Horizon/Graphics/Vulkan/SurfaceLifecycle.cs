namespace Horizon.Graphics.Vulkan;

/// <summary>
/// The bookkeeping of the device's surface, apart from the Vulkan calls so it can be tested without a GPU. Which state
/// the surface is in, whether a frame is open (the surface is only ever changed between frames), and how often a lost
/// surface is tried again. Render thread only, like the device.
/// </summary>
internal sealed class SurfaceLifecycle
{
    /// <summary>How many frames go by between tries at making a lost surface again.</summary>
    public const int RECOVER_EVERY = 30;

    private int recoverIn;

    public SurfaceState State { get; private set; } = SurfaceState.Attached;

    /// <summary>Whether a frame has been begun and not ended yet.</summary>
    public bool InFrame { get; private set; }

    /// <summary>How many times the surface was lost, for the log and the tests.</summary>
    public int Losses { get; private set; }

    /// <summary>Notes a frame being begun. A second BeginFrame without an EndFrame is a bug in whoever drives the frames.</summary>
    public void EnterFrame()
    {
        if (InFrame)
            throw new InvalidOperationException("A frame was begun while the one before it was still open.");
        InFrame = true;
    }

    public void LeaveFrame() => InFrame = false;

    /// <summary>Throws in the middle of a frame, where the swapchain may have a picture out that has to be shown first.</summary>
    public void RequireBetweenFrames(string doing)
    {
        if (InFrame)
            throw new InvalidOperationException($"The surface can't be {doing} in the middle of a frame, only between EndFrame and the next BeginFrame.");
    }

    public void Attached()
    {
        State = SurfaceState.Attached;
        recoverIn = 0;
    }

    public void Detached() => State = SurfaceState.Detached;

    /// <summary>The driver said the surface is gone. A surface the host took away stays taken away.</summary>
    public void Lost()
    {
        if (State != SurfaceState.Attached)
            return;

        State = SurfaceState.Lost;
        Losses++;
        recoverIn = 0;
    }

    /// <summary>Whether a lost surface is to be made again this frame, every <see cref="RECOVER_EVERY"/> frames after the first try.</summary>
    public bool ShouldRecover()
    {
        if (State != SurfaceState.Lost)
            return false;
        if (recoverIn > 0)
        {
            recoverIn--;
            return false;
        }

        return true;
    }

    /// <summary>A try at making a lost surface again came to nothing, so the next one waits a while.</summary>
    public void RecoverFailed() => recoverIn = RECOVER_EVERY;

    /// <summary>Whether a picture may be taken off the swapchain, which wants a surface and a swapchain made for it.</summary>
    public bool MayAcquire(bool swapchainLive) => State == SurfaceState.Attached && swapchainLive;

    /// <summary>The extensions a surface wants that an instance hasn't got, in the order they were wanted.</summary>
    public static IReadOnlyList<string> MissingExtensions(IReadOnlyList<string> wanted, IReadOnlyList<string> have)
    {
        List<string>? missing = null;
        foreach (string name in wanted)
        {
            bool found = false;
            foreach (string had in have)
            {
                if (string.Equals(had, name, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                (missing ??= []).Add(name);
        }

        return missing ?? (IReadOnlyList<string>)[];
    }
}
