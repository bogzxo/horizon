using System;

using Horizon.Logging;
using Horizon.Platform;

namespace Horizon.Audio;

/// <summary>
/// The speakers. The mixer (<see cref="Mixer"/>) and its buses, played out of whatever <see cref="IAudioOutput"/> the
/// platform gives, which asks for floats in stereo at 48000 a second and may get another rate. The engine hands it its
/// host's output, the plain constructor asks the installed platform (see <see cref="PlatformInstallation"/>). No device (none plugged in, a box without sound, <c>HORIZON_AUDIO=off</c>) is not an error,
/// the mixer is there all the same and nothing comes out, so a game never has to ask.
/// </summary>
public sealed class AudioDevice : IDisposable
{
    // Set this to off in the environment and no device is opened, for a scripted run nobody wants to hear
    private const string VARIABLE = "HORIZON_AUDIO";

    private const int RATE = 48000;
    private const int CHANNELS = 2;

    // How many frames are mixed at a time
    private const int CHUNK = 512;

    private readonly IAudioOutput? output;

    // Held while a buffer is mixed, so once Pause or Dispose has it no buffer moves anything on
    private readonly object rendering = new();

    // Keeps pause, resume and dispose in order on the output, without holding up a buffer being mixed
    private readonly object control = new();
    private bool paused;
    private bool disposed;

    /// <summary>What plays, see <see cref="AudioMixer"/>.</summary>
    public AudioMixer Mixer { get; }

    /// <summary>Whether there's a device and anything played is heard.</summary>
    public bool IsOpen => output is not null;

    /// <summary>Whether playing is held where it is, see <see cref="Pause"/>.</summary>
    public bool IsPaused
    {
        get
        {
            lock (rendering) return paused;
        }
    }

    /// <summary>The format everything is played in, the one the device gave back when there is one.</summary>
    public AudioFormat Format { get; }

    /// <param name="open">False for none at all, the mixer and nothing out of it (an application, Hex, has no use for
    /// a thread feeding the speakers).</param>
    public AudioDevice(bool open = true) : this(OutputOf(open, static () => PlatformInstallation.Current?.CreateAudioOutput()))
    {
    }

    /// <summary>
    /// Plays out of an output the platform made. The device owns it from here on and disposes it, also when it fails
    /// to open. Null is no sound at all.
    /// </summary>
    public AudioDevice(IAudioOutput? output)
    {
        var wanted = new AudioFormat(RATE, CHANNELS, CHUNK);
        Format = wanted;

        if (output is not null)
        {
            try
            {
                AudioFormat got = output.Open(wanted);
                if (got.SampleRate <= 0 || got.Channels != CHANNELS || got.FramesPerBuffer <= 0)
                    throw new InvalidOperationException($"it gave back {got.SampleRate} a second in {got.Channels} channels, {got.FramesPerBuffer} frames at a time");

                Format = got;
                Log.Info($"[Audio] Playing at {got.SampleRate} frames a second.");
            }
            catch (Exception exception)
            {
                Log.Warning($"[Audio] No sound, there's no audio device to be had: {exception.Message}");
                output.Dispose();
                output = null;
            }
        }

        // The mixer is made at the rate the device really runs at before the output is started, so nothing is
        // ever asked of a mixer that isn't there yet
        Mixer = new AudioMixer(Format.SampleRate);
        if (output is null) return;

        try
        {
            output.Start(Render);
            this.output = output;
        }
        catch (Exception exception)
        {
            Log.Warning($"[Audio] No sound, the audio device wouldn't start: {exception.Message}");
            output.Dispose();
        }
    }

    /// <summary>
    /// An audio device out of what a platform host makes, unless it isn't to be opened or HORIZON_AUDIO says off, in
    /// which case nothing is asked of the host at all.
    /// </summary>
    internal static AudioDevice For(bool open, IPlatformHost host) => new(OutputOf(open, host.CreateAudioOutput));

    // What the platform makes unless audio is off, and nothing for an application
    private static IAudioOutput? OutputOf(bool open, Func<IAudioOutput?> make)
    {
        if (!open) return null;
        if (SaysOff(Environment.GetEnvironmentVariable(VARIABLE)))
        {
            Log.Info("[Audio] Off, HORIZON_AUDIO says so.");
            return null;
        }

        IAudioOutput? output = make();
        if (output is null)
            Log.Info("[Audio] No sound, the platform has no speakers to give.");
        return output;
    }

    /// <summary>Whether a value of HORIZON_AUDIO switches the sound off.</summary>
    internal static bool SaysOff(string? value) => string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);

    // Called on the output's thread for every buffer. Paused, disposed or with nothing playing it's silence and the
    // voices stay exactly where they are
    private bool Render(Span<float> buffer)
    {
        lock (rendering)
        {
            if (paused || disposed || Mixer.Playing == 0)
            {
                buffer.Clear();
                return false;
            }

            Mixer.Mix(buffer);
            return true;
        }
    }

    /// <summary>
    /// Holds everything where it is, for a game going into the background. Every voice and stream keeps its place and
    /// nothing is heard until <see cref="Resume"/>. Once this returns no buffer moves anything on.
    /// </summary>
    public void Pause()
    {
        lock (control)
        {
            lock (rendering)
            {
                if (paused || disposed) return;
                paused = true;
            }

            output?.Pause();
        }
    }

    /// <summary>Carries on playing from where <see cref="Pause"/> held it.</summary>
    public void Resume()
    {
        lock (control)
        {
            lock (rendering)
            {
                if (!paused || disposed) return;
                paused = false;
            }

            output?.Resume();
        }
    }

    public void Dispose()
    {
        lock (control)
        {
            lock (rendering)
            {
                if (disposed) return;
                disposed = true;
            }

            // Disposing the output waits its thread out, and a buffer it still asks for meanwhile is silence
            output?.Dispose();
            Mixer.StopAll();
        }
    }
}
