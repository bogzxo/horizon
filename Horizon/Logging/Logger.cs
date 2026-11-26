using Horizon.Logging.Loggers;

namespace Horizon.Logging;

/// <summary>
/// Holds the logger everything writes to. That is the <see cref="ConcurrentLogger"/> unless a game hands over one of its own.
/// See <see cref="Log"/> for the short way to write to it.
/// </summary>
public class Logger
{
    private static ILogger? _logger;

    /// <summary>
    /// Swaps the logger everything writes to from now on.
    /// </summary>
    public static void InitializeLogger(ILogger logger)
    {
        Volatile.Write(ref _logger, logger);
    }

    public static ILogger Instance => Volatile.Read(ref _logger) ?? ConcurrentLogger.Instance;

    /// <summary>
    /// Flushes and closes the logger, if it is one that can be closed.
    /// </summary>
    public static void Dispose()
    {
        ILogger? current = Volatile.Read(ref _logger);
        if (current is not null)
        {
            if (current is IDisposable disposable)
                disposable.Dispose();
        }
        else if (ConcurrentLogger.HasInstance)
        {
            ConcurrentLogger.Instance.Dispose();
        }
    }
}
