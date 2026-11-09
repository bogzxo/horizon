using System.Runtime.InteropServices;

using Horizon.Logging;
using Horizon.Platform.Android.Native;

namespace Horizon.Platform.Android;

/// <summary>
/// The speakers of a phone, an AAudio output stream in shared mode asking for low latency, floats in stereo and
/// whatever rate the device runs at natively, which is what gets the fast path (see the low latency advice on
/// developer.android.com/ndk/guides/audio/aaudio/aaudio). AAudio calls back on a thread of its own for however many
/// frames it wants at a time and the render fills them.
/// <para>
/// Open opens the stream and gives back the rate it got, Start starts it once with the render, Pause and Resume hold
/// it and carry on. Headphones out or a Bluetooth speaker going reaches the error callback as a disconnect, and as
/// that callback mustn't stop or close anything a worker thread of this output closes the dead stream and opens a new
/// one, pinned to the rate the mixer already runs at. A new stream that can't have that rate, those channels or floats
/// isn't used, the output goes quiet and says why in the log rather than playing the mixer at the wrong speed.
/// </para>
/// <para>
/// Dispose stops and closes the stream before it lets go of the handle the callbacks find this by, so once it returns
/// nothing calls the render, and twice does nothing. Nothing here takes a lock the callback takes, and the callback
/// takes none of this class's, so AudioDevice's render lock and control lock can't deadlock against it.
/// </para>
/// </summary>
public sealed unsafe partial class AAudioOutput : IAudioOutput
{
    // How long a pause, a stop or a close waits for AAudio to get there
    private const long WAIT_NANOSECONDS = 200_000_000;

    private readonly Lock gate = new();
    private GCHandle self;
    private nint stream;
    private AudioFormat format;
    private volatile AudioRender? render;
    private bool opened;
    private bool started;
    private bool paused;
    private bool disposed;

    // Read by the callback without the gate, true and it hands over silence without asking the render
    private volatile bool quiet;

    /// <summary>The format Open gave back, nothing before it.</summary>
    public AudioFormat Format
    {
        get
        {
            lock (gate) return format;
        }
    }

    /// <summary>Whether there's a stream right now, false between a disconnect and the stream opened after it.</summary>
    public bool HasStream
    {
        get
        {
            lock (gate) return stream != 0;
        }
    }

    public AudioFormat Open(AudioFormat wanted)
    {
        if (wanted.Channels <= 0) throw new ArgumentOutOfRangeException(nameof(wanted), "An output needs a channel at least.");

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (opened) throw new InvalidOperationException("The AAudio stream is open already.");

            self = GCHandle.Alloc(this);
            int result = OpenStream(NativeAudio.UNSPECIFIED, wanted.Channels, out nint made);
            if (result != NativeAudio.OK)
            {
                self.Free();
                throw new InvalidOperationException($"AAudio wouldn't open an output stream, {NativeAudio.Describe(result)}.");
            }

            int rate = NativeAudio.SampleRate(made), channels = NativeAudio.ChannelCount(made), sampleFormat = NativeAudio.Format(made);
            int burst = NativeAudio.FramesPerBurst(made);
            if (rate <= 0 || channels != wanted.Channels || sampleFormat != NativeAudio.FORMAT_PCM_FLOAT)
            {
                CloseStream(made);
                self.Free();
                throw new InvalidOperationException($"AAudio opened {rate} a second in {channels} channels as format {sampleFormat}, not floats in {wanted.Channels}.");
            }

            Settle(made, burst);
            stream = made;
            format = new AudioFormat(rate, channels, burst > 0 ? burst : wanted.FramesPerBuffer);
            opened = true;
            StartWorker();

            Log.Info($"[Audio] AAudio at {rate} a second, {burst} frames a burst, sharing {NativeAudio.SharingMode(made)}, performance {NativeAudio.PerformanceMode(made)}.");
            return format;
        }
    }

    public void Start(AudioRender render)
    {
        ArgumentNullException.ThrowIfNull(render);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!opened) throw new InvalidOperationException("The AAudio stream isn't open.");
            if (started) throw new InvalidOperationException("The AAudio output has started already.");

            this.render = render;
            started = true;
            quiet = paused;

            // A disconnect between Open and Start left no stream, the worker starts the one it opens
            if (paused || stream == 0) return;
            int result = NativeAudio.RequestStart(stream);
            if (result != NativeAudio.OK)
            {
                started = false;
                quiet = true;
                throw new InvalidOperationException($"AAudio wouldn't start the stream, {NativeAudio.Describe(result)}.");
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
            if (!started || stream == 0) return;

            int result = NativeAudio.RequestPause(stream);
            if (result == NativeAudio.OK) WaitWhile(stream, NativeAudio.STATE_PAUSING);
            else if (result != NativeAudio.ERROR_DISCONNECTED) Log.Warning($"[Audio] AAudio wouldn't pause, {NativeAudio.Describe(result)}.");
        }
    }

    public void Resume()
    {
        lock (gate)
        {
            if (disposed || !paused) return;
            paused = false;
            quiet = false;
            if (!started || stream == 0) return;

            int result = NativeAudio.RequestStart(stream);
            if (result != NativeAudio.OK && result != NativeAudio.ERROR_DISCONNECTED) Log.Warning($"[Audio] AAudio wouldn't resume, {NativeAudio.Describe(result)}.");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            quiet = true;
        }

        // Out of the gate, the worker may be in it closing or opening a stream, and it gives up once it sees disposed
        bool workerStopped = StopWorker();

        lock (gate)
        {
            if (stream != 0) CloseStream(stream);
            stream = 0;
            render = null;

            // Closed means no callback runs or will, so the handle they find this by can go now and not before
            if (self.IsAllocated) self.Free();
        }

        if (workerStopped) wake.Dispose();
        else Log.Warning("[Audio] The AAudio worker didn't stop in time, it gives up on its own once it wakes.");
    }
}
