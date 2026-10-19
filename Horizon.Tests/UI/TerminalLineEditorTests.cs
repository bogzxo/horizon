using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>
/// The line editor and the HIDL REPL on it, fed the bytes keys send and read back off the screen of a view, which
/// needs no window for that.
/// </summary>
public class TerminalLineEditorTests
{
    private static (TerminalView View, TerminalLineEditor Editor) Editor(Action<TerminalLineEditor>? setUp = null)
    {
        var view = new TerminalView();
        var editor = new TerminalLineEditor { Prompt = "$ " };
        setUp?.Invoke(editor);
        view.Start(editor);
        return (view, editor);
    }

    private static string CursorRow(TerminalView view) => view.Screen.Grid[view.Screen.CursorY].Text().TrimEnd();

    [Fact]
    public void What_is_typed_is_on_the_line_after_the_prompt()
    {
        var (view, editor) = Editor();
        editor.Input("hello");
        Assert.Equal("$ hello", CursorRow(view));
        Assert.Equal(7, view.Screen.CursorX);
    }

    [Fact]
    public void The_caret_moves_and_the_line_is_edited_where_it_is()
    {
        var (view, editor) = Editor();
        editor.Input("helo");
        editor.Input("\u001b[D");
        editor.Input("l");
        Assert.Equal("hello", editor.Line);

        editor.Input("\u001b[H");
        editor.Input("\u001b[3~");
        Assert.Equal("ello", editor.Line);

        editor.Input("\u001b[F\u007f\u007f");
        Assert.Equal("el", editor.Line);
        Assert.Equal("$ el", CursorRow(view));
    }

    [Fact]
    public void Control_and_the_arrows_go_by_words_and_control_w_takes_one_off()
    {
        var (_, editor) = Editor();
        editor.Input("git commit --amend");
        editor.Input("\u001b[1;5D");
        Assert.Equal(11, editor.Caret);
        editor.Input("\u001b[1;5D");
        Assert.Equal(4, editor.Caret);
        editor.Input("\u001b[1;5C");
        Assert.Equal(10, editor.Caret);
        editor.Input("\u0017");
        Assert.Equal("git  --amend", editor.Line);
    }

    [Fact]
    public void Return_hands_the_line_over_and_the_prompt_comes_again()
    {
        var lines = new List<string>();
        var (view, editor) = Editor(editor => editor.OnLine = line =>
        {
            lines.Add(line);
            editor.Write($"you said {line}\n");
        });

        editor.Input("ping\r");
        Assert.Equal(["ping"], lines);
        Assert.Contains("$ ping", view.Screen.Text());
        Assert.Contains("you said ping", view.Screen.Text());
        Assert.Equal("$", CursorRow(view));
        Assert.Equal(string.Empty, editor.Line);
    }

    [Fact]
    public void Up_and_down_go_through_what_was_given()
    {
        var (_, editor) = Editor();
        editor.Input("one\rtwo\r");
        editor.Input("thr");
        editor.Input("\u001b[A");
        Assert.Equal("two", editor.Line);
        editor.Input("\u001b[A");
        Assert.Equal("one", editor.Line);
        editor.Input("\u001b[B\u001b[B");
        Assert.Equal("thr", editor.Line);
    }

    [Fact]
    public void A_completion_is_shown_dim_and_tab_takes_it()
    {
        var (view, editor) = Editor(editor => editor.Complete = text => "print".StartsWith(text) && text.Length < 5 ? "print"[text.Length..] : null);
        editor.Input("pr");
        Assert.Equal("$ print", CursorRow(view));
        Assert.Equal(4, view.Screen.CursorX);
        Assert.True(view.Screen[4, view.Screen.CursorY].Style.Has(TerminalAttributes.Dim));

        editor.Input("\t");
        Assert.Equal("print", editor.Line);
        Assert.Equal(7, view.Screen.CursorX);
    }

    [Fact]
    public void Control_c_gives_the_line_up_and_control_u_clears_it()
    {
        var (view, editor) = Editor();
        editor.Input("rm -rf");
        editor.Input("\u0003");
        Assert.Equal(string.Empty, editor.Line);
        Assert.Contains("rm -rf^C", view.Screen.Text());

        editor.Input("abc\u0015");
        Assert.Equal(string.Empty, editor.Line);
    }

    [Fact]
    public void A_line_wider_than_the_terminal_shows_the_end_with_the_caret()
    {
        var (view, editor) = Editor();
        editor.Input(new string('x', 100) + "END");
        Assert.EndsWith("END", CursorRow(view));
        Assert.True(CursorRow(view).Length <= view.Columns);
    }

    [Fact]
    public void The_repl_runs_hidl_and_writes_its_value()
    {
        var view = new TerminalView();
        var repl = new TerminalHidlRepl();
        view.Start(repl);

        repl.Input("let x = 20 + 1\r");
        repl.Input("x * 2\r");
        Assert.Contains("42", view.Screen.Text());

        repl.Input("nonsense(\r");
        Assert.Contains("...", CursorRow(view));
        repl.Input(")\r");
        Assert.Contains("nonsense", view.Screen.Text());
    }

    [Fact]
    public void The_repl_completes_the_names_it_knows_and_exit_ends_it()
    {
        var view = new TerminalView();
        var repl = new TerminalHidlRepl();
        int? ended = null;
        view.OnExited = code => ended = code;
        view.Start(repl);

        repl.Input("let fighter_speed = 3\r");
        repl.Input("figh");
        Assert.Contains("fighter_speed", CursorRow(view));
        repl.Input("\t\r");
        Assert.Contains("3", view.Screen.Text());

        repl.Input("exit\r");
        Assert.False(repl.IsRunning);
        Assert.False(view.IsRunning);
    }

    [Fact]
    public void The_repl_colours_hidl_with_the_terminal_colours()
    {
        string coloured = TerminalHidlRepl.Coloured("let a = \"text\"; // note");
        Assert.Contains("\u001b[35mlet", coloured);
        Assert.Contains("\u001b[32m", coloured);
        Assert.Contains("\u001b[90m// note", coloured);
        Assert.Equal(0, TerminalHidlRepl.Depth("{ a: [1, 2] }"));
        Assert.Equal(2, TerminalHidlRepl.Depth("{ a: [1, "));
        Assert.Equal(0, TerminalHidlRepl.Depth("\"{ not a brace\""));
    }

    [Fact]
    public void Escape_sequences_take_no_room_in_the_prompt()
    {
        Assert.Equal(7, TerminalLineEditor.VisibleLength("\u001b[36mhidl\u001b[0m > "));
    }
}
