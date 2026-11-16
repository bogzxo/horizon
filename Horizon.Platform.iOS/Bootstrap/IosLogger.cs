using System.Collections.Concurrent;

using Horizon.Logging;

namespace Horizon.Platform.iOS;

/// <summary>
/// The log on an iPhone, the console (what `xcrun simctl launch --console-pty` and Xcode's console show) and debug.log
/// in the app's Documents folder, written by a thread of its own so nobody who logs waits on either. The engine's own
/// logger would write debug.log in the working directory, which on iOS is the bundle and read only, so this one is
/// installed before anything logs.
/// <para>
/// It lives as long as the process. The engine "disposes" the logger as it goes, so Dispose only waits for what's
/// queued to be written and carries on taking messages. The debug.log of the run before is kept as debug-previous.log,
/// for a crash that took the process down.
/// </para>
/// </summary>
public sealed class IosLogger : ILoggerDisposable
{
    private readonly BlockingCollection<(LogLevel Level, string Message)> messages = new(new ConcurrentQueue<(LogLevel, string)>());
    private readonly StreamWriter? writer;
    private readonly Thread worker;
    private long pending;

    /// <summary>Where debug.log is, empty when it couldn't be opened and only the console has it.</summary>
    public string File { get; } = string.Empty;

    /// <param name="folder">The app's Documents folder.</param>
    public IosLogger(string folder)
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
            Console.Error.WriteLine($"[Horizon] debug.log couldn't be opened in {folder}, the log only goes to the console. {e.Message}");
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
                string line = $"[{level}] {message}";
                Console.WriteLine("[Horizon] " + line);
                if (writer is not null)
                {
                    writer.WriteLine(line);
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

    /// <summary>Waits a little while for everything queued to be written. Any thread.</summary>
    public void Flush(int milliseconds = 2000)
    {
        long until = Environment.TickCount64 + milliseconds;
        while (Interlocked.Read(ref pending) > 0 && Environment.TickCount64 < until) Thread.Sleep(1);
    }

    /// <summary>Only flushes, see the class remarks. The logger goes with the process.</summary>
    public void Dispose() => Flush();
}
