using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Core;

namespace Horizon.Platform.Desktop;

// Files dragged onto the window from outside the program. On Windows the window is a drop target of OLE's (see
// WindowsDropTarget), which hears what is held as it comes over the window and moves about on it, so the program can say
// what letting go would do. Everywhere else, and on Windows when OLE wouldn't have the window, the drop is what GLFW or SDL
// say once the files are let go of (SDL one file an event, GLFW all at once), gathered over one listen to the system into
// a single drop at wherever the pointer is.
public sealed partial class DesktopWindow
{
    // The files GLFW or SDL said were dropped in this listen to the system, handed on once it's done
    private readonly List<string> droppedFiles = [];

    // The window's OLE drop target on Windows, null where there's none
    private WindowsDropTarget? dropTarget;

    /// <summary>Helper method to start hearing files dropped on the window. Thread of the window, once it is made.</summary>
    private void ListenForFileDrops()
    {
        _window.FileDrop += HeardFileDrop;
        if (OperatingSystem.IsWindows() && Win32Handle is not 0 and var hwnd)
            dropTarget = WindowsDropTarget.Start(hwnd, drop => { if (host.HasEvents) Events.FilesDragged(drop); });
    }

    /// <summary>Helper method for files GLFW or SDL said were dropped, kept until the listen is over. Thread of the window.</summary>
    private void HeardFileDrop(string[] paths)
    {
        foreach (string path in paths)
            if (!string.IsNullOrEmpty(path))
                droppedFiles.Add(path);
    }

    /// <summary>Helper method to hand on what was dropped in the listen that just ended, as one drop. Thread of the window.</summary>
    private void HandOnFileDrops()
    {
        if (droppedFiles.Count == 0)
            return;

        string[] paths = [.. droppedFiles];
        droppedFiles.Clear();
        (Vector2 at, bool known) = PointerNow();
        if (host.HasEvents)
            Events.FilesDragged(new FileDrop(FileDropKind.Drop, paths, at, known));
    }

    /// <summary>
    /// Helper method to find where the pointer is over the window, in pixels, asked of Windows on Windows (GLFW last heard
    /// it before the drag began) and of the input everywhere else. Thread of the window.
    /// </summary>
    private (Vector2 At, bool Known) PointerNow()
    {
        if (OperatingSystem.IsWindows() && Win32Handle is not 0 and var hwnd && WindowsDropTarget.CursorInClient(hwnd, out Vector2 client))
            return (client, true);

        if (_input is { Mice.Count: > 0 } input)
            return (PointerToPixels(input.Mice[0].Position), true);

        return (Vector2.Zero, false);
    }

    /// <summary>Helper method to let go of the drop target, before the window goes. Thread of the window.</summary>
    private void StopListeningForFileDrops()
    {
        _window.FileDrop -= HeardFileDrop;
        if (OperatingSystem.IsWindows())
            dropTarget?.Dispose();
        dropTarget = null;
    }
}
