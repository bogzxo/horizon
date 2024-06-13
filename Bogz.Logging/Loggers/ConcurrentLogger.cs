using System.Collections.Concurrent;
using System.Text;

namespace Bogz.Logging.Loggers;

/// <summary>
/// Thread-safe logger.
/// </summary>
public class ConcurrentLogger : ILoggerDisposable
{
    private static readonly Lazy<ConcurrentLogger> _logger = new Lazy<ConcurrentLogger>(
        () => new ConcurrentLogger("debug.log"),
        true
    );

    public static ConcurrentLogger Instance
    {
        get => _logger.Value;
    }

    private readonly ConcurrentQueue<LogMessage> logMessages;
    private readonly StreamWriter? writer;
    private readonly Task loggingTask;
    private readonly CancellationTokenSource tokenSource;
    private readonly ManualResetEventSlim eventSlim;

    public ConcurrentLogger(string logFile)
    {
        logMessages = new ConcurrentQueue<LogMessage>();

        eventSlim = new ManualResetEventSlim();
        tokenSource = new CancellationTokenSource();
        loggingTask = MessageLoggingCallbackAsync();

        if (logFile.Equals(string.Empty))
            return;
        if (File.Exists(logFile))
            File.Delete(logFile);

        writer = new StreamWriter(
            logFile,
            new FileStreamOptions()
            {
                Access = FileAccess.Write,
                Mode = FileMode.CreateNew,
                Options = FileOptions.Asynchronous
            }
        );
    }

    public virtual void Log(LogLevel level, string message)
    {
        logMessages.Enqueue(new(message, level));
        eventSlim.Set();
    }

    private Task MessageLoggingCallbackAsync() => Task.Run(MessageLoggingCallback, tokenSource.Token);
    private void MessageLoggingCallback()
    {
        StringBuilder sb = new();

        while (!tokenSource.IsCancellationRequested)
        {
            eventSlim.Wait();
            eventSlim.Reset();

            if (logMessages.TryDequeue(out var msgQ))
            {
                string msg = $"[{msgQ.Level}] {msgQ.Message}";

                sb.AppendLine(msg);

                var color = msgQ.Level switch
                {
                    LogLevel.Info => ConsoleColor.Gray,
                    LogLevel.Warning => ConsoleColor.Yellow,
                    LogLevel.Error => ConsoleColor.Red,
                    LogLevel.Fatal => ConsoleColor.DarkRed,
                    LogLevel.Success => ConsoleColor.Green,
                    _ => Console.ForegroundColor,
                };

                Console.ForegroundColor = color;
                Console.WriteLine(msg);

                if (sb.Length > 100)
                {
                    writer?.Write(sb.ToString());
                    sb.Clear();
                }
            }
            if (sb.Length > 0)
                writer?.Write(sb.ToString());
            Console.ForegroundColor = ConsoleColor.White;
        }

    }

    public void Log(LogLevel level, object message)
    {
        Log(level, message);
    }

    public void Dispose()
    {
        Log(LogLevel.Info, "[Logger] Disposing self!");
        Console.WriteLine("Flushing logger.");
        tokenSource.Cancel();
        Flush();
        writer?.Flush();
        loggingTask.Wait();
        tokenSource.Dispose();
        loggingTask.Dispose();

        Console.WriteLine("Disposing logger.");
        writer?.Close();
        writer?.Dispose();
        Console.WriteLine("Logger Finalized.");

        GC.SuppressFinalize(this);
    }

    public void Flush() => eventSlim.Set();
}