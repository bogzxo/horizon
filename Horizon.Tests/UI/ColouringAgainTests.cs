using Horizon.UI.Scripting;

namespace Horizon.Tests.UI;

/// <summary>
/// Colouring a file again after an edit (ICodeHighlighter.ReadAgain), only the lines from the edit on read again, has to
/// come out the colours a whole new colouring would. Every kind of edit, a line typed into, lines added and taken out, a
/// comment opened that runs on to the end, brackets that change the depth of the rest, in C# and in HIDL.
/// </summary>
public class ColouringAgainTests
{
    private const string CSHARP = """
        using System;

        namespace Big;

        public sealed class Thing
        {
            private readonly int[] numbers = [1, 2, 3];

            /// <summary>Adds up what it has.</summary>
            public int Sum(int times)
            {
                int total = 0;
                for (int i = 0; i < times; i++)
                {
                    foreach (int number in numbers)
                        total += number * i;
                }

                string said = "a text { with brackets }";
                return total; // the end of it
            }
        }
        """;

    private const string HIDL = """
        // a layout
        let things = {
            thing_1: { name: "thing 1", size: 3, tags: ["a", "b"] },
            thing_2: { name: "thing 2", size: 4, tags: ["c"] },
        };

        func add(a, b) {
            let total = a + b;
            print(total);
            return total;
        }

        let colour = vec4(1.0, 0.5, 0.25, 1.0);
        """;

    // What every character of every line is coloured as, which is what has to be the same (how a line is cut into spans
    // doesn't show)
    private static List<string> Colours(List<SyntaxSpan>[] spans, string[] lines)
    {
        var all = new List<string>();
        for (int line = 0; line < lines.Length; line++)
        {
            var kinds = new SyntaxKind[lines[line].Length];
            foreach (SyntaxSpan span in spans[line])
            {
                for (int i = span.Start; i < span.Start + span.Length && i < kinds.Length; i++)
                    kinds[i] = span.Kind;
            }

            all.Add(string.Join(",", kinds));
        }

        return all;
    }

    private static (List<SyntaxSpan>[] Spans, CodeLineStart[] Starts) Whole(ICodeHighlighter highlighter, string[] lines)
    {
        var starts = new CodeLineStart[lines.Length];
        return (highlighter.Read(string.Join('\n', lines), lines.Length, starts), starts);
    }

    private static void SameAsWhole(ICodeHighlighter highlighter, string text, Func<List<string>, List<string>> edit)
    {
        string[] before = text.Replace("\r\n", "\n").Split('\n');
        var (spans, starts) = Whole(highlighter, before);
        var last = new CodeColouring(before, spans, starts);

        // The same strings for the lines that weren't touched, the way the document keeps them
        string[] after = [.. edit([.. before])];
        var againStarts = new CodeLineStart[after.Length];
        List<SyntaxSpan>[] again = highlighter.ReadAgain(string.Join('\n', after), after, againStarts, last);
        var (whole, wholeStarts) = Whole(highlighter, after);

        Assert.Equal(Colours(whole, after), Colours(again, after));
        Assert.Equal(wholeStarts.Select(start => start.Depth), againStarts.Select(start => start.Depth));
    }

    public static TheoryData<string> Languages => [".cs", ".hor"];

    private static (ICodeHighlighter Highlighter, string Text) Language(string extension) =>
        (CodeLanguages.ForExtension(extension)!, extension == ".cs" ? CSHARP : HIDL);

    [Theory]
    [MemberData(nameof(Languages))]
    public void A_line_typed_into(string extension)
    {
        var (highlighter, text) = Language(extension);
        SameAsWhole(highlighter, text, lines => { lines[7] += " let x = 1"; return lines; });
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Lines_added_and_taken_out(string extension)
    {
        var (highlighter, text) = Language(extension);
        SameAsWhole(highlighter, text, lines => { lines.Insert(3, "int added = 4;"); lines.Insert(3, ""); return lines; });
        SameAsWhole(highlighter, text, lines => { lines.RemoveRange(2, 3); return lines; });
        SameAsWhole(highlighter, text, lines => { lines.Add("let more = 5;"); return lines; });
        SameAsWhole(highlighter, text, lines => { lines.RemoveAt(lines.Count - 1); return lines; });
        SameAsWhole(highlighter, text, lines => { lines.Insert(0, "// first"); return lines; });
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void A_comment_opened_runs_on_and_closed_again_stops(string extension)
    {
        var (highlighter, text) = Language(extension);
        SameAsWhole(highlighter, text, lines => { lines[4] += " /* open"; return lines; });
        SameAsWhole(highlighter, text, lines => { lines[4] += " /* open"; lines[6] += " closed */"; return lines; });
        SameAsWhole(highlighter, text, lines => { lines[5] += " \"a text not ended"; return lines; });
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void A_bracket_changes_the_depth_of_everything_after(string extension)
    {
        var (highlighter, text) = Language(extension);
        SameAsWhole(highlighter, text, lines => { lines[6] += " {"; return lines; });
        SameAsWhole(highlighter, text, lines => { lines[6] += " ( ["; return lines; });
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Nothing_changed_and_everything_changed(string extension)
    {
        var (highlighter, text) = Language(extension);
        SameAsWhole(highlighter, text, lines => lines);
        SameAsWhole(highlighter, text, lines => ["something else entirely", "and more {"]);
        SameAsWhole(highlighter, text, lines => [""]);
    }

    [Fact]
    public void A_name_made_a_parameter_further_up_is_coloured_as_one_further_down()
    {
        // HIDL's names take their kind from what's round them, which reaches over lines the grammar left as they were
        SameAsWhole(CodeLanguages.ForExtension(".hor")!, HIDL, lines => { lines[6] = "func add(a, b, print) {"; return lines; });
    }
}
