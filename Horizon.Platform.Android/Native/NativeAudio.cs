using System.Runtime.InteropServices;

namespace Horizon.Platform.Android.Native;

/// <summary>
/// libaaudio.so, everything here is there from API 26. The numbers are aaudio/AAudio.h's, results are 0 for fine and
/// AAUDIO_ERROR_BASE (-900) plus something for an error. Stream and builder are opaque pointers.
/// See developer.android.com/ndk/reference/group/audio.
/// </summary>
internal static unsafe class NativeAudio
{
    private const string LIBRARY = "libaaudio.so";

    public const int UNSPECIFIED = 0;
    public const int OK = 0;

    public const int ERROR_DISCONNECTED = -899;
    public const int ERROR_ILLEGAL_ARGUMENT = -898;
    public const int ERROR_INVALID_STATE = -895;
    public const int ERROR_TIMEOUT = -885;

    public const int DIRECTION_OUTPUT = 0;
    public const int FORMAT_PCM_FLOAT = 2;
    public const int SHARING_MODE_SHARED = 1;
    public const int PERFORMANCE_MODE_LOW_LATENCY = 12;
    public const int CALLBACK_RESULT_CONTINUE = 0;

    public const int STATE_UNINITIALIZED = 0;
    public const int STATE_UNKNOWN = 1;
    public const int STATE_OPEN = 2;
    public const int STATE_STARTING = 3;
    public const int STATE_STARTED = 4;
    public const int STATE_PAUSING = 5;
    public const int STATE_PAUSED = 6;
    public const int STATE_FLUSHING = 7;
    public const int STATE_FLUSHED = 8;
    public const int STATE_STOPPING = 9;
    public const int STATE_STOPPED = 10;
    public const int STATE_CLOSING = 11;
    public const int STATE_CLOSED = 12;
    public const int STATE_DISCONNECTED = 13;

    [DllImport(LIBRARY, EntryPoint = "AAudio_createStreamBuilder")]
    public static extern int CreateBuilder(nint* builder);

    [DllImport(LIBRARY, EntryPoint = "AAudioStreamBuilder_setDirection")]
    public static extern void SetDirection(nint builder, int direction);

    [DllImport(LIBRARY, EntryPoint = "AAudioStreamBuilder_setSampleRate")]
    public static extern void SetSampleRate(nint builder, int rate);

    [DllImport(LIBRARY, EntryPoint = "AAudioStreamBuilder_setChannelCount")]
    public static extern void SetChannelCount(nint builder, int channels);

    [DllImport(LIBRARY, EntryPoint = "AAudioStreamBuilder_setFormat")]
    public static extern void SetFormat(nint builder, int format);

    [DllImport(LIBRARY, EntryPoint = "AAudioStreamBuilder_setSharingMode")]
    public static extern void SetSharingMode(nint builder, int mode);

    [DllImport(LIBRARY, EntryPoint = "AAudioStreamBuilder_setPerformanceMode")]
    public static extern void SetPerformanceMode(nint builder, int mode);

    /// <summary>Takes an aaudio_data_callback_result_t (*)(AAudioStream*, void*, void*, int32_t).</summary>
    [DllImport(LIBRARY, EntryPoint = "AAudioStreamBuilder_setDataCallback")]
    public static extern void SetDataCallback(nint builder, nint callback, nint user);

    /// <summary>Takes a void (*)(AAudioStream*, void*, aaudio_result_t).</summary>
    [DllImport(LIBRARY, EntryPoint = "AAudioStreamBuilder_setErrorCallback")]
    public static extern void SetErrorCallback(nint builder, nint callback, nint user);

    [DllImport(LIBRARY, EntryPoint = "AAudioStreamBuilder_openStream")]
    public static extern int OpenStream(nint builder, nint* stream);

    [DllImport(LIBRARY, EntryPoint = "AAudioStreamBuilder_delete")]
    public static extern int DeleteBuilder(nint builder);

    [DllImport(LIBRARY, EntryPoint = "AAudioStream_requestStart")]
    public static extern int RequestStart(nint stream);

    [DllImport(LIBRARY, EntryPoint = "AAudioStream_requestPause")]
    public static extern int RequestPause(nint stream);

    [DllImport(LIBRARY, EntryPoint = "AAudioStream_requestStop")]
    public static extern int RequestStop(nint stream);

    [DllImport(LIBRARY, EntryPoint = "AAudioStream_waitForStateChange")]
    public static extern int WaitForStateChange(nint stream, int from, int* next, long timeoutNanoseconds);

    [DllImport(LIBRARY, EntryPoint = "AAudioStream_close")]
    public static extern int Close(nint stream);

    [DllImport(LIBRARY, EntryPoint = "AAudioStream_getState")]
    public static extern int State(nint stream);

    [DllImport(LIBRARY, EntryPoint = "AAudioStream_getSampleRate")]
    public static extern int SampleRate(nint stream);

    [DllImport(LIBRARY, EntryPoint = "AAudioStream_getChannelCount")]
    public static extern int ChannelCount(nint stream);

    [DllImport(LIBRARY, EntryPoint = "AAudioStream_getFormat")]
    public static extern int Format(nint stream);

    [DllImport(LIBRARY, EntryPoint = "AAudioStream_getFramesPerBurst")]
    public static extern int FramesPerBurst(nint stream);

    [DllImport(LIBRARY, EntryPoint = "AAudioStream_getBufferCapacityInFrames")]
    public static extern int BufferCapacity(nint stream);

    [DllImport(LIBRARY, EntryPoint = "AAudioStream_setBufferSizeInFrames")]
    public static extern int SetBufferSize(nint stream, int frames);

    [DllImport(LIBRARY, EntryPoint = "AAudioStream_getSharingMode")]
    public static extern int SharingMode(nint stream);

    [DllImport(LIBRARY, EntryPoint = "AAudioStream_getPerformanceMode")]
    public static extern int PerformanceMode(nint stream);

    [DllImport(LIBRARY, EntryPoint = "AAudio_convertResultToText")]
    private static extern byte* ResultText(int result);

    /// <summary>What AAudio calls a result, with the number, for an exception or the log. Never in the callback.</summary>
    public static string Describe(int result)
    {
        string? text = null;
        try { text = Marshal.PtrToStringUTF8((nint)ResultText(result)); }
        catch (Exception) { }
        return $"{text ?? "AAUDIO_ERROR"} ({result})";
    }
}
