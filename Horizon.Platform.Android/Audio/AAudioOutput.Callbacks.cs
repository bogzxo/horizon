using System.Runtime.InteropServices;

using Horizon.Platform.Android.Native;

namespace Horizon.Platform.Android;

// What AAudio calls on its own threads. Nothing in here allocates, logs, locks or lets an exception out, a fault is
// counted and the buffer goes out silent. The first call on a new AAudio thread has the runtime attach that thread,
// which is the one time anything is made there
public sealed unsafe partial class AAudioOutput
{
    private long callbacks;
    private long frames;
    private long faults;
    private long disconnects;
    private int lastError;

    /// <summary>How many buffers AAudio has asked for, for a device check.</summary>
    public long Callbacks => Interlocked.Read(ref callbacks);

    /// <summary>How many frames AAudio has asked for altogether.</summary>
    public long FramesRequested => Interlocked.Read(ref frames);

    /// <summary>How many buffers went out silent because the render threw.</summary>
    public long Faults => Interlocked.Read(ref faults);

    /// <summary>How many times AAudio said the stream was lost.</summary>
    public long Disconnects => Interlocked.Read(ref disconnects);

    /// <summary>The last error AAudio's error callback gave, 0 for none yet.</summary>
    public int LastError => Volatile.Read(ref lastError);

    [UnmanagedCallersOnly]
    private static int OnData(nint stream, nint user, nint audio, int count)
    {
        try
        {
            if (user != 0 && GCHandle.FromIntPtr(user).Target is AAudioOutput output) output.Fill(audio, count);
        }
        catch
        {
            // Nothing can be done about it here and nothing may leave
        }
        return NativeAudio.CALLBACK_RESULT_CONTINUE;
    }

    [UnmanagedCallersOnly]
    private static void OnError(nint stream, nint user, int error)
    {
        try
        {
            if (user != 0 && GCHandle.FromIntPtr(user).Target is AAudioOutput output) output.Failed(stream, error);
        }
        catch
        {
            // The worker may be gone already when a dying stream says so, which is fine
        }
    }

    // Fills a buffer of some frames out of the render, or with silence when there's nothing to ask or it says it's quiet
    private void Fill(nint audio, int count)
    {
        Interlocked.Increment(ref callbacks);
        if (count <= 0 || audio == 0) return;
        Interlocked.Add(ref frames, count);

        // The channels are pinned at Open and every stream after has the same, so this is the stream's buffer size
        var buffer = new Span<float>((void*)audio, count * format.Channels);
        AudioRender? asked = render;
        if (quiet || asked is null)
        {
            buffer.Clear();
            return;
        }

        try
        {
            if (!asked(buffer)) buffer.Clear();
        }
        catch
        {
            buffer.Clear();
            Interlocked.Increment(ref faults);
        }
    }

    // Never closes or stops anything here, AAudio says no to that on this thread, it hands the stream to the worker
    private void Failed(nint failed, int error)
    {
        Volatile.Write(ref lastError, error);
        if (error == NativeAudio.ERROR_DISCONNECTED) Interlocked.Increment(ref disconnects);
        Interlocked.Exchange(ref failedStream, failed);
        wake.Set();
    }
}
