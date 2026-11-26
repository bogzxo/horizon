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

    private volatile IAudioOutput? output;

    // Held while a buffer is mixed, so once Pause or Dispose has it no buffer moves anything on
    private readonly object rendering = new();

    // Keeps pause, resume and dispose in order on the output, without holding up a buffer being mixed
    private readonly object control = new();
    private AudioFormat format;
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
    public AudioFormat Format
    {
        get
        {
            lock (control) return format;
        }
    }

    /// <param name="open">False for none at all, the mixer and nothing out of it (an application, Hex, has no use for
    /// a thread feeding the speakers).</param>
    public AudioDevice(bool open = true) : this(OutputOf(open, static () => PlatformInstallation.Current?.CreateAudioOutput()), openAsync: false)
    {
    }

    /// <summary>
    /// Plays out of an output the platform made. The device owns it from here on and disposes it, also when it fails
    /// to open. Null is no sound at all.
    /// </summary>
    public AudioDevice(IAudioOutput? output) : this(output, openAsync: false)
    {
    }

    internal AudioDevice(IAudioOutput? output, bool openAsync)
    {
        var wanted = new AudioFormat(RATE, CHANNELS, CHUNK);
        format = wanted;

        // The mixer is made immediately at 48 kHz so callers can queue audio without waiting on OS audio device open
        Mixer = new AudioMixer(RATE);
        if (output is null) return;

        if (openAsync)
        {
            var thread = new System.Threading.Thread(OpenWorker) { Name = "Audio Open", IsBackground = true };
            thread.Start(new OpenState(this, output, wanted));
        }
        else
        {
            OpenAndStart(output, wanted);
        }
    }

    private sealed class OpenState(AudioDevice device, IAudioOutput candidate, AudioFormat wanted)
    {
        public readonly AudioDevice Device = device;
        public readonly IAudioOutput Candidate = candidate;
        public readonly AudioFormat Wanted = wanted;
    }

    private static void OpenWorker(object? state)
    {
        var open = (OpenState)state!;
        open.Device.OpenAndStart(open.Candidate, open.Wanted);
    }

    private void OpenAndStart(IAudioOutput candidate, AudioFormat wanted)
    {
        lock (control)
        {
            if (disposed)
            {
                candidate.Dispose();
                return;
            }
        }

        AudioFormat got;
        try
        {
            got = candidate.Open(wanted);
            if (got.SampleRate <= 0 || got.Channels != CHANNELS || got.FramesPerBuffer <= 0)
                throw new InvalidOperationException($"it gave back {got.SampleRate} a second in {got.Channels} channels, {got.FramesPerBuffer} frames at a time");
        }
        catch (Exception exception)
        {
            Log.Warning($"[Audio] No sound, there's no audio device to be had: {exception.Message}");
            candidate.Dispose();
            return;
        }

        try
        {
            lock (control)
            {
                if (disposed)
                {
                    candidate.Dispose();
                    return;
                }

                format = got;
                if (got.SampleRate != RATE)
                    Mixer.SetSampleRate(got.SampleRate);

                candidate.Start(Render);
                output = candidate;

                bool isPaused;
                lock (rendering) isPaused = paused;
                if (isPaused)
                    candidate.Pause();
            }

            Log.Info($"[Audio] Playing at {got.SampleRate} frames a second.");
        }
        catch (Exception exception)
        {
            Log.Warning($"[Audio] No sound, the audio device wouldn't start: {exception.Message}");
            candidate.Dispose();
        }
    }

    /// <summary>
    /// An audio device out of what a platform host makes, unless it isn't to be opened or HORIZON_AUDIO says off, in
    /// which case nothing is asked of the host at all.
    /// </summary>
    internal static AudioDevice For(bool open, IPlatformHost host, bool openAsync = false) => new(OutputOf(open, host.CreateAudioOutput), openAsync);

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
            if (paused || disposed)
            {
                buffer.Clear();
                return false;
            }

            return Mixer.Mix(buffer);
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
            output = null;
            Mixer.StopAll();
        }
    }
}
