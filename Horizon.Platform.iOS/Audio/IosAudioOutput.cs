using AudioToolbox;

using AVFoundation;

using Foundation;

using Horizon.Logging;

namespace Horizon.Platform.iOS;

/// <summary>
/// The speakers of an iPhone, an AVAudioEngine with one source node that asks the engine's mixer for frames on Core
/// Audio's own thread. The session is the ambient kind, so the game mixes with whatever music is playing and goes
/// quiet with the silent switch, which is what a game that hasn't said otherwise should do.
/// <para>
/// The node runs in the session's own rate, whatever the hardware is at (48000 on every recent phone), and in the
/// standard format, a buffer of floats for every channel. The engine's render fills interleaved floats, so a scratch
/// buffer takes them and they're dealt out to the channels. Nothing in the callback allocates or takes a lock of this
/// class's.
/// </para>
/// </summary>
public sealed unsafe class IosAudioOutput : IAudioOutput
{
    // Frames Core Audio asks for at most in one go, the scratch is made for this many and grows only if it's beaten
    private const int MOST_FRAMES = 4096;

    private readonly Lock gate = new();
    private AVAudioEngine? engine;
    private AVAudioSourceNode? node;
    private AudioFormat format;
    private volatile AudioRender? render;
    private float[] scratch = [];
    private bool opened, started, paused, disposed;

    // Read by the callback without the gate, true and it hands over silence without asking the render
    private volatile bool quiet = true;

    /// <summary>The format Open gave back, nothing before it.</summary>
    public AudioFormat Format
    {
        get
        {
            lock (gate) return format;
        }
    }

    public AudioFormat Open(AudioFormat wanted)
    {
        if (wanted.Channels <= 0) throw new ArgumentOutOfRangeException(nameof(wanted), "An output needs a channel at least.");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (opened) throw new InvalidOperationException("The audio engine is open already.");

            AVAudioSession session = AVAudioSession.SharedInstance();
            NSError? error = session.SetCategory(AVAudioSessionCategory.Ambient);
            if (error is not null) Log.Warning($"[Audio] The audio session wouldn't be ambient, {error.LocalizedDescription}");
            session.SetPreferredIOBufferDuration(wanted.FramesPerBuffer / (double)Math.Max(1, wanted.SampleRate), out _);
            if (!session.SetActive(true, out error))
                throw new InvalidOperationException($"The audio session wouldn't start, {error?.LocalizedDescription}.");

            int rate = (int)Math.Round(session.SampleRate);
            if (rate <= 0) rate = wanted.SampleRate;
            int buffer = (int)Math.Round(session.IOBufferDuration * rate);

            var made = new AVAudioEngine();
            var nodeFormat = new AVAudioFormat(rate, (uint)wanted.Channels);
            var source = new AVAudioSourceNode(nodeFormat, Fill);
            made.AttachNode(source);

            // iOS 27 wants the Connect that says what went wrong, which 16 doesn't have
#pragma warning disable CA1422
            made.Connect(source, made.MainMixerNode, nodeFormat);
#pragma warning restore CA1422
            made.Prepare();

            engine = made;
            node = source;
            scratch = new float[MOST_FRAMES * wanted.Channels];
            format = new AudioFormat(rate, wanted.Channels, buffer > 0 ? buffer : wanted.FramesPerBuffer);
            opened = true;

            Log.Info($"[Audio] AVAudioEngine at {rate} a second, {format.FramesPerBuffer} frames a buffer, {session.OutputLatency * 1000:0.0} ms out to {session.CurrentRoute.Outputs.FirstOrDefault()?.PortName ?? "nowhere"}.");
            return format;
        }
    }

    public void Start(AudioRender render)
    {
        ArgumentNullException.ThrowIfNull(render);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!opened || engine is null) throw new InvalidOperationException("The audio engine isn't open.");
            if (started) throw new InvalidOperationException("The audio output has started already.");

            this.render = render;
            started = true;
            if (paused) return;

            quiet = false;
            if (!engine.StartAndReturnError(out NSError? error))
            {
                started = false;
                quiet = true;
                throw new InvalidOperationException($"The audio engine wouldn't start, {error?.LocalizedDescription}.");
            }
        }
    }

    public void Pause()
    {
        lock (gate)
        {
            if (disposed || paused) return;
            paused = true;
            quiet = true;
            if (started) engine?.Pause();
        }
    }

    public void Resume()
    {
        lock (gate)
        {
            if (disposed || !paused) return;
            paused = false;
            if (!started || engine is null) return;

            // A call or Siri may have taken the session, it's asked for again first
            AVAudioSession.SharedInstance().SetActive(true, out _);
            if (engine.StartAndReturnError(out NSError? error)) quiet = false;
            else Log.Warning($"[Audio] The audio engine wouldn't carry on, {error?.LocalizedDescription}");
        }
    }

    // Core Audio's thread. Interleaved frames from the engine into the scratch, then dealt out to each channel's buffer
    private int Fill(ref bool isSilence, ref AudioTimeStamp timestamp, uint frameCount, AudioBuffers outputData)
    {
        int channels = outputData.Count;
        int frames = (int)frameCount;
        AudioRender? filling = render;

        bool heard = false;
        Span<float> interleaved = default;
        if (!quiet && filling is not null && channels > 0)
        {
            if (scratch.Length < frames * channels) scratch = new float[frames * channels];
            interleaved = scratch.AsSpan(0, frames * channels);
            heard = filling(interleaved);
        }

        for (int channel = 0; channel < channels; channel++)
        {
            AudioBuffer buffer = outputData[channel];
            var samples = new Span<float>((void*)buffer.Data, Math.Min(frames, buffer.DataByteSize / sizeof(float)));
            if (!heard)
            {
                samples.Clear();
                continue;
            }

            for (int frame = 0; frame < samples.Length; frame++) samples[frame] = interleaved[frame * channels + channel];
        }

        isSilence = !heard;
        return 0;
    }

    /// <summary>Stops the engine before anything is let go of, so once this returns nothing calls the render. Twice does nothing.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            quiet = true;
            render = null;

            engine?.Stop();
            if (node is not null) engine?.DetachNode(node);
            node?.Dispose();
            engine?.Dispose();
            node = null;
            engine = null;
        }
    }
}
