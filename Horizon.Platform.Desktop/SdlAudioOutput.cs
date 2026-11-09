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
    }

    // Keeps the device fed, asking for a chunk whenever what's queued drops under what it should be ahead by. Paused
    // the queue stays where it is and plays on from there after a resume, so nothing already mixed is lost
    private void Pump()
    {
        var chunk = new float[format.FramesPerBuffer * format.Channels];
        uint ahead = (uint)(AHEAD * format.Channels * sizeof(float));

        while (running)
        {
            if (paused || sdl!.GetQueuedAudioSize(device) >= ahead)
            {
                Thread.Sleep(2);
                continue;
            }

            // With nothing playing nothing is queued, SDL plays silence by itself, and the thread hardly wakes
            if (!render!(chunk))
            {
                Thread.Sleep(2);
                continue;
            }

            fixed (float* samples = chunk) sdl!.QueueAudio(device, samples, (uint)(chunk.Length * sizeof(float)));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;

        running = false;
        if (thread is not null && !thread.Join(500))
        {
            // A pump that won't stop could still be queueing, so the device is left open rather than closed under it
            Log.Warning("[Audio] The audio thread didn't stop, leaving the device open.");
            return;
        }

        if (device != 0) sdl!.CloseAudioDevice(device);
        device = 0;
    }
}
