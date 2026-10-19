using System.Text.RegularExpressions;

namespace Horizon.UI.Components;

/// <summary>A colour out of the terminal's palette for what a rule matched, with an underline or not.</summary>
public enum TerminalOutputColour : byte
{
    None,
    Red,
    Green,
    Yellow,
    Blue,
    Magenta,
    Cyan,

    /// <summary>The text's own colour faded, what matters less (the frames of a stack trace that aren't yours).</summary>
    Dim
}

/// <summary>A rule of the output's colours, what to look for and the colour of what is found, a plugin can add its own.</summary>
public sealed class TerminalOutputRule
{
    public const byte UNDERLINE = 0x10;

    public string Name { get; }
    public Regex Pattern { get; }
    public TerminalOutputColour Colour { get; }
    public bool Underline { get; }

    /// <summary>What a cell matched by it is written as, the colour and the underline in one byte.</summary>
    public byte Code => (byte)((byte)Colour | (Underline ? UNDERLINE : 0));

    public TerminalOutputRule(string name, string pattern, TerminalOutputColour colour, bool underline = false, bool ignoreCase = false)
    {
        Name = name;
        Pattern = new Regex(pattern, RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : 0), TimeSpan.FromMilliseconds(20));
        Colour = colour;
        Underline = underline;
    }

    /// <summary>A colour by the name a plugin uses, red, green, yellow, blue, magenta, cyan or dim, None for one it isn't.</summary>
    public static TerminalOutputColour ColourOf(string name) => name.ToLowerInvariant() switch
    {
        "red" or "error" => TerminalOutputColour.Red,
        "green" or "success" => TerminalOutputColour.Green,
        "yellow" or "warning" => TerminalOutputColour.Yellow,
        "blue" or "link" => TerminalOutputColour.Blue,
        "magenta" or "purple" => TerminalOutputColour.Magenta,
        "cyan" => TerminalOutputColour.Cyan,
        "dim" => TerminalOutputColour.Dim,
        _ => TerminalOutputColour.None
    };
}

/// <summary>
/// Colours what programs write by what it looks like, where they left it plain (a program's own colours always win).
/// Rules are a pattern and a colour of the palette, gone through in order with a later one going over an earlier one,
/// so the rules of a whole line (a diff's) come first and the small ones (a path, a number) after. Matched with
/// <see cref="Regex.EnumerateMatches(ReadOnlySpan{char})"/>, no garbage, a row at a time and only when it changed.
/// </summary>
public sealed class TerminalOutputHighlighter
{
    /// <summary>The rules a terminal comes with.</summary>
    public static TerminalOutputRule[] Shipped =>
    [
        new("diff_added", @"^\+(?!\+\+ ).*", TerminalOutputColour.Green),
        new("diff_removed", @"^-(?!-- ).*", TerminalOutputColour.Red),
        new("stack_frame", @"^\s+at\s.*", TerminalOutputColour.Dim),
        new("json_key", @"""[^""\r\n]{1,64}""(?=\s*:)", TerminalOutputColour.Cyan),
        new("number", @"(?<![\w.])\d+(\.\d+)?(ms|s|m|h|KB|MB|GB|kB|B|%)?(?![\w.])", TerminalOutputColour.Magenta),
        new("timestamp", @"\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d+)?)?Z?", TerminalOutputColour.Blue),
        // after the numbers, or the numbers in it are numbers
        new("diff_hunk", @"^@@ .*? @@", TerminalOutputColour.Cyan),
        new("success", @"\b(succeeded|success|passed|ok|done)\b", TerminalOutputColour.Green, ignoreCase: true),
        new("warning", @"\b(warning|warn|deprecated)\b", TerminalOutputColour.Yellow, ignoreCase: true),
        new("error", @"\b(error|errors|fatal|failed|failure|fail|exception|panic(ked)?)\b", TerminalOutputColour.Red, ignoreCase: true),
        new("path", @"(?<![\w/])(?:[A-Za-z]:\\|\.{0,2}/|~/)[^\s:""'<>|*?()]+(?::\d+(?::\d+)?|\(\d+(,\d+)?\))?", TerminalOutputColour.Cyan, underline: true),
        new("url", @"\bhttps?://[^\s""'<>]+", TerminalOutputColour.Blue, underline: true)
    ];

    public List<TerminalOutputRule> Rules { get; } = [.. Shipped];

    /// <summary>Goes up when the rules change, so the rows on screen are coloured again.</summary>
    public int Version { get; private set; }

    /// <summary>A rule added, a plugin's.</summary>
    public void Add(TerminalOutputRule rule)
    {
        Rules.RemoveAll(existing => existing.Name == rule.Name);
        Rules.Add(rule);
        Version++;
    }

    /// <summary>The cells of a row written with what each matched, 0 for nothing.</summary>
    public void Colour(ReadOnlySpan<char> row, Span<byte> codes)
    {
        codes[..row.Length].Clear();
        for (int r = 0; r < Rules.Count; r++)
        {
            TerminalOutputRule rule = Rules[r];
            try
            {
                foreach (ValueMatch match in rule.Pattern.EnumerateMatches(row))
                {
                    if (match.Length > 0)
                        codes.Slice(match.Index, match.Length).Fill(rule.Code);
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }
    }
}
