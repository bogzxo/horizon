using System.Collections.Concurrent;

using Horizon.Logging;

using AndroidLog = Android.Util.Log;
using AndroidLogPriority = Android.Util.LogPriority;

namespace Horizon.Platform.Android;

/// <summary>
/// The log on a phone, logcat under the tag Horizon and debug.log in the app's files folder, written by a thread of
/// its own so nobody who logs waits on either. The engine's own logger would write debug.log in the working directory,
/// which on Android is the root of the file system and not ours, so this one is installed before anything logs.
/// <para>
/// It lives as long as the process. The engine "disposes" the logger as it goes, and an activity made again after
/// that still has to log, so Dispose only waits for what's queued to be written and carries on taking messages.
/// The debug.log of the run before is kept as debug-previous.log, for a crash that took the process down.
/// </para>
/// </summary>
public sealed class AndroidLogger : ILoggerDisposable
{
    /// <summary>What logcat lines are tagged, <c>adb logcat -s Horizon</c>.</summary>
    public const string TAG = "Horizon";

    // logcat cuts a line at about 4 KB, longer messages go in pieces
    private const int LOGCAT_PIECE = 3800;

    private readonly BlockingCollection<(LogLevel Level, string Message)> messages = new(new ConcurrentQueue<(LogLevel, string)>());
    private readonly StreamWriter? writer;
    private readonly Thread worker;
    private long pending;

    /// <summary>Where debug.log is, empty when it couldn't be opened and only logcat has it.</summary>
    public string File { get; } = string.Empty;

    /// <param name="folder">The app's files folder.</param>
    public AndroidLogger(string folder)
    {
        try
        {
            string path = Path.Combine(folder, "debug.log");
            string previous = Path.Combine(folder, "debug-previous.log");
            if (System.IO.File.Exists(path)) System.IO.File.Move(path, previous, overwrite: true);
            writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read));
            File = path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AndroidLog.Warn(TAG, $"debug.log couldn't be opened in {folder}, the log only goes to logcat. {e.Message}");
        }

        worker = new Thread(Write) { Name = "Logger", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        worker.Start();
    }

    public void Log(LogLevel level, string message)
    {
        Interlocked.Increment(ref pending);
        try
        {
            messages.Add((level, message));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Decrement(ref pending);
        }
    }

    public void Log(LogLevel level, object message) => Log(level, message.ToString() ?? string.Empty);

    private void Write()
    {
        foreach ((LogLevel level, string message) in messages.GetConsumingEnumerable())
        {
            try
            {
                ToLogcat(level, message);
                if (writer is not null)
                {
                    writer.WriteLine($"[{level}] {message}");
                    if (messages.Count == 0) writer.Flush();
                }
            }
            catch (Exception)
            {
                // The log failing must never take the game with it
            }
            finally
            {
                Interlocked.Decrement(ref pending);
            }
        }
    }

    private static void ToLogcat(LogLevel level, string message)
    {
        AndroidLogPriority priority = level switch
        {
            LogLevel.Warning => AndroidLogPriority.Warn,
            LogLevel.Error => AndroidLogPriority.Error,
            LogLevel.Fatal => AndroidLogPriority.Assert,
            _ => AndroidLogPriority.Info
        };

        for (int at = 0; at < message.Length || at == 0; at += LOGCAT_PIECE)
        {
            AndroidLog.WriteLine(priority, TAG, message.Length <= LOGCAT_PIECE ? message : message.Substring(at, Math.Min(LOGCAT_PIECE, message.Length - at)));
            if (message.Length <= LOGCAT_PIECE) break;
        }
    }

    /// <summary>Waits a little while for everything queued to be written. Any thread.</summary>
    public void Flush(int milliseconds = 2000)
    {
        long until = Environment.TickCount64 + milliseconds;
        while (Interlocked.Read(ref pending) > 0 && Environment.TickCount64 < until) Thread.Sleep(1);
    }

    /// <summary>Only flushes, see the class remarks. The logger goes with the process.</summary>
    public void Dispose() => Flush();
}
