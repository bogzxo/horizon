using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Horizon.Tests.UI;

/// <summary>
/// TODO and FIXME lit in the comments of every language Hex colours, line comments and block comments over several
/// lines, and never in a string, a name or a word with TODO in it. Each sample marks what should be lit with « and »,
/// which are taken out before the code is coloured, so the columns are exact and the samples still read as code.
/// </summary>
public class TodoCommentsTests
{
    public static TheoryData<string, string> Samples => new()
    {
        { ".cs", """
            // «TODO» a line comment
            int TODO = 1; // «FIXME» after the code
            string said = "TODO not in a string";
            /* the first line
               «TODO» the second line
               «FIXME» and the third */
            /// <summary>«TODO» in a doc comment</summary>
            // TODOS NOTODO MY_TODO TODO_LIST aren't words of it
            string t = $"interpolated TODO {TODO}";
            string v = @"verbatim
            TODO still the string";
            """ },

        { ".hor", """
            // «TODO» a line comment
            let TODO = 1; // «FIXME» after the code
            let said = "TODO not in a string";
            /* the first line
               «TODO» the second line
               «FIXME» and the third */
            let t = `template TODO {TODO}`;
            // TODOS NOTODO aren't words of it
            """ },

        { ".js", """
            // «TODO» a line comment
            const TODO = "TODO not in a string"; /* «FIXME» an inline block */
            /*
             * «TODO» the second line
             * «FIXME» and the third
             */
            const t = `template TODO
            still the TODO template`;
            /** «TODO» in a doc comment */
            const re = /TODO/;
            """ },

        { ".ts", """
            // «TODO» a line comment
            let TODO: string = "TODO not in a string"; // «FIXME» after the code
            /* the first line
               «TODO» the second line
               «FIXME» and the third */
            type TODOS = 'TODO';
            """ },

        { ".py", """"
            # «TODO» a line comment
            TODO = "TODO not in a string"  # «FIXME» after the code
            def f():
                '''TODO a docstring is a string'''
                return 'TODO'
            s = """a long string
            TODO still the string"""
            """" },

        { ".sh", """
            # «TODO» a line comment
            echo "TODO not in a string" # «FIXME» after the code
            TODO=1
            echo TODO 'TODO'
            """ },

        { ".ps1", """
            # «TODO» a line comment
            $TODO = "TODO not in a string" # «FIXME» after the code
            <#
              «TODO» the second line
              «FIXME» and the third
            #>
            Write-Host 'TODO single quoted'
            """ },

        { ".sql", """
            -- «TODO» a line comment
            SELECT 'TODO not in a string' AS TODO; -- «FIXME» after the code
            /* the first line
               «TODO» the second line
               «FIXME» and the third */
            """ },

        { ".yaml", """
            # «TODO» a line comment
            todo: "TODO not in a string" # «FIXME» after the code
            TODO: a plain TODO value
            text: |
              TODO in a block of text
            """ },

        { ".toml", """
            # «TODO» a line comment
            TODO = "TODO not in a string" # «FIXME» after the code
            s = '''
            TODO in a long string
            '''
            list = [ "TODO", # «TODO» in an array
              'TODO' ]
            """ },

        { ".html", """
            <!-- «TODO» a comment on one line -->
            <p title="TODO not in a string">TODO in the text</p>
            <!--
              «TODO» the second line
              «FIXME» and the third
            -->
            <script>
            // «TODO» in the script
            var s = "TODO";
            </script>
            <style>
            /* «FIXME» in the style */
            </style>
            """ },

        { ".xml", """
            <?xml version="1.0"?>
            <!-- «TODO» a comment on one line -->
            <root a="TODO not in a string">
              <TODO>TODO in the text</TODO>
              <!--
                «FIXME» the second line
                «TODO» and the third -->
              <![CDATA[TODO in the data]]>
            </root>
            """ },

        { ".css", """
            /* «TODO» a comment on one line */
            .TODO { content: "TODO not in a string"; } /* «FIXME» after the code */
            /*
              «TODO» the second line
              «FIXME» and the third
            */
            """ },

        { ".md", """
            # A heading with TODO in it
            Some TODO in the text.
            <!-- «TODO» a comment on one line -->
            <!--
            «FIXME» the second line
            «TODO» and the third
            -->
            `TODO in code`
            ```cs
            // «TODO» in a block of C#
            var s = "TODO";
            ```
            """ },

        { ".go", """
            // «TODO» a line comment
            var TODO = "TODO not in a string" // «FIXME» after the code
            /* the first line
               «TODO» the second line
               «FIXME» and the third */
            var r = `raw
            TODO still raw`
            """ },

        { ".rs", """
            // «TODO» a line comment
            let todo = "TODO not in a string"; // «FIXME» after the code
            /* the first line
               «TODO» the second line
               «FIXME» and the third */
            /// «TODO» in a doc comment
            let TODO = r"TODO raw";
            /* outer /* inner */ «TODO» still outer */
            """ },

        { ".c", """
            // «TODO» a line comment
            int TODO = 1; /* «FIXME» an inline block */
            /* the first line
             * «TODO» the second line
             * «FIXME» and the third
             */
            char *s = "TODO not in a string";
            #define TODO_X 1 // «TODO» after a define
            """ },

        { ".cpp", """
            // «TODO» a line comment
            auto TODO = 1; // «FIXME» after the code
            /* the first line
               «TODO» the second line
               «FIXME» and the third */
            auto s = R"(TODO raw
            TODO still raw)";
            std::string t = "TODO not in a string";
            """ },

        { ".java", """
            // «TODO» a line comment
            int TODO = 1; // «FIXME» after the code
            /* the first line
               «TODO» the second line
               «FIXME» and the third */
            /**
             * «TODO» in a doc comment
             */
            String s = "TODO not in a string";
            """ },

        { ".d", """
            // «TODO» a line comment
            int TODO = 1; // «FIXME» after the code
            /* the first line
               «TODO» the second line
               «FIXME» and the third */
            /+ nested /+ inner +/
               «TODO» still nested +/
            string s = "TODO not in a string";
            string w = `TODO wysiwyg`;
            """ },

        { ".txt", """
            «TODO» everything is a comment in plain text, «FIXME» too
            TODOS NOTODO aren't words of it
            """ }
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Todos_are_lit_in_comments_and_nowhere_else(string extension, string sample)
    {
        var (code, wanted) = Unmark(sample);
        string[] lines = code.Split('\n');

        // Plain text has no grammar, and no colours is how the editor asks for it
        ICodeHighlighter? highlighter = CodeLanguages.ForExtension(extension);
        if (extension != ".txt")
            Assert.NotNull(highlighter);
        List<SyntaxSpan>[]? spans = highlighter?.Read(code, lines.Length);

        var lit = new List<(int Line, int From, int To, bool Urgent)>();
        for (int i = 0; i < lines.Length; i++)
            lit.AddRange(CodeEditor.TodosIn(lines[i], spans?[i]).Select(todo => (i, todo.From, todo.To, todo.Urgent)));

        Assert.Equal(wanted.Order(), lit.Order());
    }

    [Fact]
    public void A_line_with_no_colours_is_plain_text()
    {
        Assert.Equal(new (int, int, bool)[] { (0, 4, false), (9, 14, true) }, CodeEditor.TodosIn("TODO and FIXME", null));
        Assert.Empty(CodeEditor.TodosIn("TODOS NOTODO MY_TODO", null));
    }

    // Helper method to take the marks out of a sample, the code left and where each marked word is
    private static (string Code, List<(int Line, int From, int To, bool Urgent)> Wanted) Unmark(string sample)
    {
        var wanted = new List<(int, int, int, bool)>();
        var lines = sample.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            for (int open = line.IndexOf('«'); open >= 0; open = line.IndexOf('«'))
            {
                int close = line.IndexOf('»', open);
                string word = line[(open + 1)..close];
                wanted.Add((i, open, open + word.Length, word == "FIXME"));
                line = line[..open] + word + line[(close + 1)..];
            }

            lines[i] = line;
        }

        return (string.Join('\n', lines), wanted);
    }
}
