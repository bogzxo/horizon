using System.Runtime.InteropServices;

namespace Horizon.Platform.Desktop;

// A hidden window brought back (BringToFront, a program that sleeps between uses woken) has no picture in it until the
// program has drawn one: its frames were held while it was hidden, and Windows showed what it had in the meantime, the
// white of the window's class, a whole screen of it for a maximised or fullscreen window. On Windows it comes back
// cloaked instead (DWM's cloak, shown as far as everybody is concerned, sized, maximised or fullscreen as it was, its
// swapchain made again to that, just not drawn by the compositor) and is uncovered once frames have been presented in it.
public sealed partial class DesktopWindow
{
    // Frames still to be presented before a cloaked window is uncovered, 0 for none waiting
    private int framesToReveal;

    // Two, the first may still be at the size the window had before it was shown (the resize is heard a frame late)
    private const int FRAMES_BEFORE_REVEAL = 2;

    // The longest a window stays cloaked waiting on frames, a program that never draws again still gets its window
    private const int REVEAL_AT_THE_LATEST_MS = 400;

    private Timer? revealAtTheLatest;

    /// <summary>
    /// Helper method to show the window covered, so nothing of it is seen until it has frames in it. Whether it did,
    /// Windows only. Thread of the window.
    /// </summary>
    private bool ShowCloaked()
    {
        if (!OperatingSystem.IsWindows() || _window.IsVisible || _window.Native?.Win32 is not { Hwnd: not 0 } win32)
            return false;

        int cloak = 1;
        if (DwmSetWindowAttribute(win32.Hwnd, DWMWA_CLOAK, ref cloak, sizeof(int)) != 0)
            return false;

        Volatile.Write(ref framesToReveal, FRAMES_BEFORE_REVEAL);
        revealAtTheLatest?.Dispose();
        revealAtTheLatest = new Timer(_ => PostToWindow(Reveal), null, REVEAL_AT_THE_LATEST_MS, Timeout.Infinite);
        return true;
    }

    /// <summary>A frame was presented in the window, the one a cloaked window waits for. The thread that draws.</summary>
    public void FramePresented()
    {
        if (Volatile.Read(ref framesToReveal) > 0 && Interlocked.Decrement(ref framesToReveal) == 0)
            PostToWindow(Reveal);
    }

    /// <summary>Helper method to uncover a window shown cloaked, and put it in front. Thread of the window.</summary>
    private void Reveal()
    {
        Volatile.Write(ref framesToReveal, 0);
        revealAtTheLatest?.Dispose();
        revealAtTheLatest = null;

        if (OperatingSystem.IsWindows() && _window.Native?.Win32 is { Hwnd: not 0 } win32)
        {
            int cloak = 0;
            DwmSetWindowAttribute(win32.Hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
        }

        Foreground();
    }

    private const int DWMWA_CLOAK = 13;

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
