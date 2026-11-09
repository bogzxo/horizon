using TextMateSharp.Internal.Types;

namespace Horizon.UI.Scripting;

// What's put right in a grammar TextMateSharp ships before the registry gets it. Its XML grammar names the marks a
// comment starts and ends with and not the comment itself, so whatever is written between <!-- and --> came out as
// plain text, not coloured as a comment and with no TODO lit in it. The rule is given the name VS Code's has.
public static partial class CodeLanguages
{
    private const string XML_SCOPE = "text.xml";

    // Helper method to mend a grammar as it's read, the same grammar back for one with nothing wrong with it
    private static IRawGrammar? Mended(string scopeName, IRawGrammar? grammar)
    {
        if (grammar is null || scopeName != XML_SCOPE)
            return grammar;

        if (grammar.GetRepository()?.GetProp("comments")?.GetPatterns() is not { } comments)
            return grammar;

        foreach (IRawRule rule in comments)
        {
            if (rule.GetName() is null && rule.GetBegin() is "<!--" or "<%--")
                rule.SetName("comment.block.xml");
        }

        return grammar;
    }
}
