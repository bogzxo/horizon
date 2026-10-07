namespace Bogz.Logging;

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

    public static void Write(LogLevel level, string message) => Logger.Instance.Log(level, message);
}
