namespace Horizon.Platform.iOS;

/// <summary>
/// An iPhone or an iPad as a platform the engine runs on. Not installed globally the way the desktop's is, the app
/// delegate hands it to its engine through GameEngineConfiguration.Platform, which HorizonAppDelegate does. One engine
/// at a time, the process has one session and the session one host, see <see cref="IosSession"/>.
/// </summary>
public sealed class IosPlatform : IPlatform
{
    private readonly IosSession session;

    internal IosPlatform(IosSession session) => this.session = session;

    public IPlatformHost CreateHost(PlatformHostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return session.MakeHost(request);
    }

    /// <summary>AVAudioEngine, for an audio device made without a host. The engine checks HORIZON_AUDIO itself.</summary>
    public IAudioOutput? CreateAudioOutput() => session.TrackAudio(new IosAudioOutput());
}
