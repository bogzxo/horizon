using System.Text;

using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Horizon.UI.Components;

// The commands of a shell as blocks and the keys that get about them, the host's half (the view's is
// TerminalView.Blocks). Jumping between commands, a command's output selected or copied, a command run again, the
// scrollback searched, every shell's history searched and the line put on the prompt, the line run in a new split, and
// typing into every pane of a tab at once.
public sealed partial class TerminalHost
{
    // The tabs that type into every pane of theirs at once
    private readonly HashSet<TerminalTab> broadcasting = [];

    /// <summary>Whether a tab types into every pane of it at once.</summary>
    public bool IsBroadcasting(TerminalTab tab) => broadcasting.Contains(tab);

    /// <summary>Typing into every pane of the open tab at once, on and off.</summary>
    public void ToggleBroadcast()
    {
        if (Panel.Current is not { } tab)
            return;

        if (!broadcasting.Remove(tab))
            broadcasting.Add(tab);
        Say(broadcasting.Contains(tab) ? $"typing into all {tab.Tree.Count} panes of the tab" : "typing into one pane again");
    }

    /// <summary>What a key sent to one pane of a broadcasting tab sent to the others too.</summary>
    private void Broadcast(TerminalView from, string bytes)
    {
        if (broadcasting.Count == 0 || Panel.TabOf(from) is not { } tab || !broadcasting.Contains(tab))
            return;

        foreach (TerminalView other in tab.Views)
        {
            if (other != from)
                other.Send(bytes);
        }
    }

    /// <summary>The line being typed in the focused shell run in a new split on its right, the line taken off where it was.</summary>
    public void RunInSplit()
    {
        if (Panel.FocusedView is not { } view)
            return;

        LineState state = StateOf(view);
        string line = state.Typed.ToString();
        if (line.Trim().Length == 0)
        {
            Say("nothing typed to run in a split");
            return;
        }

        view.Send(new string('\u007f', line.Length));
        state.Typed.Clear();

        if (Panel.Split(SplitOrientation.Horizontal) is { } split)
            split.SendWhenReady(line + "\r");
    }

    /// <summary>Every line any shell ever ran, the newest first, picked from and put on the focused shell's prompt.</summary>
    public void SearchHistory()
    {
        if (Panel.FocusedView is not { } view)
            return;

        List<string> lines = TerminalShellHistory.All(History.Lines);
        if (lines.Count == 0)
        {
            Say("no history yet");
            return;
        }

        options.Ui?.Choose("History", "Every line a shell has run, the newest first, a pick goes on the prompt", lines.Take(2000).ToArray(), index => PutOnPrompt(view, lines[index]));
    }

    /// <summary>A line put on a shell's prompt in place of what was typed there, as many backspaces as that and then the line.</summary>
    public void PutOnPrompt(TerminalView view, string line)
    {
        LineState state = StateOf(view);
        view.Send(new string('\u007f', state.Typed.Length) + line);
        state.Typed.Clear().Append(line);
        Panel.FocusPane(view);
    }

    /// <summary>The output of the last command that ran (or the block at the top of the view) selected.</summary>
    public void SelectOutput()
    {
        if (Panel.FocusedView is { } view && view.LastRun() is { } block && !view.SelectOutput(block))
            Say("the last command wrote nothing");
    }

    /// <summary>The output of the last command that ran copied.</summary>
    public void CopyOutput()
    {
        if (Panel.FocusedView is not { } view || view.LastRun() is not { } block)
            return;

        string output = view.Screen.OutputOf(block);
        UIClipboard.Copy(output);
        Say(output.Length == 0 ? "the last command wrote nothing" : $"copied what {view.Screen.CommandOf(block)} wrote");
    }

    /// <summary>The last command that ran, run again.</summary>
    public void Rerun()
    {
        if (Panel.FocusedView is not { } view || view.LastRun() is not { } block)
            return;

        string command = view.Screen.CommandOf(block);
        if (command.Length > 0)
            PutOnPrompt(view, command);
        view.Send("\r");
        Typed(view, "\r");
    }

    /// <summary>The commands of the focused terminal in a list, how each ended, a pick scrolls to it.</summary>
    public void ListCommands()
    {
        if (Panel.FocusedView is not { } view)
            return;

        var blocks = view.Blocks().Where(block => view.Screen.CommandOf(block).Length > 0).Reverse().ToArray();
        if (blocks.Length == 0)
        {
            Say(view.Screen.MarksSeen ? "no commands run yet" : "the shell says nothing of its commands (shell_integration in settings.hor)");
            return;
        }

        options.Ui?.Choose("Commands", "What ran in this terminal, the newest first, a pick scrolls to it",
            [.. blocks.Select(block => $"{(block.Exit is null ? "$(circle-outline)" : block.Failed ? "$(error)" : "$(check)")} {view.Screen.CommandOf(block)}{(block.Failed ? $"   (ended {block.Exit})" : string.Empty)}")],
            index =>
            {
                view.ScrollToRow(blocks[index].PromptRow);
                view.SelectBlock(blocks[index]);
            });
    }

