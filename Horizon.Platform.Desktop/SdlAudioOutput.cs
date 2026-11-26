using System.Threading;

using Horizon.Logging;

using Silk.NET.SDL;

using Thread = System.Threading.Thread;
using ThreadPriority = System.Threading.ThreadPriority;

namespace Horizon.Platform.Desktop;

/// <summary>
/// SDL's audio (the engine has SDL already, for the window on a Mac and the gamepads) behind the output contract. SDL
/// is opened for floats, and a thread of its own keeps a few tens of milliseconds queued up on it out of the render.
/// It leans on nothing of the engine's but the public output contract. The desktop host makes it, and whoever it is
/// handed to (the engine's audio device) owns it from then on.
/// </summary>
internal sealed unsafe class SdlAudioOutput : IAudioOutput
{
    private const uint SDL_INIT_AUDIO = 0x10;
    private const ushort AUDIO_F32 = 0x8120;

    // How many frames are kept queued on the device ahead of what it's playing, a little over 40 ms at 48000, which
    // is the delay between a hit and its sound
    private const int AHEAD = 2048;

    // Bounded waits when idle: paused wakes immediately on Resume/Dispose; silence backs off to ~16 ms (~60 Hz)
    // instead of 500 Hz busy polling while still starting newly played audio within one frame.
    internal const int PAUSED_WAIT_MS = 500;
    internal const int SILENT_INITIAL_WAIT_MS = 4;
    internal const int SILENT_MAX_WAIT_MS = 16;

    private readonly AutoResetEvent wakeEvent = new(false);
    private Sdl? sdl;
    private uint device;
    private AudioFormat format;
    private AudioRender? render;
    private Thread? thread;
    private volatile bool running;
    private volatile bool paused;
    private int disposed;

    public AudioFormat Open(AudioFormat wanted)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (device != 0) throw new InvalidOperationException("The SDL audio device is open already.");

        sdl = SdlProvider.SDL.Value;
        if (sdl.InitSubSystem(SDL_INIT_AUDIO) != 0) throw new InvalidOperationException(sdl.GetErrorS());

        var asked = new AudioSpec { Freq = wanted.SampleRate, Format = AUDIO_F32, Channels = (byte)wanted.Channels, Samples = (ushort)wanted.FramesPerBuffer };
        AudioSpec got;
        device = sdl.OpenAudioDevice((byte*)null, 0, &asked, &got, 0);
        if (device == 0) throw new InvalidOperationException(sdl.GetErrorS());

        // With no changes allowed SDL converts to whatever the hardware wants itself, so the floats and the channels
        // stay as asked, and the chunk mixed at a time stays the one asked for whatever SDL buffers inside
        format = wanted with { SampleRate = got.Freq };
        return format;
    }

    public void Start(AudioRender render)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (device == 0) throw new InvalidOperationException("The SDL audio device isn't open.");
        if (thread is not null) throw new InvalidOperationException("The SDL audio output has started already.");

        this.render = render;
        running = true;
        if (!paused) sdl!.PauseAudioDevice(device, 0);

        thread = new Thread(Pump) { Name = "Audio", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    public void Pause()
    {
        if (disposed != 0 || paused) return;
        paused = true;
        if (device != 0) sdl!.PauseAudioDevice(device, 1);
    }

    public void Resume()
    {
        if (disposed != 0 || !paused) return;
        paused = false;
        if (device != 0 && thread is not null) sdl!.PauseAudioDevice(device, 0);
        SignalWake();
    }

    /// <summary>Computes the bounded wait duration for the pump thread given its current state.</summary>
    internal static int ComputeWaitMs(bool paused, bool queueFull, bool silent, int silentStreak, AudioFormat format)
    {
        if (paused)
            return PAUSED_WAIT_MS;

        if (queueFull)
        {
            int halfChunkMs = format.SampleRate > 0 ? (format.FramesPerBuffer * 500) / format.SampleRate : 4;
            return Math.Clamp(halfChunkMs, 2, 10);
        }

        if (silent)
        {
            if (silentStreak <= 1) return SILENT_INITIAL_WAIT_MS;
            if (silentStreak <= 3) return 8;
            return SILENT_MAX_WAIT_MS;
        }

        return 0;
    }

    private void SignalWake()
    {
        try
        {
            wakeEvent.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // Keeps the device fed, asking for a chunk whenever what's queued drops under what it should be ahead by. Paused
    // the queue stays where it is and plays on from there after a resume, so nothing already mixed is lost
    private void Pump()
    {
        var chunk = new float[format.FramesPerBuffer * format.Channels];
        uint ahead = (uint)(AHEAD * format.Channels * sizeof(float));
        int silentStreak = 0;

        while (running)
        {
            bool isPaused = paused;
            if (isPaused)
            {
                silentStreak = 0;
                wakeEvent.WaitOne(ComputeWaitMs(paused: true, queueFull: false, silent: false, silentStreak: 0, format));
                continue;
            }

            if (sdl!.GetQueuedAudioSize(device) >= ahead)
            {
                silentStreak = 0;
                wakeEvent.WaitOne(ComputeWaitMs(paused: false, queueFull: true, silent: false, silentStreak: 0, format));
                continue;
            }

            // With nothing playing nothing is queued, SDL plays silence by itself, and the thread backs off cleanly
            if (!render!(chunk))
            {
                if (silentStreak < 16) silentStreak++;
                wakeEvent.WaitOne(ComputeWaitMs(paused: false, queueFull: false, silent: true, silentStreak, format));
                continue;
            }

            silentStreak = 0;
            fixed (float* samples = chunk) sdl!.QueueAudio(device, samples, (uint)(chunk.Length * sizeof(float)));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;

        running = false;
        SignalWake();
        if (thread is not null && !thread.Join(500))
        {
            // A pump that won't stop could still be queueing, so the device is left open rather than closed under it
            Log.Warning("[Audio] The audio thread didn't stop, leaving the device open.");
            return;
        }

        if (device != 0) sdl!.CloseAudioDevice(device);
        device = 0;
        wakeEvent.Dispose();
    }
}
