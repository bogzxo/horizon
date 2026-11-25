namespace Horizon.Platform;

/// <summary>
/// Tells the application being quit (a Mac's Cmd+Q, the Dock's Quit, logout) from its window being closed (the red
/// button), which SDL says the same way to the program that listens: it sends <c>SDL_QUIT</c> for both when the window is the
/// last one. What differs is what came first. A window closed says so (<c>SDL_WINDOWEVENT_CLOSE</c>) just before the quit
/// that follows it, an application quit comes alone. Pure, so it is tested anywhere; the platform that listens feeds it.
/// From any thread.
/// </summary>
public sealed class AppQuitTracker
{
    private readonly object gate = new();
    private bool closeSeen, quitPending;

    /// <summary>The window was asked to close, which is followed by a quit of SDL's that isn't the application's.</summary>
    public void WindowCloseSeen()
    {
        lock (gate)
            closeSeen = true;
    }

    /// <summary>The system's quit came in.</summary>
    public void QuitSeen()
    {
        lock (gate)
        {
            // The one a window close brings along with it
            if (closeSeen)
                closeSeen = false;
            else
                quitPending = true;
        }
    }

    /// <summary>
    /// Whether the application was asked to quit since the last time this was asked, and so what is being closed now is a
    /// quit and not a window. Asked once for each close, it forgets.
    /// </summary>
    public bool TakeQuit()
    {
        lock (gate)
        {
            bool quit = quitPending;
            quitPending = false;
            closeSeen = false;
            return quit;
        }
    }
}
