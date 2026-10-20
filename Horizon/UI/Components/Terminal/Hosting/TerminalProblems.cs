using System.Text;
using System.Text.RegularExpressions;

namespace Horizon.UI.Components;

/// <summary>A problem a program said about a file, where, how bad and what, read off a terminal by a matcher.</summary>
public readonly record struct TerminalProblem(string File, int Line, int Column, string Message, bool Warning, string Matcher);

/// <summary>
/// A way a program says what is wrong, VS Code's problem matcher. A pattern with groups for the file, the line, the
/// column, the kind (error or warning, a warning when it says so) and the message, and a name. A matcher that is two
/// lines (Rust says the message and then where on the line after) has a second pattern for the place, which takes the
/// message of the line before.
/// </summary>
public sealed record TerminalProblemMatcher(string Name, Regex Pattern, Regex? Place = null)
{
    public TerminalProblemMatcher(string name, string pattern, string? place = null)
        : this(name, new Regex(pattern, RegexOptions.Compiled), place is null ? null : new Regex(place, RegexOptions.Compiled)) { }

    /// <summary>The matchers that come with the terminal, .NET and MSBuild, Python, gcc and clang, Rust, Slang and HIDL.</summary>
    public static readonly TerminalProblemMatcher[] Shipped =
    [
        // A file of these has no > in it, which keeps a prompt that echoed one of these lines from being read as a file
        new("dotnet", @"^\s*(?<file>[^\s(>][^(>]*?)\((?<line>\d+),(?<column>\d+)\)\s*:\s*(?<kind>error|warning)\s+(?<code>[A-Z]+\d+)\s*:\s*(?<message>.*?)(?:\s+\[[^\]]*\])?$"),
        new("msbuild", @"^\s*(?<file>[^\s:][^:]*?)\s*:\s*(?<kind>error|warning)\s+(?<code>MSB\d+)\s*:\s*(?<message>.*?)(?:\s+\[[^\]]*\])?$"),
        new("python", @"^\s*(?<kind>[A-Za-z]*Error): (?<message>.*)$", @"^\s*File ""(?<file>[^""]+)"", line (?<line>\d+)"),
        new("gcc", @"^(?<file>(?:[A-Za-z]:)?[^:\s][^:]*?):(?<line>\d+):(?<column>\d+): (?<kind>error|warning|fatal error): (?<message>.*)$"),
        new("rust", @"^(?<kind>error|warning)(?:\[(?<code>E\d+)\])?: (?<message>.*)$", @"^\s*--> (?<file>[^:]+):(?<line>\d+):(?<column>\d+)"),
        new("slang", @"^(?<file>[^\s(][^(]*?)\((?<line>\d+)\): (?<kind>error|warning) (?<code>\d+): (?<message>.*)$"),
        new("hidl", @"^(?<file>[^\s:][^:]*\.hor):(?<line>\d+):(?<column>\d+): (?<message>.*)$")
    ];

    /// <summary>A problem out of a line, null for a line this doesn't match. The kind out of the line, a warning when it says so.</summary>
    public TerminalProblem? Match(string line)
    {
        System.Text.RegularExpressions.Match match = Pattern.Match(line);
        if (!match.Success || !match.Groups["file"].Success)
            return null;

        return Problem(match, match);
    }

    /// <summary>A two line problem, the message of one line and the place of the next, null when they aren't one.</summary>
    public TerminalProblem? Match(string message, string place)
    {
        if (Place is null)
            return null;

        System.Text.RegularExpressions.Match said = Pattern.Match(message), at = Place.Match(place);
        return said.Success && at.Success ? Problem(said, at) : null;
    }

    private TerminalProblem Problem(System.Text.RegularExpressions.Match said, System.Text.RegularExpressions.Match at)
    {
        string kind = said.Groups["kind"].Success ? said.Groups["kind"].Value : "error";
        string code = said.Groups["code"].Success ? said.Groups["code"].Value + ": " : string.Empty;
        string message = said.Groups["message"].Success ? said.Groups["message"].Value.Trim() : said.Value.Trim();
        if (Name == "python")
            message = $"{kind}: {message}";
        return new TerminalProblem(at.Groups["file"].Value.Trim(), Number(at, "line"), Number(at, "column"), code + message,
            kind.StartsWith("warn", StringComparison.OrdinalIgnoreCase), Name);
    }

    private static int Number(System.Text.RegularExpressions.Match match, string group) =>
        match.Groups[group].Success && int.TryParse(match.Groups[group].Value, out int number) ? number : 0;
}

