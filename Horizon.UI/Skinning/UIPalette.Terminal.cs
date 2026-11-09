using System.Numerics;

namespace Horizon.UI.Skinning;

// The sixteen colours a terminal is drawn in, for a theme that wants its own rather than the ones worked out of the rest
// of its colours (TerminalPalette.FromPalette). A theme writes them under terminal by their names, red or bright_red,
// and only the ones it wants its own way. Visual Studio's theme has Windows Terminal's Campbell, whose magenta isn't
// the keywords' blue.
public sealed partial class UIPalette
{
    /// <summary>The names of the sixteen as a theme writes them, black to white and then the bright half.</summary>
    public static readonly string[] TerminalNames =
    [
        "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
        "bright_black", "bright_red", "bright_green", "bright_yellow", "bright_blue", "bright_magenta", "bright_cyan", "bright_white"
    ];

    /// <summary>
    /// The terminal's sixteen colours as the theme wrote them, null for one it didn't (worked out like the rest), and the
    /// whole of it null for a theme that wrote none.
    /// </summary>
    public Vector4?[]? Terminal { get; set; }

    /// <summary>Sets one of the sixteen by its name, false for a name that isn't one.</summary>
    public bool SetTerminal(string name, Vector4 color)
    {
        int at = Array.IndexOf(TerminalNames, name);
        if (at < 0)
            return false;

        Terminal ??= new Vector4?[TerminalNames.Length];
        Terminal[at] = color;
        return true;
    }
}
