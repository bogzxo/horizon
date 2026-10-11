namespace Horizon.Logging;

/// <summary>
/// The short way to write to the log from anywhere, which is the console and debug.log next to the game.
/// <code>
/// Log.Info("[Fight] Round two.");
/// Log.Warning($"[MapLoader] '{name}' has no spawns!");
/// </code>
/// It goes to whatever <see cref="Logger.Instance"/> is, so a game that wants its log somewhere else only has to swap that.
/// Nothing here waits for the disk or the console, a thread of its own does the writing.
/// </summary>
public static class Log
{
    /// <summary>
    /// Raised for every message that is written, on the thread that writes it, for whatever shows the log besides the
    /// console and the file, the log drawer of the Skyline debugger for one. Keep it quick, the
    /// game is waiting.
    /// </summary>
    public static event Action<LogLevel, string>? Written;

    public static void Info(string message) => Write(LogLevel.Info, message);

    /// <summary>
    /// For the good news, it shows up green.
    /// </summary>
    public static void Success(string message) => Write(LogLevel.Success, message);

    public static void Warning(string message) => Write(LogLevel.Warning, message);

    public static void Error(string message) => Write(LogLevel.Error, message);

    /// <summary>
    /// For the kind of fuck up there is no carrying on from.
    /// </summary>
    public static void Fatal(string message) => Write(LogLevel.Fatal, message);

    public static void Write(LogLevel level, string message)
    {
        Logger.Instance.Log(level, message);
        Written?.Invoke(level, message);
    }
}
