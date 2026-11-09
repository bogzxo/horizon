using Horizon.Core;

namespace Horizon.Platform;

/// <summary>
/// A platform the engine can run on, what makes a fresh host for every engine and the speakers for an audio device
/// made without one. Not disposable, nothing in it lives longer than what it makes. A desktop program installs the
/// desktop's in its Main (Horizon.Platform.Desktop's DesktopPlatform.Install), an Android activity hands its own host in.
/// </summary>
public interface IPlatform
{
    /// <summary>Makes the host for one engine.</summary>
    IPlatformHost CreateHost(PlatformHostRequest request);

    /// <summary>Makes speakers for an audio device that has no host to ask, null for none.</summary>
    IAudioOutput? CreateAudioOutput();
}

/// <summary>
/// The platform a program said it runs on, for every engine it makes that wasn't handed one of its own. Installed once in
/// Main, before the first engine. A factory, never a host, so two engines never share one.
/// </summary>
public static class PlatformInstallation
{
    private static IPlatform? current;

    /// <summary>The installed platform, null until somebody installed one.</summary>
    public static IPlatform? Current => Volatile.Read(ref current);

    /// <summary>Installs the platform engines are made on from now on.</summary>
    public static void Install(IPlatform platform) => Volatile.Write(ref current, platform ?? throw new ArgumentNullException(nameof(platform)));

    /// <summary>Takes the installed platform out again, for tests.</summary>
    internal static void Uninstall() => Volatile.Write(ref current, null);

    /// <summary>The platform to make an engine on, the one it was handed or else the installed one.</summary>
    internal static IPlatform Resolve(IPlatform? handed) => handed ?? Current ?? throw new InvalidOperationException(
        "There's no platform to run the engine on. A desktop program calls Horizon.Platform.Desktop.DesktopPlatform.Install() " +
        "in its Main before it makes the engine, or hands one in through GameEngineConfiguration.Platform.");

    /// <summary>Helper method to make a host and check it came out whole.</summary>
    internal static IPlatformHost CreateHost(IPlatform platform, PlatformHostRequest request) =>
        platform.CreateHost(request) ?? throw new InvalidOperationException($"{platform.GetType().Name} made no host.");
}
