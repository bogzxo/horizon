using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>
/// The line typed at a prompt read off the screen, which is what the history keeps. The keys that made it lost an arrow
/// back into the middle of it, and a line run after one came back out of the history with its words out of order.
/// </summary>
public class TerminalTypedLineTests
{
    private const string PROMPT = "\u001b]133;A\u0007$ \u001b]133;B\u0007";

    [Fact]
    public void A_line_edited_in_the_middle_is_read_whole_with_the_caret_left_in_it()
    {
        var view = new TerminalView();

        // "git log --oneline" typed, the caret taken back ten and " -5" put in, the shell drawing the rest again after it
        view.Feed(PROMPT + "git log --oneline\u001b[10D -5 --oneline\u001b[10D");

        Assert.Equal("git log -5 --oneline", view.TypedLine());
    }

    [Fact]
    public void A_line_that_wrapped_is_read_across_its_rows()
    {
        var view = new TerminalView();
        string longLine = "echo " + new string('x', 100);
        view.Feed(PROMPT + longLine);

        Assert.Equal(longLine, view.TypedLine());
    }

    [Fact]
    public void A_prediction_after_the_cursor_is_not_the_line()
    {
        var view = new TerminalView();

        // PSReadLine's suggestion, dim after the caret, which hasn't been typed
        view.Feed(PROMPT + "git\u001b[2m status\u001b[0m\u001b[7D");

        Assert.Equal("git", view.TypedLine());
    }

    [Fact]
    public void A_cmd_prompt_with_no_marks_is_found_by_its_look()
    {
        var view = new TerminalView();
        view.Feed("C:\\work>dir /b");

        Assert.Equal("dir /b", view.TypedLine());
    }

    [Fact]
    public void Nothing_is_typed_where_a_program_has_the_screen()
    {
        var view = new TerminalView();
        view.Feed(PROMPT + "micro\r\n\u001b]133;C\u0007\u001b[?1049hsome file");

        Assert.Null(view.TypedLine());
    }
}
