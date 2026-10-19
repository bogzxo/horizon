using System;
using System.Threading;

using Horizon.Logging;

using Silk.NET.SDL;

using Thread = System.Threading.Thread;
using ThreadPriority = System.Threading.ThreadPriority;

namespace Horizon.Audio;

/// <summary>
/// The speakers. SDL's audio (the engine has SDL already, for the window on a Mac and the gamepads), opened for floats
/// in stereo at 48000 a second, and a thread of its own that keeps a few tens of milliseconds queued up on it out of
/// <see cref="Mixer"/>. No device (none plugged in, a box without sound, <c>HORIZON_AUDIO=off</c>) is not an error, the
/// mixer is there all the same and nothing comes out, so a game never has to ask.
/// </summary>
public sealed unsafe class AudioDevice : IDisposable
{
    // Set this to off in the environment and no device is opened, for a scripted run nobody wants to hear
    private const string VARIABLE = "HORIZON_AUDIO";

    private const uint SDL_INIT_AUDIO = 0x10;
    private const ushort AUDIO_F32 = 0x8120;
    private const int RATE = 48000;

    // How many frames are mixed at a time, and how many are kept queued on the device ahead of what it's playing, a
    // little over 40 ms, which is the delay between a hit and its sound
    private const int CHUNK = 512;
    private const int AHEAD = 2048;

    private readonly Sdl? sdl;
    private readonly uint device;
    private readonly Thread? thread;
    private volatile bool running;

    /// <summary>What plays, see <see cref="AudioMixer"/>.</summary>
    public AudioMixer Mixer { get; }

    /// <summary>Whether there's a device and anything played is heard.</summary>
    public bool IsOpen => device != 0;

    /// <param name="open">False for none at all, the mixer and nothing out of it (an application, Hex, has no use for
    /// a thread feeding the speakers).</param>
    public AudioDevice(bool open = true)
    {
        if (!open || string.Equals(Environment.GetEnvironmentVariable(VARIABLE), "off", StringComparison.OrdinalIgnoreCase))
        {
            Mixer = new AudioMixer(RATE);
            if (open) Log.Info("[Audio] Off, HORIZON_AUDIO says so.");
            return;
        }

        int rate = RATE;
        try
        {
            sdl = SdlProvider.SDL.Value;
            if (sdl.InitSubSystem(SDL_INIT_AUDIO) != 0) throw new InvalidOperationException(sdl.GetErrorS());

            var wanted = new AudioSpec { Freq = RATE, Format = AUDIO_F32, Channels = 2, Samples = CHUNK };
            AudioSpec got;
            device = sdl.OpenAudioDevice((byte*)null, 0, &wanted, &got, 0);
            if (device == 0) throw new InvalidOperationException(sdl.GetErrorS());

            rate = got.Freq;
            sdl.PauseAudioDevice(device, 0);
            Log.Info($"[Audio] Playing at {rate} frames a second.");
        }
        catch (Exception exception)
        {
            device = 0;
            Log.Warning($"[Audio] No sound, there's no audio device to be had: {exception.Message}");
        }

        Mixer = new AudioMixer(rate);
        if (device == 0) return;

        running = true;
        thread = new Thread(Pump) { Name = "Audio", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    // Keeps the device fed, mixing a chunk whenever what's queued drops under what it should be ahead by
    private void Pump()
    {
        var chunk = new float[CHUNK * 2];
        uint ahead = AHEAD * 2 * sizeof(float);

        while (running)
        {
            // With nothing playing nothing is queued, SDL plays silence by itself, and the thread hardly wakes
            if (sdl!.GetQueuedAudioSize(device) >= ahead || Mixer.Playing == 0)
            {
                Thread.Sleep(2);
                continue;
            }

            Mixer.Mix(chunk);
            fixed (float* samples = chunk) sdl.QueueAudio(device, samples, (uint)(chunk.Length * sizeof(float)));
        }
    }

    public void Dispose()
    {
        running = false;
        thread?.Join(500);
        Mixer.StopAll();

        if (device != 0) sdl!.CloseAudioDevice(device);
    }
}
