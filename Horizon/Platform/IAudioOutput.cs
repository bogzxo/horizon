namespace Horizon.Platform;

/// <summary>
/// Fills a buffer of interleaved 32 bit floats, as many whole frames as fit, and says whether anything was heard in
/// it. False means the buffer is silence, which an output that queues may skip queueing. Called on the output's own
/// thread, never on the game's.
/// </summary>
public delegate bool AudioRender(Span<float> output);

/// <summary>
/// What an output plays. Samples are always interleaved 32 bit floats, so only the rate, the channels and how many
/// frames go at a time are left to say.
/// </summary>
public readonly record struct AudioFormat(int SampleRate, int Channels, int FramesPerBuffer);

/// <summary>
/// The platform's speakers, SDL's queue on desktop and an AAudio stream on a phone later. The engine opens it, hands it
/// what fills the buffers, and the output asks for buffers on its own thread whenever it wants one. No native types in
/// here, so a host can be written without the engine's audio code.
/// <para>
/// The order is Open, then Start once, then any number of Pause and Resume, then Dispose. Open throws when there's no
/// device to be had, which the engine takes as no sound and carries on. Once Dispose has returned the render is never
/// called again, and disposing twice does nothing.
/// </para>
/// </summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>
    /// Opens the device asking for a format and gives back what it actually got, which can be another rate. Nothing
    /// is asked of the engine until <see cref="Start"/>.
    /// </summary>
    AudioFormat Open(AudioFormat wanted);

    /// <summary>Starts asking for buffers through the render, in the format Open gave back.</summary>
    void Start(AudioRender render);

    /// <summary>Stops asking for buffers and stops playing, keeping the device. What was already handed over may stay with it.</summary>
    void Pause();

    /// <summary>Carries on after <see cref="Pause"/>.</summary>
    void Resume();
}
