using System.Collections.Concurrent;
using System.Diagnostics;

namespace Horizon.Logging.Loggers;

/// <summary>
/// The logger of the engine. Messages come in from any thread and are written to the console and to a file by a thread of its own,
/// so nobody who logs ever waits for either of the two.
/// </summary>
public class ConcurrentLogger : ILoggerDisposable
{
    private const string DEFAULT_FILE = "debug.log";

    // How long (in milliseconds) closing the logger waits for the last messages to be written
    private const int LONGEST_FLUSH = 2000;

    // The most lines written to a file shared with other processes on one turn of it, so they get theirs in a burst too
    private const int MOST_AT_ONCE = 256;

    private static readonly Lazy<ConcurrentLogger> _logger = new(() => new ConcurrentLogger(DEFAULT_FILE), true);

    public static ConcurrentLogger Instance => _logger.Value;

    private readonly BlockingCollection<LogMessage> messages = new(new ConcurrentQueue<LogMessage>());
    private readonly StreamWriter? writer;
    private readonly Thread worker;
    private readonly Mutex? fileGate;
    private int disposed;

    // Messages logged and not written yet, the ones on their way out of the queue among them, which is what Flush waits for
    private int pending;

    /// <summary>
    /// The file the log ends up in, empty if it only goes to the console.
    /// </summary>
    public string File { get; } = string.Empty;

    /// <summary>
    /// Anything less important than this is thrown away. Everything is kept unless somebody says otherwise.
    /// </summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <param name="logFile">Where the log is written to, an empty string for the console only.</param>
    public ConcurrentLogger(string logFile, bool singleFile = false)
    {
        if (singleFile && logFile.Length > 0)
        {
            string path = Path.GetFullPath(logFile);
            if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
            string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..24];
            fileGate = new Mutex(false, "horizon-log-" + key);
        }
        if (logFile.Length > 0)
        {
            if (fileGate is null) (writer, File) = Open(logFile);
            else
            {
                EnterFile();
                try { (writer, File) = Open(logFile, shared: true); }
                finally { fileGate.ReleaseMutex(); }
            }
        }

        worker = new Thread(Write)
        {
            Name = "Logger",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        worker.Start();
    }

    /// <summary>
    /// Helper method to open the log file. A second copy of the game in the same folder finds the file taken,
    /// it gets one with its process id in the name instead of falling over.
    /// </summary>
    private static (StreamWriter?, string) Open(string logFile, bool shared = false)
    {
        string taken = Path.ChangeExtension(logFile, null) + $"-{Environment.ProcessId}" + Path.GetExtension(logFile);

        foreach (string file in shared ? [logFile] : (string[])[logFile, taken])
        {
            try
            {
                var stream = new FileStream(file, FileMode.Create, FileAccess.Write, shared ? FileShare.ReadWrite | FileShare.Delete : FileShare.Read);
                return (new StreamWriter(stream), file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Somebody else has it, or we are not allowed to write here. On to the next name
            }
        }

        return (null, string.Empty);
    }

    public virtual void Log(LogLevel level, string message)
    {
        if (level < MinimumLevel || Volatile.Read(ref disposed) != 0) return;

        Interlocked.Increment(ref pending);
        try
        {
            messages.Add(new LogMessage(message, level));
        }
        catch (InvalidOperationException)
        {
            // Closed between the test above and here, the message is too late
            Interlocked.Decrement(ref pending);
        }
    }

    public void Log(LogLevel level, object message) => Log(level, message.ToString() ?? string.Empty);

    /// <summary>
    /// The thread that does the writing. It sleeps until there is something to write, and the file is flushed whenever it has caught up.
    /// </summary>
    private void Write()
    {
        foreach (LogMessage message in messages.GetConsumingEnumerable())
        {
            string line = Show(message);
            int written = 1;

            if (fileGate is null)
            {
                writer?.WriteLine(line);
                if (messages.Count == 0) writer?.Flush();
            }
            else if (writer is not null)
            {
                EnterFile();
                try
                {
                    // Another instance may have reset or appended to the same file since this process last wrote.
                    writer.BaseStream.Seek(0, SeekOrigin.End);
                    writer.WriteLine(line);

                    // Whatever else has come in by now goes in on the same turn of the file. Taking the lock between
                    // processes, seeking and flushing for every single line made a burst of the log (a program
                    // starting) a few system calls a line
                    while (written < MOST_AT_ONCE && messages.TryTake(out LogMessage next))
                    {
                        writer.WriteLine(Show(next));
                        written++;
                    }

                    writer.Flush();
                }
                finally { fileGate.ReleaseMutex(); }
            }

            Interlocked.Add(ref pending, -written);
        }
    }

    /// <summary>
    /// Helper method to write a message to the console in its colour, and make the line it is in the file.
    /// </summary>
    private static string Show(in LogMessage message)
    {
        string line = $"[{message.Level}] {message.Message}";

        Console.ForegroundColor = ColorOf(message.Level);
        Console.WriteLine(line);
        Console.ResetColor();

        return line;
    }

    private void EnterFile()
    {
        try { fileGate!.WaitOne(); }
        catch (AbandonedMutexException) { /* Ownership is granted when the previous writer exited unexpectedly. */ }
    }

    private static ConsoleColor ColorOf(LogLevel level) => level switch
    {
        LogLevel.Warning => ConsoleColor.Yellow,
        LogLevel.Error => ConsoleColor.Red,
        LogLevel.Fatal => ConsoleColor.DarkRed,
        LogLevel.Success => ConsoleColor.Green,
        _ => ConsoleColor.Gray
    };

    /// <summary>
    /// Waits until everything that was logged so far has been written, or until it has waited for too long.
    /// </summary>
    public void Flush()
    {
        long started = Stopwatch.GetTimestamp();

        while (Volatile.Read(ref pending) > 0 && Stopwatch.GetElapsedTime(started).TotalMilliseconds < LONGEST_FLUSH)
            Thread.Sleep(1);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;

        // Whatever is still in the queue gets written before the thread stops
        messages.CompleteAdding();
        worker.Join(LONGEST_FLUSH);

        writer?.Dispose();
        GC.SuppressFinalize(this);
    }
}
