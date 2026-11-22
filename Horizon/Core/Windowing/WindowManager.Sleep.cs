using System.Runtime;
using System.Runtime.InteropServices;

using Horizon.Core.Threading;
using Horizon.Logging;

namespace Horizon.Core;

// An application that stays running between uses, asleep with its window hidden. The device and the window are kept,
// so waking is quick, and everything else stops: the simulation doesn't tick, no frames are drawn, the window's
// thread waits on the system, the system's timer goes back to its usual pace. The swapchain and the surface are let go
// of, and the memory that can be is given back to the system. What's left awake is the graphics driver's own: NVIDIA's
// keeps a thread looking in a hundred or so times a second for as long as there's a device, swapchain or not.
public partial class WindowManager
{
    /// <summary>
    /// Puts an application to sleep: the window hidden, nothing ticking or drawing, the surface let go of and the memory
    /// that can be given back, until <see cref="Resume"/>. Whoever calls it has nothing more asked of the program, a
    /// scene that does nothing swapped in first. Hides the window again if it's asleep already. Nothing for a game.
    /// From any thread.
    /// </summary>
    public void Sleep()
    {
        if (Mode != RunMode.Application)
            return;

        Hide();
        if (dormant)
            return;

        Dormant = true;
        LoopTiming.RestTimer(true);
        RequestSurfaceDetach().ContinueWith(_ => GiveMemoryBack(), TaskScheduler.Default);
    }

    /// <summary>
    /// Wakes an application put to sleep with <see cref="Sleep"/>: ticks and frames go on and the surface is given back
    /// before the next frame. The window stays hidden until whoever woke it shows it (BringToFront), once it has
    /// something to show. From any thread.
    /// </summary>
    public void Resume()
    {
        if (!dormant)
            return;

        LoopTiming.RestTimer(false);
        RequestSurfaceAttach();
        Dormant = false;
    }

    /// <summary>Helper method to give the system back what memory can be, everything left over collected and compacted.</summary>
    private void GiveMemoryBack()
    {
        if (!dormant)
            return;

        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        Log.Info($"[{Name}] Asleep, {GC.GetTotalMemory(false) / (1024 * 1024)} MB of objects kept, {GC.GetGCMemoryInfo().TotalCommittedBytes / (1024 * 1024)} MB committed.");

        if (dormant && OperatingSystem.IsWindows())
            SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSize(nint process, nint least, nint most);
}