/// <summary>
/// What a terminal's program printed, read a line at a time for problems. The bytes as they come, escape sequences and
/// all, a line ended by a newline, the sequences taken out of it and the matchers run over it. A two line matcher
/// keeps the message of the line before for the place on this one (Python says where and then what, so its place is
/// kept for the message after it). Nothing of the screen in it, it reads the stream.
/// </summary>
public sealed class TerminalProblemReader(IReadOnlyList<TerminalProblemMatcher> matchers, Action<TerminalProblem> found)
{
    private static readonly Regex Escapes = new(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])", RegexOptions.Compiled);

    private readonly StringBuilder pending = new();
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    private char[] chars = new char[4096];

    /// <summary>Bytes the program wrote, as they came.</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        if (chars.Length < bytes.Length + 4)
            chars = new char[bytes.Length + 4];

        int count = decoder.GetChars(bytes, chars, flush: false);
        for (int i = 0; i < count; i++)
        {
            char character = chars[i];
            if (character == '\n')
            {
                Line(pending.ToString());
                pending.Clear();
            }
            else if (character != '\r' && pending.Length < 4096)
            {
                pending.Append(character);
            }
        }
    }

    /// <summary>Text the program wrote, for a test.</summary>
    public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    // The marks of a shell with integration (OSC 133): where a prompt starts, where it ends and the typing starts, where
    // the command's output starts
    private const string PROMPT_STARTS = "\u001b]133;A", PROMPT_ENDS = "\u001b]133;B", OUTPUT_STARTS = "\u001b]133;C";

    /// <summary>
    /// A line without the prompts on it and what was typed at them: from where a prompt starts (or the line's start, for
    /// the end of a prompt that started on a line before) up to where the output starts, or the line's end. What was
    /// typed is an error echoed back as a command and no error of anybody's. Windows' console draws with the cursor
    /// rather than new lines, so a line can be an error and the next prompt both, and the error stays. It also sends the
    /// marks on ahead of what it draws, so a prompt with nothing between its marks is only the marks, and what comes
    /// after them (a new tab's first output) is kept.
    /// </summary>
    internal static string WithoutPrompts(string raw)
    {
        int at = 0;
        while (true)
        {
            int starts = raw.IndexOf(PROMPT_STARTS, at, StringComparison.Ordinal), ends = raw.IndexOf(PROMPT_ENDS, at, StringComparison.Ordinal);
            if (starts < 0 && ends < 0)
                return raw;

            if (starts >= 0 && ends > starts && IsBlank(raw.AsSpan(starts + PROMPT_STARTS.Length, ends - starts - PROMPT_STARTS.Length)))
            {
                int markEnds = raw.IndexOf('\a', ends);
                raw = raw[..starts] + raw[(markEnds < 0 ? ends + PROMPT_ENDS.Length : markEnds + 1)..];
                at = starts;
                continue;
            }

            int cut = starts >= 0 && (ends < 0 || starts < ends) ? starts : at == 0 ? 0 : ends;
            int output = raw.IndexOf(OUTPUT_STARTS, Math.Max(cut, ends), StringComparison.Ordinal);
            raw = output < 0 ? raw[..cut] : raw[..cut] + raw[output..];
            at = cut;
        }
    }

    // Whether what's between two marks draws nothing, the end of the first mark and escape sequences aside
    private static bool IsBlank(ReadOnlySpan<char> between)
    {
        string text = Escapes.Replace(between.ToString(), string.Empty);
        foreach (char c in text)
        {
            if (!char.IsWhiteSpace(c) && c != '\a')
                return false;
        }

        return true;
    }

    private void Line(string raw)
    {
        // A shell without the marks has its prompt kept out by what it looks like (no > in a file, see Shipped)
        string line = Escapes.Replace(WithoutPrompts(raw), string.Empty).TrimEnd();
        if (line.Length == 0)
            return;

        foreach (TerminalProblemMatcher matcher in matchers)
        {
            if (matcher.Place is null)
            {
                if (matcher.Match(line) is { } problem)
                {
                    found(problem);
                    return;
                }

                continue;
            }

            // Rust says the message first and the place after, Python the place first (the deepest frame last) and the
            // message a line or two after, so each two line matcher keeps the last of either it has seen
            if (matcher.Place.IsMatch(line))
            {
                if (lastMessage.Remove(matcher, out string? message) && matcher.Match(message, line) is { } after)
                {
                    found(after);
                    return;
                }

                lastPlace[matcher] = line;
            }
            else if (matcher.Pattern.IsMatch(line))
            {
                if (lastPlace.Remove(matcher, out string? place) && matcher.Match(line, place) is { } before)
                {
                    found(before);
                    return;
                }

                lastMessage[matcher] = line;
            }
        }
    }

    private readonly Dictionary<TerminalProblemMatcher, string> lastMessage = [], lastPlace = [];
}
