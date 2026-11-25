using System.Text.RegularExpressions;

namespace Horizon.UI.Components;

/// <summary>
/// Knowing when what is typed is a secret: a password, a passphrase, a PIN. Such input is never kept in the history,
/// never told to a plugin and never handed to a prediction. Two signs, either one is enough: the program doesn't echo
/// what was typed (the keys went in and none of them came back onto the screen, which is what a no-echo prompt is),
/// and the prompt the cursor sits after asks for one ("[sudo] password for bob:", "Enter passphrase for key", "PIN:").
/// </summary>
public static partial class TerminalSecrets
{
    // The end of the text before the cursor, what is drawn in place of the typed characters (stars, dots) left off
    [GeneratedRegex(@"(pass(word|phrase|code|wd)|\bpin\b|\bsecret\b|\botp\b|one[- ]time (code|password)|verification code|security code|2fa|mfa code|api[ _-]?key|access token|auth(entication)? token|credentials?|private key)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeywordPattern();

    // Longest prompt looked at, a long line is a command that happens to end in the word
    private const int LONGEST_PROMPT = 120;

    // The words of a prompt are in its last segment, after a shell prompt's "$ " or "> " if the row has one
    private static string LastSegment(string text)
    {
        int cut = -1;
        foreach (string mark in new[] { "$ ", "# ", "% ", "> " })
        {
            int at = text.LastIndexOf(mark, StringComparison.Ordinal);
            if (at >= 0)
                cut = Math.Max(cut, at + mark.Length - 1);
        }
        return cut >= 0 ? text[(cut + 1)..] : text;
    }

    /// <summary>
    /// Whether a row is the shell's own prompt: it ends in $ # % or > (after the mask characters are off), which a prompt
    /// for a secret doesn't.
    /// </summary>
    private static bool EndsLikeShellPrompt(string text) => text.Length > 0 && text[^1] is '$' or '#' or '%' or '>';

    /// <summary>
    /// Whether the text before the cursor, with what was typed taken off its end where it is there, is a prompt for a
    /// secret. The row of a normal command is the shell's prompt and the command, which has to end in the typed text
    /// to be left alone, so "git log --grep=password:" is a command and "Password:" is not.
    /// </summary>
    public static bool IsSecretPrompt(string beforeCursor, string typed = "")
    {
        string text = beforeCursor.TrimEnd();
        if (typed.Length > 0 && text.EndsWith(typed.TrimEnd(), StringComparison.Ordinal))
            return false;

        if (text.Length > LONGEST_PROMPT)
            text = text[^LONGEST_PROMPT..];
        if (text.Length == 0)
            return false;

        // What is drawn in place of the typed characters (stars, dots) is left off the end
        text = text.TrimEnd('*', '•', '●', '·', ' ', '\t');
        if (text.Length == 0 || EndsLikeShellPrompt(text) || text[^1] is not (':' or '?' or ']'))
            return false;

        string segment = LastSegment(text);
        if (segment.Length > 90)
            segment = segment[^90..];
        return KeywordPattern().IsMatch(segment);
    }

    /// <summary>
    /// Whether what was typed was echoed: the row the cursor is on has it. Nothing typed is trivially echoed. A program
    /// that echoes nothing (sudo, ssh, a Read-Host -AsSecureString) leaves the row as it was.
    /// </summary>
    public static bool WasEchoed(string typed, string rowBeforeCursor)
    {
        typed = typed.Trim();
        return typed.Length == 0 || rowBeforeCursor.Contains(typed, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a line about to be run was typed at a secret prompt: its keys weren't echoed back, or the prompt asked for
    /// a secret. <paramref name="typed"/> is what was typed, <paramref name="rowBeforeCursor"/> the row the cursor is on.
    /// </summary>
    public static bool IsSecret(string typed, string rowBeforeCursor) =>
        typed.Trim().Length > 0 && (!WasEchoed(typed, rowBeforeCursor) || IsSecretPrompt(rowBeforeCursor, typed));
}
