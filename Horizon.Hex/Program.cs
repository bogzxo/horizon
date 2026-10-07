using System.Globalization;
using System.Numerics;

using Horizon.Core;
using Horizon.Engine;

namespace Horizon.Hex;

internal class Program
{
    private const string ARGUMENT_OPEN = "--open";
    private const string ARGUMENT_WORKSPACE = "--workspace";
    private const string ARGUMENT_SIZE = "--size";
    private const string ARGUMENT_CHECK = "--check";
    private const string ARGUMENT_SELF_TEST = "--selftest";
    private const string ARGUMENT_WHEEL = "--wheel";
    private const string ARGUMENT_EXIT = "--exit";
    private const string ARGUMENT_EXERCISE = "--exercise";
    private const string ARGUMENT_SCALE = "--scale";
    private const string ARGUMENT_BROWSE = "--browse";

    // Where layouts go when nobody says otherwise, next to the editor
    private const string DEFAULT_WORKSPACE = "layouts";

    /// <summary>
    /// Starts the editor. Without arguments it comes up maximised with an empty layout.
    /// <list type="bullet">
    /// <item><c>--workspace ../Game/Assets/ui</c> is the folder layouts are opened from and saved to.</item>
    /// <item><c>--open menu.hor</c> starts with a layout open.</item>
    /// <item><c>--browse</c> starts by asking which layout to open, with the dialog Windows has for it.</item>
    /// <item><c>--size 1280x720</c> starts in a window of that size rather than maximised.</item>
    /// <item><c>--check a.hor folder</c> opens every layout named (or in the folders named) and prints whether it writes back the same.</item>
    /// <item><c>--exercise</c> also edits, saves, reopens and undoes every layout that is checked. It writes over them, so point it at copies.</item>
    /// <item><c>--scale 1.25</c> draws the editor that much bigger, rather than as big as it was left the last time (the selector in its toolbar).</item>
    /// <item><c>--selftest</c> has the editor click through itself and print how that went, <c>--wheel</c> makes it wait for the real mouse wheel as well.</item>
    /// <item><c>--exit</c> closes the editor once its checks or its self-test are done, with an exit code of 1 if anything failed.</item>
    /// </list>
    /// </summary>
    public static void Main(string[] args)
    {
        bool selfTest = args.Contains(ARGUMENT_SELF_TEST);
        string workspace = ValueOf(args, ARGUMENT_WORKSPACE) ?? DEFAULT_WORKSPACE;

        if (selfTest)
        {
            // The test makes and saves layouts of its own, somewhere nobody's work is in the way of it
            workspace = Path.Combine(Path.GetTempPath(), "horizon-hex-selftest");
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }

        Directory.CreateDirectory(workspace);

        var options = new HexOptions(
            Open: ValueOf(args, ARGUMENT_OPEN),
            Workspace: workspace,
            SelfTest: selfTest,
            Wheel: args.Contains(ARGUMENT_WHEEL),
            Check: ValuesOf(args, ARGUMENT_CHECK),
            Exit: args.Contains(ARGUMENT_EXIT),
            Exercise: args.Contains(ARGUMENT_EXERCISE),
            Browse: args.Contains(ARGUMENT_BROWSE),
            // The editor testing itself starts out the same size every time, unless it is told which
            Scale: float.TryParse(ValueOf(args, ARGUMENT_SCALE), CultureInfo.InvariantCulture, out float scale) ? scale : selfTest ? 1.0f : null);

        // The editor lays itself out to whatever window it gets, so it takes all there is
        Vector2? size = ParseSize(ValueOf(args, ARGUMENT_SIZE));

        using var engine = new GameEngine(GameEngineConfiguration.Default with
        {
            WindowConfiguration = WindowManagerConfiguration.Default1600x900 with
            {
                WindowTitle = "Horizon.Hex",
                WindowSize = size ?? WindowManagerConfiguration.Default1600x900.WindowSize,
                Maximized = size is null
            }
        });

        engine.SetScene(new HexScene(options));
        engine.Run();
    }

    private static string? ValueOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// Everything that follows an argument up to the next one that starts with two dashes.
    /// </summary>
    private static string[] ValuesOf(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0)
            return [];

        return [.. args.Skip(index + 1).TakeWhile(value => !value.StartsWith("--"))];
    }

    private static Vector2? ParseSize(string? text)
    {
        if (text?.Split('x', 'X') is [var width, var height]
            && float.TryParse(width, CultureInfo.InvariantCulture, out float x)
            && float.TryParse(height, CultureInfo.InvariantCulture, out float y)
            && x >= 320 && y >= 200)
        {
            return new Vector2(x, y);
        }

        return null;
    }
}
