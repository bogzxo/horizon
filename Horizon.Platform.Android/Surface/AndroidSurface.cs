using Horizon.Platform.Android.Native;

using Java.Interop;

namespace Horizon.Platform.Android;

/// <summary>
/// The SurfaceView's native window as something the engine shows frames in. The host hands the Java Surface over from
/// its SurfaceHolder callback, this takes an ANativeWindow reference of its own on it, and the engine's render thread
/// makes its VkSurfaceKHR through <see cref="CreateVulkanSurface"/>.
/// <para>
/// Who lets go of what, and when. surfaceCreated (or the first surfaceChanged) calls <see cref="Acquire"/>, and
/// surfaceChanged calls <see cref="Resized"/> with the size Android says. surfaceDestroyed calls <see cref="Withdraw"/>
/// first, so no new Vulkan surface is made for a window on its way out, then has the engine detach its surface and
/// waits for the engine to say it has, and only then calls <see cref="Release"/>. Nothing in here waits on the engine
/// or the render thread, that handshake is the host's. Releasing while the GPU still has a swapchain on the window is
/// the host breaking its side, not something this class can see, Vulkan holds its own reference on the window so the
/// process doesn't fall over, but the swapchain is presenting into a surface Android has torn down.
/// </para>
/// <para>
/// One native window at a time. Acquire with one still held throws, Release then Acquire again is how a new
/// surface comes in after the app was in the background. Thread safe, the main thread acquires and releases while the
/// render thread makes Vulkan surfaces, and a lock keeps a release from happening half way through a create.
/// </para>
/// </summary>
public sealed class AndroidSurface : IPlatformSurface, IDisposable
{
    private static readonly string[] extensions = [VulkanAndroidSurface.SURFACE_EXTENSION, VulkanAndroidSurface.ANDROID_SURFACE_EXTENSION];

    private readonly Lock gate = new();
    private nint window;
    private bool withdrawn;
    private bool disposed;

    // Width in the low half and height in the high half, so one read gets both of the same moment without a lock
    private long size;

    public IReadOnlyList<string> VulkanInstanceExtensions => extensions;

    /// <summary>Whether a native window is held and not on its way out.</summary>
    public bool IsAvailable
    {
        get
        {
            lock (gate) return window != 0 && !withdrawn && !disposed;
        }
    }

    /// <summary>
    /// Whether a native window reference is held at all, going away or not. True from Acquire to Release, which is the
    /// stretch the host must not acquire another in.
    /// </summary>
    public bool Holding
    {
        get
        {
            lock (gate) return window != 0;
        }
    }

    public SurfaceSize PixelSize
    {
        get
        {
            long packed = Interlocked.Read(ref size);
            return new SurfaceSize((uint)(packed & 0xFFFFFFFF), (uint)(packed >> 32));
        }
    }

    /// <summary>A phone's picture goes out unturned and the compositor turns it, see notes/android-plan.md stage 5.</summary>
    public SurfaceTransformPolicy TransformPolicy => SurfaceTransformPolicy.Identity;

    /// <summary>How many native windows have been acquired so far, for a log or a check that a new one came in.</summary>
    public int Generation { get; private set; }

    /// <summary>
    /// Takes a native window reference on the surface Android handed the SurfaceView. A thread that's attached to the
    /// Java VM, which is the main thread in a SurfaceHolder callback. The Java surface isn't kept, the native window
    /// reference is what keeps the window alive.
    /// </summary>
    public void Acquire(global::Android.Views.Surface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (!surface.IsValid) throw new ArgumentException("The surface isn't valid, there's no native window behind it.", nameof(surface));

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (window != 0) throw new InvalidOperationException("A native window is held already. Release it, after the engine has detached, before acquiring another.");

            nint made = NativeWindow.FromSurface(JniEnvironment.EnvironmentPointer, surface.Handle);
            GC.KeepAlive(surface);
            if (made == 0) throw new InvalidOperationException("ANativeWindow_fromSurface gave back no window for the surface.");

            window = made;
            withdrawn = false;
            Generation++;
            Store(NativeWindow.Width(made), NativeWindow.Height(made));
        }
    }

    /// <summary>
    /// The size surfaceChanged reports, in pixels. Called on the main thread, PixelSize reads it from any thread without
    /// going near JNI.
    /// </summary>
    public void Resized(int width, int height) => Store(width, height);

    /// <summary>
    /// Says the window is going (surfaceDestroyed came in). From here on no Vulkan surface is made for it and it isn't
    /// available, but it's still held, the engine may be presenting into it until it has detached.
    /// </summary>
    public void Withdraw()
    {
        lock (gate)
        {
            withdrawn = true;
            Store(0, 0);
        }
    }

    /// <summary>
    /// Gives the native window reference back. Only once the engine has said it detached (its VkSurfaceKHR destroyed),
    /// see the class remarks. Nothing happens when none is held.
    /// </summary>
    public void Release()
    {
        lock (gate)
        {
            if (window == 0) return;
            NativeWindow.Release(window);
            window = 0;
            withdrawn = true;
            Store(0, 0);
        }
    }

    /// <summary>
    /// Makes the VkSurfaceKHR for the window held, on the render thread. Throws when there's no window, it's going, or
    /// Vulkan says no, and the message says which.
    /// </summary>
    public ulong CreateVulkanSurface(nint instance, Func<string, nint> instanceFunction)
    {
        ArgumentNullException.ThrowIfNull(instanceFunction);
        if (instance == 0) throw new ArgumentException("There's no Vulkan instance.", nameof(instance));

        nint create = instanceFunction(VulkanAndroidSurface.CREATE_FUNCTION);
        if (create == 0) throw new InvalidOperationException($"The instance has no {VulkanAndroidSurface.CREATE_FUNCTION}, was it made with {VulkanAndroidSurface.ANDROID_SURFACE_EXTENSION}?");

        // Held across the create so a Release on the main thread can't take the window out from under it
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (window == 0) throw new InvalidOperationException("There's no native window, the SurfaceView hasn't got a surface right now.");
            if (withdrawn) throw new InvalidOperationException("The native window is going away, no surface is made for it.");

            int result = VulkanAndroidSurface.Create(create, instance, window, out ulong surface);
            if (result != VulkanAndroidSurface.SUCCESS) throw new InvalidOperationException($"{VulkanAndroidSurface.CREATE_FUNCTION} failed with {VulkanAndroidSurface.Describe(result)}.");
            if (surface == 0) throw new InvalidOperationException($"{VulkanAndroidSurface.CREATE_FUNCTION} said it worked and gave back no surface.");
            return surface;
        }
    }

    /// <summary>
    /// Lets go of a native window still held. For the host's own teardown, after the engine is gone, the same rule as
    /// <see cref="Release"/>. Twice does nothing.
    /// </summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        Release();
    }

    // A negative size is an error from the window, which counts as nothing to draw into
    private void Store(int width, int height)
    {
        uint w = width > 0 ? (uint)width : 0, h = height > 0 ? (uint)height : 0;
        Interlocked.Exchange(ref size, (long)w | ((long)h << 32));
    }
}
