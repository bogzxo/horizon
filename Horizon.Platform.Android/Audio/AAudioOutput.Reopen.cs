using Horizon.Logging;
using Horizon.Platform.Android.Native;

namespace Horizon.Platform.Android;

// The worker that takes over a stream the error callback says is gone, closes it and opens another at the same format
public sealed unsafe partial class AAudioOutput
{
    // How many times a new stream is tried after a loss, and how long apart
    private const int REOPEN_TRIES = 10;
    private const int REOPEN_WAIT_MILLISECONDS = 300;

    private readonly AutoResetEvent wake = new(false);
    private Thread? worker;
    private volatile bool stopping;
    private nint failedStream;

    /// <summary>How many streams were opened again after a loss.</summary>
    public long Reopens => Interlocked.Read(ref reopens);
    private long reopens;

    /// <summary>Why the last try at a new stream failed, null when the last one worked.</summary>
    public string? ReopenProblem { get; private set; }

    private void StartWorker()
    {
        worker = new Thread(Work) { Name = "AAudio worker", IsBackground = true };
        worker.Start();
    }

    // True once the worker is gone, or there never was one
    private bool StopWorker()
    {
        stopping = true;
        if (worker is null) return true;
        wake.Set();
        return worker.Join(2000);
    }

    private void Work()
    {
        while (true)
        {
            wake.WaitOne();
            if (stopping) return;

            nint failed = Interlocked.Exchange(ref failedStream, 0);
            if (failed == 0 || !CloseFailed(failed)) continue;

            for (int attempt = 0; attempt < REOPEN_TRIES; attempt++)
            {
                if (stopping || TryReopen()) break;

                // Waits between tries, and a dispose cuts the wait short
                wake.WaitOne(REOPEN_WAIT_MILLISECONDS);
            }
        }
    }

    // Closes the stream that failed if it's still this output's, false when it's an old one already dealt with
    private bool CloseFailed(nint failed)
    {
        lock (gate)
        {
            if (disposed || stream != failed) return false;
            CloseStream(stream);
            stream = 0;
            return true;
        }
    }

    // A new stream pinned to the rate and channels the mixer has. True when it's there or there's no point going on
    private bool TryReopen()
    {
        lock (gate)
        {
            if (disposed) return true;
            if (stream != 0) return true;

            int result = OpenStream(format.SampleRate, format.Channels, out nint made);
            if (result != NativeAudio.OK)
            {
                ReopenProblem = $"AAudio wouldn't open a stream at {format.SampleRate} a second again, {NativeAudio.Describe(result)}.";
                Log.Warning("[Audio] " + ReopenProblem);
                return false;
            }

            int rate = NativeAudio.SampleRate(made), channels = NativeAudio.ChannelCount(made), sampleFormat = NativeAudio.Format(made);
            if (rate != format.SampleRate || channels != format.Channels || sampleFormat != NativeAudio.FORMAT_PCM_FLOAT)
            {
                // Playing the mixer's buffers at another rate or channel count would be wrong, so it stays quiet
                CloseStream(made);
                ReopenProblem = $"The new AAudio stream is {rate} a second in {channels} channels as format {sampleFormat}, the mixer runs {format.SampleRate} in {format.Channels}.";
                Log.Error("[Audio] " + ReopenProblem);
                return false;
            }

            Settle(made, NativeAudio.FramesPerBurst(made));
            stream = made;
            Interlocked.Increment(ref reopens);
            ReopenProblem = null;

            if (started && !paused)
            {
                int start = NativeAudio.RequestStart(made);
                if (start != NativeAudio.OK) Log.Warning($"[Audio] AAudio wouldn't start the new stream, {NativeAudio.Describe(start)}.");
            }
            Log.Info($"[Audio] AAudio stream opened again at {rate} a second after it was lost.");
            return true;
        }
    }
}
