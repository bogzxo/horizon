using System.Runtime.InteropServices;
using System.Text;

namespace Horizon.Platform.Android.Native;

/// <summary>
/// libandroid.so's asset manager, for reading the APK's assets where they are without unpacking anything. The native
/// manager may be shared between threads, an AAsset may not, which is why every stream opens its own.
/// See developer.android.com/ndk/reference/group/asset.
/// </summary>
internal static unsafe class NativeAssets
{
    private const string LIBRARY = "libandroid.so";

    // The access modes of AAssetManager_open, in the order asset_manager.h has them
    public const int MODE_UNKNOWN = 0, MODE_RANDOM = 1, MODE_STREAMING = 2, MODE_BUFFER = 3;

    // lseek's whence
    public const int SEEK_SET = 0, SEEK_CUR = 1, SEEK_END = 2;

    /// <summary>The native manager behind a Java AssetManager. The caller keeps the Java one alive for as long as this is used.</summary>
    [DllImport(LIBRARY, EntryPoint = "AAssetManager_fromJava")]
    public static extern nint ManagerFromJava(nint environment, nint assetManager);

    [DllImport(LIBRARY, EntryPoint = "AAssetManager_open")]
    private static extern nint Open(nint manager, byte* name, int mode);

    /// <summary>Bytes read, zero at the end, below zero for an error.</summary>
    [DllImport(LIBRARY, EntryPoint = "AAsset_read")]
    public static extern int Read(nint asset, void* buffer, nuint count);

    /// <summary>The new position, or minus one for an error.</summary>
    [DllImport(LIBRARY, EntryPoint = "AAsset_seek64")]
    public static extern long Seek(nint asset, long offset, int whence);

    [DllImport(LIBRARY, EntryPoint = "AAsset_getLength64")]
    public static extern long Length(nint asset);

    [DllImport(LIBRARY, EntryPoint = "AAsset_close")]
    public static extern void Close(nint asset);

    /// <summary>Opens an asset by its name in the APK, zero when there's no such file (a folder isn't one).</summary>
    public static nint Open(nint manager, string name, int mode)
    {
        int length = Encoding.UTF8.GetMaxByteCount(name.Length) + 1;
        byte[]? rented = null;
        Span<byte> bytes = length <= 512 ? stackalloc byte[length] : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(length));
        try
        {
            int written = Encoding.UTF8.GetBytes(name, bytes);
            bytes[written] = 0;
            fixed (byte* text = bytes) return Open(manager, text, mode);
        }
        finally
        {
            if (rented is not null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
