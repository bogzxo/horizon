using Horizon.Core;

namespace Horizon.Platform.Desktop;

/// <summary>
/// The engine on a desktop, one per engine. It owns the window, its input and its surface, the queue of work for the
/// window's thread, and the loop that thread runs until the window closes. Made by <see cref="DesktopPlatform"/>,
/// disposed by the window manager once the GPU let go of the surface, on the window's thread.
/// </summary>
public sealed class DesktopHost : IPlatformHost, IPlatformLoop
{
    private readonly PlatformHostRequest request;
    private readonly PlatformDispatcher dispatcher;
    private IPlatformEvents? events;
    private DesktopWindow? window;
    private DesktopInput? input;
    private SilkSurface? surface;
    private readonly List<SilkGamepadSource> gamepads = [];
    private volatile bool disposed, closed;

    internal DesktopHost(PlatformHostRequest request)
    {
        this.request = request;
        dispatcher = new PlatformDispatcher(() => window?.WakeWindow());

        // A desktop window has it all, the drag area only where SDL drags it (Wayland can't place a window itself)
        Features = PlatformFeatures.Window | PlatformFeatures.DisplaySettings | PlatformFeatures.PointerCapture |
            PlatformFeatures.Clipboard | PlatformFeatures.FileSystem |
            (request.ReadsGamepads ? PlatformFeatures.Gamepads : 0) |
            (request.Window.CustomFrame ? PlatformFeatures.WindowFrame | PlatformFeatures.CornerRadius : 0) |
            (request.Window.CustomFrame && DesktopWindow.UsesSdl ? PlatformFeatures.DragArea : 0) |
            (request.Window.CustomFrame && OperatingSystem.IsWindows() ? PlatformFeatures.HotKeys : 0) |
            PlatformFeatures.DropDown;

        SurfaceAhead = Win32SurfaceAhead.ForThisMachine(DesktopWindow.UsesSdl);
    }

    public PlatformFeatures Features { get; }

    /// <summary>The window, there from <see cref="Attach"/> on.</summary>
    public DesktopWindow Window => window ?? throw new InvalidOperationException("The desktop host has no window until the engine attached it.");

    IPlatformWindow? IPlatformHost.Window => window;

    public IPlatformSurfaceAhead? SurfaceAhead { get; }

    public IPlatformSurface Surface => surface ?? throw new InvalidOperationException("The desktop window has no surface until it is initialized.");

    public IPlatformInput Input => input ?? throw new InvalidOperationException("The desktop window has no input until it is initialized.");

    public PlatformState State => window?.State ?? throw new InvalidOperationException("The desktop window has no state until it is initialized.");

    internal IPlatformEvents Events => events ?? throw new InvalidOperationException("Nobody is listening to the desktop host yet.");

    internal bool HasEvents => events is not null;

    public IAudioOutput? CreateAudioOutput() => new SdlAudioOutput();

    public void Attach(IPlatformEvents events)
    {
        if (this.events is not null) throw new InvalidOperationException("The desktop host is attached already.");
        this.events = events;
        window = new DesktopWindow(this, request);
    }

    public void Initialize()
    {
        if (window is null) throw new InvalidOperationException("The desktop host has to be attached before it's initialized.");

        dispatcher.BindToCurrentThread();
        window.Initialize();

        var context = window.NativeInput ?? throw new InvalidOperationException("The desktop window loaded without an input context.");
        input = new DesktopInput(this, context, window.PointerToPixels, window.Win32Handle);

        // On a Mac making the surface is SDL putting a Metal view into the window, which only the main thread may do,
        // see SilkSurface
        var vkSurface = window.Native.VkSurface ?? throw new InvalidOperationException("The window has no Vulkan surface, is the Vulkan loader installed?");
        DesktopWindow made = window;
        surface = new SilkSurface(vkSurface, OperatingSystem.IsMacOS() ? Invoke : null, () => made.FramebufferPixels, () => !disposed && !closed);
    }

    public bool IsPlatformThread => dispatcher.IsPlatformThread;

    public void Post(Action work) => dispatcher.Post(work);

    public void Invoke(Action work) => dispatcher.Invoke(work);

    public void Wake() => window?.WakeWindow();

    public void RequestClose() => window?.RequestClose();

    /// <summary>Helper method to do whatever was posted to the window's thread. Thread of the window.</summary>
    internal void DoWork() => dispatcher.Drain();

    /// <summary>Opens a feed of the controllers, sampled on the window's thread once it's started.</summary>
    internal SilkGamepadSource OpenGamepads(Silk.NET.Input.IInputContext context) => new(context, Post, Sampled);

    // A feed started or let go of, on the window's thread
    private void Sampled(SilkGamepadSource source, bool sampled)
    {
        if (sampled) gamepads.Add(source);
        else gamepads.Remove(source);
    }

    /// <summary>Helper method to have every controller feed look at its devices. Thread of the window, after it heard the system.</summary>
    internal void SampleGamepads()
    {
        for (int index = 0; index < gamepads.Count; index++)
            gamepads[index].Sample();
    }

    public void Run()
    {
        Window.RunUntilClosed();

        // No more frames are to be shown in it from here on
        closed = true;
    }

    public void PumpUntil(Task done)
    {
        while (!done.IsCompleted)
        {
            dispatcher.Drain();
            Window.WaitForWork();
        }

        dispatcher.Drain();
    }

    public void Finish() => window?.Finish();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        dispatcher.Close();
        foreach (var source in gamepads.ToArray())
            source.DisposeHere();
        gamepads.Clear();
        input?.Dispose();
        input = null;
        window?.Dispose();
    }
}
