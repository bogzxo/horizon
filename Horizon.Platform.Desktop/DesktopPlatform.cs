namespace Horizon.Platform.Desktop;

/// <summary>
/// The desktop as a platform the engine runs on, Windows, a Mac and Linux. A program says so first thing in its Main,
/// before it makes its engine.
/// <code>
/// DesktopPlatform.Install();
/// using var engine = new GameEngine(config);
/// engine.Run&lt;MainMenuScene&gt;();
/// </code>
/// Every engine gets a host of its own, nothing is shared between two.
/// </summary>
public sealed class DesktopPlatform : IPlatform
{
    /// <summary>The desktop platform. It has no state, the hosts it makes do.</summary>
    public static DesktopPlatform Instance { get; } = new();

    private DesktopPlatform() { }

    /// <summary>Installs the desktop as the platform engines are made on, see <see cref="PlatformInstallation"/>.</summary>
    public static void Install() => PlatformInstallation.Install(Instance);

    public IPlatformHost CreateHost(PlatformHostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        DesktopWindow.RegisterPlatform(request.Window.AppId);
        return new DesktopHost(request);
    }

    public IAudioOutput? CreateAudioOutput() => new SdlAudioOutput();
}
