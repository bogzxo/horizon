using System.Runtime.InteropServices;

using Horizon.Platform.Android.Native;

namespace Horizon.Platform.Android;

// Opening and closing native streams. Called with the gate held, never on a callback thread
public sealed unsafe partial class AAudioOutput
{
    // Opens a stream at a rate (UNSPECIFIED for the device's own) with this output's callbacks. The builder goes
    // whatever happens, and a failed open leaves no stream behind
    private int OpenStream(int rate, int channels, out nint opened)
    {
        opened = 0;
        nint builder = 0;
        int result = NativeAudio.CreateBuilder(&builder);
        if (result != NativeAudio.OK) return result;
        if (builder == 0) return NativeAudio.ERROR_INVALID_STATE;

        try
        {
            NativeAudio.SetDirection(builder, NativeAudio.DIRECTION_OUTPUT);
            NativeAudio.SetSharingMode(builder, NativeAudio.SHARING_MODE_SHARED);
            NativeAudio.SetPerformanceMode(builder, NativeAudio.PERFORMANCE_MODE_LOW_LATENCY);
            NativeAudio.SetFormat(builder, NativeAudio.FORMAT_PCM_FLOAT);
            NativeAudio.SetChannelCount(builder, channels);
            NativeAudio.SetSampleRate(builder, rate);

            nint user = GCHandle.ToIntPtr(self);
            NativeAudio.SetDataCallback(builder, (nint)(delegate* unmanaged<nint, nint, nint, int, int>)&OnData, user);
            NativeAudio.SetErrorCallback(builder, (nint)(delegate* unmanaged<nint, nint, int, void>)&OnError, user);

            nint made = 0;
            result = NativeAudio.OpenStream(builder, &made);
            if (result == NativeAudio.OK && made == 0) result = NativeAudio.ERROR_INVALID_STATE;
            if (result == NativeAudio.OK) opened = made;
            return result;
        }
        finally
        {
            NativeAudio.DeleteBuilder(builder);
        }
    }

    // Two bursts of buffer is the usual low latency start, AAudio rounds it to what it can and saying no is fine
    private static void Settle(nint stream, int burst)
    {
        if (burst <= 0) return;
        int capacity = NativeAudio.BufferCapacity(stream);
        NativeAudio.SetBufferSize(stream, capacity > 0 ? Math.Min(burst * 2, capacity) : burst * 2);
    }

    // Stops a stream, waits for it to get there and closes it. Waiting for stopped first is what keeps an older AAudio
    // from closing under a callback still running, after close no callback comes
    private static void CloseStream(nint stream)
    {
        int state = NativeAudio.State(stream);
        if (state is NativeAudio.STATE_STARTING or NativeAudio.STATE_STARTED or NativeAudio.STATE_PAUSING or NativeAudio.STATE_PAUSED
            or NativeAudio.STATE_FLUSHING or NativeAudio.STATE_FLUSHED)
        {
            if (NativeAudio.RequestStop(stream) == NativeAudio.OK) WaitWhile(stream, NativeAudio.STATE_STOPPING, NativeAudio.STATE_STARTING, NativeAudio.STATE_STARTED,
                NativeAudio.STATE_PAUSING, NativeAudio.STATE_PAUSED, NativeAudio.STATE_FLUSHING, NativeAudio.STATE_FLUSHED);
        }

        NativeAudio.Close(stream);
    }

    // Waits as long as the stream is in one of some states, a few rounds of the wait at most
    private static void WaitWhile(nint stream, params ReadOnlySpan<int> states)
    {
        int state = NativeAudio.State(stream);
        for (int round = 0; round < 5 && states.Contains(state); round++)
        {
            int next = state;
            int result = NativeAudio.WaitForStateChange(stream, state, &next, WAIT_NANOSECONDS);
            if (result != NativeAudio.OK) return;
            state = next;
        }
    }
}
