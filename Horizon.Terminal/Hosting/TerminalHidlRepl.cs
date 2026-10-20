using System.Text;

using Horizon.HIDL;
using Horizon.HIDL.Lexing;
using Horizon.HIDL.Runtime;

namespace Horizon.UI.Components;

/// <summary>
/// HIDL typed in and run, as a program in a terminal (<see cref="TerminalLineEditor"/>), the line coloured as it is
/// typed by the language's own lexer in the terminal's sixteen colours, the names the runtime knows completed after the
/// caret, what a line comes to written under it and what went wrong in red. Run in a runtime of its own unless it is
/// handed one, which is how Hex runs it in the values of the file on screen (<see cref="RuntimeFor"/> asked every line,
/// so the file can change under it). A line that ends in an open bracket waits for the rest of it on the next line.
/// </summary>
public class TerminalHidlRepl : TerminalLineEditor
{
    private readonly HIDLRuntime own = new();
    private readonly StringBuilder pending = new();

    /// <summary>The runtime a line is run in, its own when this is null or hands back null.</summary>
    public Func<HIDLRuntime?>? RuntimeFor { get; set; }

    /// <summary>What the prompt says before its arrow, the file it runs in for Hex, "hidl" on its own.</summary>
    public Func<string>? Where { get; set; }

    public override string Name { get; set; } = "HIDL";

    public HIDLRuntime Runtime => RuntimeFor?.Invoke() ?? own;

    /// <param name="history">Where the lines are kept from one run to the next, null for only this one.</param>
    public TerminalHidlRepl(string? history = null) : base(history)
    {
        Greeting = "\u001b[2mHIDL, the language of Horizon. A line is run as it is given, its value written under it. help() lists what there is.\u001b[0m";
        OnLine = Run;
        Complete = Completion;
        Colour = Coloured;
        Prompt = PromptText();
    }

    public override void Attach(TerminalView view)
    {
        own.Output = text => Write(text + "\n");
        Prompt = PromptText();
        base.Attach(view);
    }

    private string PromptText() => pending.Length > 0 ? "\u001b[2m... \u001b[0m" : $"\u001b[36m{Where?.Invoke() ?? "hidl"}\u001b[0m \u001b[35m>\u001b[0m ";

    private void Run(string given)
    {
        if (given.Trim() is "exit" or "exit()" or "quit")
        {
            Exit();
            return;
        }

        pending.Append(given).Append('\n');
        string program = pending.ToString();

        // Brackets still open, the rest of it is on the lines to come
        if (Depth(program) > 0)
        {
            Prompt = PromptText();
            return;
        }

        pending.Clear();
        Prompt = PromptText();
        if (program.Trim().Length == 0)
            return;

        HIDLRuntime runtime = Runtime;
        Action<string>? was = runtime.Output;
        runtime.Output = text => Write(text + "\n");
        try
        {
            var (success, result) = runtime.Evaluate(program);
            if (!success)
                Write($"\u001b[31m{result}\u001b[0m\n");
            else if (result.Length > 0 && result != "null")
                Write(Coloured(result) + "\n");
        }
        finally
        {
            runtime.Output = was;
        }
    }

    /// <summary>How many brackets are open at the end of a text, strings and comments left out.</summary>
    public static int Depth(string text)
    {
        try
        {
            int depth = 0;
            foreach (Token token in Lexer.Tokenize(text, lenient: true))
            {
                depth += token.Type switch
                {
                    TokenType.OpenBrace or TokenType.OpenBracket or TokenType.OpenParenthesis => 1,
                    TokenType.CloseBrace or TokenType.CloseBracket or TokenType.CloseParenthesis => -1,
                    _ => 0
                };
            }

            return depth;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>The rest of the name being typed, out of the names the runtime knows, the shortest first.</summary>
    private string? Completion(string text)
    {
        int start = text.Length;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
            start--;
        if (text.Length - start < 1)
            return null;

        string word = text[start..];

        // After a dot it is a member of what is before it, when that reads as something
        if (start > 0 && text[start - 1] == '.')
        {
            int from = start - 1;
            while (from > 0 && (char.IsLetterOrDigit(text[from - 1]) || text[from - 1] is '_' or '.'))
                from--;
            string of = text[from..(start - 1)];
            try
            {
                if (Runtime.Run(of) is ObjectValue objectValue)
                    return Best(objectValue.Properties.Keys, word);
            }
            catch (Exception)
            {
                return null;
            }

            return null;
        }

        HIDLRuntime runtime = Runtime;
        return Best(runtime.UserScope.Variables.Keys.Concat(runtime.GlobalScope.Variables.Keys).Concat(Words), word);
    }

    // The words of the language, which are no names a runtime holds
    private static readonly string[] Words = ["let", "const", "if", "else", "while", "do", "for", "in", "return", "break", "continue", "func", "delete", "true", "false", "null"];

    private static string? Best(IEnumerable<string> names, string word)
    {
        string? best = null;
        foreach (string name in names)
        {
            if (name.Length > word.Length && name.StartsWith(word, StringComparison.Ordinal) && (best is null || name.Length < best.Length))
                best = name;
        }

        return best?[word.Length..];
    }

    /// <summary>A text coloured by the language's lexer in the terminal's colours, the keywords magenta, texts green and the rest.</summary>
    public static string Coloured(string text)
    {
        Token[] tokens;
        try
        {
            tokens = Lexer.Tokenize(text, lenient: true);
        }
        catch (Exception)
        {
            return text;
        }

        var coloured = new StringBuilder();
        int at = 0;
        foreach (Token token in tokens)
        {
            if (token.Type == TokenType.EndOfFile || token.Index < at || token.Index + token.Length > text.Length)
                continue;

            coloured.Append(text, at, token.Index - at);
            string? colour = token.Type switch
            {
                TokenType.Let or TokenType.Const or TokenType.If or TokenType.Else or TokenType.While or TokenType.For or TokenType.In
                    or TokenType.Return or TokenType.Function or TokenType.Break or TokenType.Continue or TokenType.Do or TokenType.Delete => "35",
                TokenType.Number => "33",
                TokenType.TextLiteral or TokenType.Quote => "32",
                TokenType.Boolean or TokenType.Null => "34",
                TokenType.Comment => "90",
                TokenType.Directive => "95",
                TokenType.Identifier => token.Index + token.Length < text.Length && text[token.Index + token.Length] == '(' ? "36" : null,
                _ => null
            };

            string piece = text.Substring(token.Index, token.Length);
            if (colour is null)
                coloured.Append(piece);
            else
                coloured.Append("\u001b[").Append(colour).Append('m').Append(piece).Append("\u001b[39m");
            at = token.Index + token.Length;
        }

        coloured.Append(text, at, text.Length - at);
        return coloured.ToString();
    }
}