    /// <summary>The scrollback of the focused terminal searched, asked what for when nothing is given.</summary>
    public void Find(string? text = null)
    {
        if (Panel.FocusedView is not { } view)
            return;

        if (text is null)
        {
            options.Ui?.AskText("Find", "What to look for in all of this terminal, F3 and Shift+F3 step through it", view.Finding, Find);
            return;
        }

        int found = view.Find(text);
        Say(text.Length == 0 ? "nothing looked for" : found == 0 ? $"{text} isn't there" : $"{found} of {text}, F3 for the next");
    }

    private (string, IRuntimeValue)[] BlocksApi() =>
    [
        ("jump_command", Natives.Function("jump_command", args => Values.Bool(Panel.FocusedView?.JumpToCommand(args.Number(0, -1) < 0 ? -1 : 1) == true))),
        ("select_output", Natives.Action("select_output", _ => SelectOutput())),
        ("copy_output", Natives.Action("copy_output", _ => CopyOutput())),
        ("rerun", Natives.Action("rerun", _ => Rerun())),
        ("list_commands", Natives.Action("list_commands", _ => ListCommands())),
        ("blocks", Natives.Function("blocks", _ =>
        {
            var listed = new ListValue();
            if (Panel.FocusedView is { } view)
            {
                foreach (TerminalBlock block in view.Blocks())
                {
                    listed.Items.Add(Natives.Object(
                        ("command", new StringValue(view.Screen.CommandOf(block))),
                        ("exit", block.Exit is { } code ? new NumberValue(code) : Values.Null),
                        ("row", new NumberValue(block.PromptRow)),
                        ("guessed", Values.Bool(block.Guessed))));
                }
            }

            return listed;
        })),
        ("output_of", Natives.Function("output_of", args =>
        {
            if (Panel.FocusedView is not { } view)
                return new StringValue(string.Empty);
            var blocks = view.Blocks();
            int at = (int)args.Number(0, blocks.Count - 2);
            return new StringValue(at >= 0 && at < blocks.Count ? view.Screen.OutputOf(blocks[at]) : string.Empty);
        })),
        ("scroll", Natives.Action("scroll", args =>
        {
            if (args[0] is StringValue { Value: "top" or "bottom" } end)
                Panel.FocusedView?.ScrollToEnd(end.Value == "top");
            else
                Panel.FocusedView?.Scroll((int)args.Number(0));
        })),
        ("find", Natives.Action("find", args => Find(args.Has(0) ? args.Text(0) : null))),
        ("find_next", Natives.Action("find_next", args => Panel.FocusedView?.FindNext(args.Number(0, 1) < 0 ? -1 : 1))),
        ("find_clear", Natives.Action("find_clear", _ => Panel.FocusedView?.ClearFind())),
        ("history_search", Natives.Action("history_search", _ => SearchHistory())),
        ("put_on_prompt", Natives.Action("put_on_prompt", args =>
        {
            if (Panel.FocusedView is { } view) PutOnPrompt(view, args.Text(0));
        })),
        ("run_in_split", Natives.Action("run_in_split", _ => RunInSplit())),
        ("broadcast", Natives.Function("broadcast", _ =>
        {
            ToggleBroadcast();
            return Values.Bool(Panel.Current is { } tab && broadcasting.Contains(tab));
        }))
    ];

    /// <summary>A direction as a plugin says it, null for one it isn't.</summary>
    private static SplitDirection? DirectionOf(string name) => name switch
    {
        "left" => SplitDirection.Left,
        "right" => SplitDirection.Right,
        "up" => SplitDirection.Up,
        "down" => SplitDirection.Down,
        _ => null
    };

    /// <summary>The functions of the blocks part of the object, described.</summary>
    private static readonly TerminalApiFunction[] BlocksFunctions =
    [
        new("jump_command", "direction", "The command before (-1) or after (1) the top of the view scrolled to, false when there is none that way."),
        new("select_output", "", "Selects what the last command wrote."),
        new("copy_output", "", "Copies what the last command wrote."),
        new("rerun", "", "Runs the last command again."),
        new("list_commands", "", "What ran in the focused terminal in a list, a pick scrolls to it."),
        new("blocks", "", "The commands of the focused terminal, { command, exit, row, guessed } each, the oldest first."),
        new("output_of", "index?", "What a command wrote, by its place in blocks(), the last that ran when none is given."),
        new("scroll", "rows or \"top\" or \"bottom\"", "Scrolls the focused terminal, up by rows more than none."),
        new("find", "text?", "Looks for a text in all of the focused terminal, asked for when none is given, every match lit."),
        new("find_next", "direction?", "The next match (1) or the one before (-1)."),
        new("find_clear", "", "Lets the matches of a find go."),
        new("history_search", "", "Every line every shell ran in a list, a pick goes on the prompt."),
        new("put_on_prompt", "line", "Puts a line on the focused shell's prompt in place of what is typed there."),
        new("run_in_split", "", "Runs the line being typed in a new split on the right."),
        new("broadcast", "", "Typing into every pane of the tab at once, on and off, true when it is on.")
    ];
}
