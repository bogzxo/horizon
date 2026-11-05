using System.Runtime.InteropServices;

namespace Horizon.Platform.Android.Native;

/// <summary>
/// The bits of libandroid.so's native window the surface needs. ANativeWindow_fromSurface hands back a window with a
/// reference taken on it, which ANativeWindow_release gives back. The size calls are plain native calls, no JNI.
/// See developer.android.com/ndk/reference/group/a-native-window.
/// </summary>
internal static unsafe class NativeWindow
{
    private const string LIBRARY = "libandroid.so";

    /// <summary>The window behind a Java Surface, with a reference held for the caller. Null when the surface has none.</summary>
    [DllImport(LIBRARY, EntryPoint = "ANativeWindow_fromSurface")]
    public static extern nint FromSurface(nint environment, nint surface);

    [DllImport(LIBRARY, EntryPoint = "ANativeWindow_release")]
    public static extern void Release(nint window);

    /// <summary>Width in pixels, or a negative error.</summary>
    [DllImport(LIBRARY, EntryPoint = "ANativeWindow_getWidth")]
    public static extern int Width(nint window);

    /// <summary>Height in pixels, or a negative error.</summary>
    [DllImport(LIBRARY, EntryPoint = "ANativeWindow_getHeight")]
    public static extern int Height(nint window);
}
