using Horizon.Content;
using Horizon.Platform.Android.Native;

using Java.Interop;

namespace Horizon.Platform.Android;

/// <summary>
/// The game's content out of the APK's assets, read where it is with the NDK's asset manager and never unpacked. The
/// assets are packed at the same logical paths the engine asks for (Assets/fighters/x.aseprite, shaders/compiled/..),
/// so IndexedContent does the path rules, case sensitive with forward slashes, no leaving the root, Win32 patterns
/// and the folder listings, from an index walked once when this is made. Package content never changes while the app
/// runs, so every file's time is DateTime.MinValue. There's no disk under it, a path that isn't in the APK is not found.
/// <para>
/// The application context's AssetManager is held for as long as this is, which is what keeps the native manager
/// valid (AAssetManager_fromJava says the caller holds the Java one). It belongs to the app, so nothing here disposes it.
/// </para>
/// </summary>
public sealed class AndroidContent : IndexedContent
{
    /// <summary>The folders of the assets indexed when no others are asked for, the engine's and the game's files and the shader pack.</summary>
    public static readonly IReadOnlyList<string> DefaultRoots = ["Assets", "shaders"];

    private readonly global::Android.Content.Res.AssetManager assets;
    private readonly nint manager;

    /// <summary>The folders of the assets that were indexed.</summary>
    public IReadOnlyList<string> Roots { get; }

    private AndroidContent(global::Android.Content.Res.AssetManager assets, nint manager, IReadOnlyList<string> roots)
        : base(AssetIndex.Build(assets, manager, roots))
    {
        this.assets = assets;
        this.manager = manager;
        Roots = roots;
    }

    /// <summary>
    /// Indexes the APK's assets under some folders (<see cref="DefaultRoots"/> when none are given). Call it on a thread
    /// attached to the Java VM, the main thread or one .NET made, as it goes through JNI. A big APK takes a moment, so
    /// it's done once as the platform comes up.
    /// </summary>
    public static AndroidContent Open(global::Android.Content.Context context, IEnumerable<string>? roots = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        global::Android.Content.Res.AssetManager assets = (context.ApplicationContext ?? context).Assets
            ?? throw new InvalidOperationException("The context has no asset manager.");

        nint manager = NativeAssets.ManagerFromJava(JniEnvironment.EnvironmentPointer, assets.Handle);
        if (manager == 0) throw new InvalidOperationException("AAssetManager_fromJava gave back no asset manager.");

        string[] indexed = (roots ?? DefaultRoots).Select(root => root.Replace('\\', '/').Trim('/')).Where(root => root.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return new AndroidContent(assets, manager, indexed);
    }

    // The path comes resolved and known to be in the index
    protected override Stream OpenFile(string path)
    {
        GC.KeepAlive(assets);
        return AssetStream.Open(manager, path);
    }
}
