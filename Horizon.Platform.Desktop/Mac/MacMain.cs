using System.Runtime.Versioning;

namespace Horizon.Platform.Desktop;

/// <summary>
/// The one rule of AppKit, that it is only called from the main thread of the process. On a Mac that's the thread SDL
/// hears the system on and the desktop host's platform thread, so a call that has to be made from another one is handed to
/// the host to make there (<see cref="Poster"/>, set once by <see cref="DesktopHost"/> when it starts).
/// </summary>
[SupportedOSPlatform("macos")]
public static class MacMain
{
    private static Action<Action>? poster;

    /// <summary>What has something done on the main thread when it next gets round to it. Set by the desktop host.</summary>
    public static Action<Action>? Poster
    {
        get => poster;
        set => poster = value;
    }

    /// <summary>Whether the calling thread is the process's main thread, <c>[NSThread isMainThread]</c>.</summary>
    public static bool IsMainThread
    {
        get
        {
            if (!OperatingSystem.IsMacOS())
                return false;

            return MacObjC.SendBool(MacObjC.Class("NSThread"), MacObjC.Sel("isMainThread"));
        }
    }

    /// <summary>
    /// Does something on the main thread: right now when this is it, otherwise when the host gets to it. With no host to hand it to
    /// (it isn't started, or it's gone) the work is not done, which is the right thing for what is AppKit's.
    /// Never waits for it.
    /// </summary>
    public static void Run(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!OperatingSystem.IsMacOS())
            return;

        if (IsMainThread)
        {
            using var pool = MacObjC.Pool();
            work();
            return;
        }

        poster?.Invoke(() =>
        {
            using var pool = MacObjC.Pool();
            work();
        });
    }
}
