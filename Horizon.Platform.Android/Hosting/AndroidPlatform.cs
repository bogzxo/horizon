namespace Horizon.Platform.Android;

/// <summary>
/// A phone or a tablet as a platform the engine runs on. Not installed globally the way the desktop's is, an activity
/// hands it to its engine through GameEngineConfiguration.Platform, which HorizonActivity does. One engine at a time,
/// the process has one session and the session one host, see <see cref="AndroidSession"/>.
/// </summary>
public sealed class AndroidPlatform : IPlatform
{
    private readonly AndroidSession session;

    internal AndroidPlatform(AndroidSession session) => this.session = session;

    public IPlatformHost CreateHost(PlatformHostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return session.MakeHost(request);
    }

    /// <summary>AAudio, for an audio device made without a host. The engine checks HORIZON_AUDIO itself.</summary>
    public IAudioOutput? CreateAudioOutput() => session.TrackAudio(new AAudioOutput());
}
