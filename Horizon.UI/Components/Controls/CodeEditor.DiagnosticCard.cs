using System.Text;

using Horizon.UI.Drawing;

namespace Horizon.UI.Components;

// What the pointer resting on something wrong says, a card rather than the bare message. Each thing wrong there has a
// heading of how bad it is in its colour (Error in the error's, Warning in the warning's, Hint dim) with its code after
// it, a link to the code's page when the server gave one, then what it says, and the places it points at under that.
// After them, under a rule, whatever the program says of the word anyway, the type of a name with a warning on it was
// hidden by the warning. It was the first message alone in plain words, "CS0103: The name ... (CS0103)" with no way of
// telling an error from a warning but the squiggle under it.
public partial class CodeEditor
{
    /// <summary>
    /// What the headings of the hover of something wrong call how bad it is, an error, a warning and a hint, in the
    /// language the program is in.
    /// </summary>
    public (string Error, string Warning, string Hint) DiagnosticWords { get; set; } = ("Error", "Warning", "Hint");

    // The most places a diagnostic points at listed under it, and the most diagnostics at one place said
    private const int CARD_RELATED = 3, CARD_MOST = 4;

    // The card last made and what it was made of, so the pointer resting on one place makes it once
    private int cardKey;
    private string card = string.Empty;
    private readonly StringBuilder cardText = new();

    /// <summary>Whether anything wrong is at a place, the parser's or the program's.</summary>
    private bool WrongAt(UITextPosition at)
    {
        if (SyntaxError is { } error && Covers(error, at))
            return true;
        for (int i = 0; i < moreErrors.Count; i++)
        {
            if (Covers(moreErrors[i], at))
                return true;
        }
        for (int i = 0; i < diagnostics.Count; i++)
        {
            if (Covers(diagnostics[i], at))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The card of what's wrong at a place, markdown, with what's said of the word there (markdown too, empty for
    /// nothing) under it. Made again only when what's there or what's said changes.
    /// </summary>
    internal string DiagnosticCard(UITextPosition at, string said)
    {
        // What it's made of, by the diagnostics that cover the place and the text said, none of it allocated
        var hash = new HashCode();
        hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(diagnostics));
        hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(said));
        hash.Add(DiagnosticWords);
        if (SyntaxError is { } first && Covers(first, at))
            hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(first.Message));
        for (int i = 0; i < moreErrors.Count; i++)
        {
            if (Covers(moreErrors[i], at))
                hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(moreErrors[i].Message));
        }
        for (int i = 0; i < diagnostics.Count; i++)
        {
            if (Covers(diagnostics[i], at))
                hash.Add(i);
        }

        int key = hash.ToHashCode();
        if (key == cardKey && card.Length > 0)
            return card;

        cardText.Clear();
        int told = 0;
        if (SyntaxError is { } error && Covers(error, at))
            AddToCard(error, ref told);
        for (int i = 0; i < moreErrors.Count; i++)
        {
            if (Covers(moreErrors[i], at))
                AddToCard(moreErrors[i], ref told);
        }
        for (int i = 0; i < diagnostics.Count; i++)
        {
            if (Covers(diagnostics[i], at))
                AddToCard(diagnostics[i], ref told);
        }

        if (said.Length > 0)
            cardText.Append("\n\n---\n\n").Append(said);

        cardKey = key;
        return card = cardText.ToString();
    }

    // One diagnostic's heading, message and places on the card, the first few of them
    private void AddToCard(in UICodeDiagnostic diagnostic, ref int told)
    {
        if (told++ >= CARD_MOST)
            return;
        if (told > 1)
            cardText.Append("\n\n");

        var (message, code) = SplitCode(diagnostic.Message);
        string word = diagnostic.Hint ? DiagnosticWords.Hint : diagnostic.Warning ? DiagnosticWords.Warning : DiagnosticWords.Error;
        string colour = diagnostic.Hint ? "dim" : diagnostic.Warning ? "warning" : "error";
        cardText.Append("[color:").Append(colour).Append("][b]").Append(UIRichText.Plain(word)).Append("[/b][/color]");
        if (code.Length > 0)
        {
            cardText.Append("  ");
            if (diagnostic.DocumentationUrl is { Length: > 0 } url)
                cardText.Append('[').Append(UIRichText.MarkdownLiteral(code)).Append("](").Append(url).Append(')');
            else
                cardText.Append("[color:dim]").Append(UIRichText.MarkdownLiteral(code)).Append("[/color]");
        }

        cardText.Append('\n').Append(UIRichText.MarkdownLiteral(message));

        if (diagnostic.RelatedLocations is { Count: > 0 } related)
        {
            for (int i = 0; i < related.Count && i < CARD_RELATED; i++)
            {
                UICodeDiagnosticLocation place = related[i];
                cardText.Append("\n[color:dim]").Append(UIRichText.MarkdownLiteral(Path.GetFileName(place.File))).Append(':').Append(place.Line + 1);
                if (place.Message.Length > 0)
                    cardText.Append("  ").Append(UIRichText.MarkdownLiteral(place.Message));
                cardText.Append("[/color]");
            }
        }
    }

    /// <summary>
    /// A message and the code at its end, the way Hex has them from a server ("... (CS0103)", "... (dmd)"), or at its start
    /// ("CS0103: ..."), the message alone and no code when it has none. Codes are a word with a digit in it or a short one.
    /// </summary>
    internal static (string Message, string Code) SplitCode(string message)
    {
        string trimmed = message.TrimEnd();
        if (trimmed.EndsWith(')') && trimmed.LastIndexOf(" (", StringComparison.Ordinal) is int open and > 0)
        {
            string code = trimmed[(open + 2)..^1];
            if (LooksLikeCode(code))
                return (trimmed[..open].TrimEnd(), code);
        }

        int colon = trimmed.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0 && LooksLikeCode(trimmed[..colon]) && trimmed[..colon].Any(char.IsDigit))
            return (trimmed[(colon + 2)..].TrimStart(), trimmed[..colon]);

        return (trimmed, string.Empty);
    }

    // A server's code for a diagnostic, CS0103, IDE0059, reportMissingImports, E501, dmd, and not words in brackets
    private static bool LooksLikeCode(string code)
    {
        if (code.Length is 0 or > 40 || !char.IsAsciiLetter(code[0]))
            return false;
        foreach (char c in code)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or '/'))
                return false;
        }
        // A digit (CS0103), a name in camel case (reportMissingImports) or the compiler Hex names itself (dmd), and not
        // a word somebody put in brackets (optional)
        return code.Any(char.IsDigit) || code == "dmd" || (code.Length >= 6 && code.Any(char.IsAsciiLetterUpper) && code.Any(char.IsAsciiLetterLower));
    }
}
