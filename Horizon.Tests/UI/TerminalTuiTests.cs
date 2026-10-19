using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>Programs with a screen of their own written in HIDL, the runner, its keys, the widgets and the programs that come with it.</summary>
public class TerminalTuiTests
{
    private static string Shipped(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", "terminal", "tui", name);

    private static (TerminalView View, TerminalTui Program) Run(string file, TerminalTuiServices? services = null)
    {
        var view = new TerminalView();
        var program = new TerminalTui(file, services);
        view.Start(program);
        return (view, program);
    }

    [Theory]
    [InlineData("\u001b[A", "up", false)]
    [InlineData("\u001b[1;5B", "down", true)]
    [InlineData("\u001b[5~", "pgup", false)]
    [InlineData("\r", "enter", false)]
    [InlineData("\u007f", "backspace", false)]
    [InlineData("\u0015", "u", true)]
    [InlineData("\u001b", "escape", false)]
    public void Keys_are_read_back_out_of_what_a_terminal_sends(string bytes, string name, bool ctrl)
    {
        var key = Assert.Single(TerminalTuiKeys.Parse(bytes));
        Assert.Equal(name, key.Name);
        Assert.Equal(ctrl, key.Ctrl);
    }

    [Fact]
    public void Letters_alt_and_a_paste_are_keys_too()
    {
        var keys = TerminalTuiKeys.Parse("aB\u001bh\u001b[200~two words\u001b[201~");
        Assert.Equal(["a", "b", "h", "paste"], keys.Select(key => key.Name));
        Assert.True(keys[1].Shift);
        Assert.True(keys[2].Alt);
        Assert.Equal("two words", keys[3].Text);
    }

    [Fact]
    public void A_program_draws_takes_keys_and_leaves()
    {
        string file = Path.Combine(Path.GetTempPath(), $"tui-{Guid.NewGuid():N}.hor");
        File.WriteAllText(file, """
            let program = { name: "Counter" };
            let count = 0;
            func on_key(tui, key) {
                if (key.name == "q") { tui.exit(3); return; }
                count += 1;
            }
            func draw(tui) {
                tui.clear();
                tui.write(2, 1, `pressed {count}`, "bold red");
                tui.box(0, 3, 12, 3, "box");
            }
            """);
        try
        {
            var (view, program) = Run(file);
            Assert.Equal("Counter", program.Name);
            Assert.True(view.Screen.AlternateScreen);
            Assert.Contains("pressed 0", program.ScreenText());
            Assert.Contains("pressed 0", view.Screen.RowAt(view.Screen.Scrollback.Count + 1).Text());
            Assert.Contains("╭ box ", view.Screen.RowAt(view.Screen.Scrollback.Count + 3).Text());

            program.Input("xx");
            Assert.Contains("pressed 2", view.Screen.RowAt(view.Screen.Scrollback.Count + 1).Text());

            program.Input("q");
            Assert.False(program.IsRunning);
            Assert.False(view.Screen.AlternateScreen);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_program_that_goes_wrong_says_so_and_leaves_on_a_key()
    {
        string file = Path.Combine(Path.GetTempPath(), $"tui-{Guid.NewGuid():N}.hor");
        File.WriteAllText(file, "func draw(tui) { tui.write(0, 0, nonsense.here); }");
        try
        {
            var (_, program) = Run(file);
            Assert.NotNull(program.Failed);
            Assert.Contains("went wrong", program.ScreenText());
            program.Input("x");
            Assert.False(program.IsRunning);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void The_file_picker_narrows_goes_into_folders_and_hands_a_path_to_the_shell()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"tui-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "alpha.txt"), "a");
        File.WriteAllText(Path.Combine(folder, "beta.md"), "b");
        File.WriteAllText(Path.Combine(folder, "sub", "inside.cs"), "c");
        string? typed = null, opened = null;
        try
        {
            var (_, program) = Run(Shipped("files.hor"), new TerminalTuiServices { Folder = folder, Type = text => typed = text, Open = (path, _) => opened = path });
            Assert.Null(program.Failed);
            string screen = program.ScreenText();
            Assert.Contains("sub\\", screen);
            Assert.Contains("alpha.txt", screen);
            Assert.Contains("3 of 3", screen);

            program.Input("bt");
            Assert.Contains("1 of 3", program.ScreenText());
            Assert.DoesNotContain("alpha.txt", program.ScreenText());

            // escape clears what was typed, enter on the folder goes in, backspace comes back out with it picked
            program.Input("\u001b");
            program.Input("\r");
            Assert.Contains("inside.cs", program.ScreenText());
            program.Input("\u007f");
            Assert.Contains("alpha.txt", program.ScreenText());

            // down to alpha.txt, enter opens it and leaves
            program.Input("\u001b[B");
            program.Input("\r");
            Assert.Equal(Path.Combine(folder, "alpha.txt"), opened);
            Assert.False(program.IsRunning);

            var (_, again) = Run(Shipped("files.hor"), new TerminalTuiServices { Folder = folder, Type = text => typed = text });
            again.Input("beta");
            again.Input("\t");
            Assert.Equal(Path.Combine(folder, "beta.md") + " ", typed);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void The_git_viewer_shows_the_branch_the_changes_and_the_log()
    {
        // this repository, read only, nothing is staged or unstaged
        string repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var (_, program) = Run(Shipped("git.hor"), new TerminalTuiServices { Folder = repository });
        Assert.Null(program.Failed);
        string screen = program.ScreenText();
        Assert.Contains(" Status ", screen);
        Assert.Contains("changes", screen);

        program.Input("\t");
        Assert.Contains("What it changed", program.ScreenText());
        program.Input("\r");
        Assert.DoesNotContain("enter on a commit shows it here", program.ScreenText());
        program.Input("q");
        Assert.False(program.IsRunning);
    }

    [Fact]
    public void A_folder_that_is_no_repository_says_so()
    {
        var (_, program) = Run(Shipped("git.hor"), new TerminalTuiServices { Folder = Path.GetTempPath() });
        Assert.Null(program.Failed);
        Assert.Contains("r again", program.ScreenText());
    }

    [Fact]
    public void The_process_list_lists_this_process_and_narrows_by_name()
    {
        var (_, program) = Run(Shipped("processes.hor"));
        Assert.Null(program.Failed);
        Assert.Contains("memory", program.ScreenText());
        string name = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        program.Input(name);
        Assert.Contains(name, program.ScreenText());
        Assert.Contains(System.Environment.ProcessId.ToString(), program.ScreenText());
        program.Input("\u001b\u001b");
    }

    [Fact]
    public void The_settings_form_flips_a_flag_and_steps_a_choice()
    {
        var settings = new TerminalSettings(null);
        var set = new List<string>();
        TerminalTuiServices services = new()
        {
            Settings = () => TerminalSettingsForm.Script(["core"], ["cmd"], TerminalSettingsForm.Values(settings)),
            SetSetting = (key, value) =>
            {
                set.Add(key);
                return settings.Set(key, TerminalSettingsForm.ValueOf(key, value));
            }
        };

        var (_, program) = Run(Shipped("settings.hor"), services);
        Assert.Null(program.Failed);
        Assert.Contains("theme", program.ScreenText());

        // the first row is the theme, a choice, right steps it on
        string before = settings.Theme;
        program.Input("\u001b[C");
        Assert.NotEqual(before, settings.Theme);

        // down to animations, a flag, enter flips it
        int animations = Array.FindIndex(TerminalSettings.Described, setting => setting.Key == "animations");
        program.Input(string.Concat(Enumerable.Repeat("\u001b[B", animations)));
        bool was = settings.Animations;
        program.Input("\r");
        Assert.NotEqual(was, settings.Animations);

        // text_scale is a number, enter edits it on the line at the bottom
        program.Input("\u001b[H");
        program.Input(string.Concat(Enumerable.Repeat("\u001b[B", Array.FindIndex(TerminalSettings.Described, setting => setting.Key == "text_scale"))));
        program.Input("\r\u0015" + "0.3\r");
        Assert.Equal(0.3f, settings.TextScale, 3);
        Assert.Contains("text_scale", set);
    }

    [Fact]
    public void The_repl_runs_a_line_and_offers_names_in_a_list_tab_takes()
    {
        var runtime = new Horizon.HIDL.HIDLRuntime();
        TerminalTuiServices services = new()
        {
            Evaluate = code => TerminalHost.Evaluate(runtime, code),
            Complete = word => TerminalHost.NamesFor(runtime, word)
        };

        var (_, program) = Run(Shipped("repl.hor"), services);
        Assert.Null(program.Failed);
        program.Input("1 + 2\r");
        Assert.Null(program.Failed);
        Assert.Contains("hidl > 1 + 2", program.ScreenText());
        Assert.Contains("\n3", program.ScreenText());

        program.Input("let speed = 4;\r");
        program.Input("spe");
        Assert.Contains("speed", program.ScreenText());
        program.Input("\t\r");
        Assert.Contains("\n4", program.ScreenText());

        program.Input("math.flo");
        program.Input("\t(2.7)\r");
        Assert.Contains("hidl > math.floor(2.7)", program.ScreenText());
        Assert.Contains("\n2", program.ScreenText());

        program.Input("nonsense(\r");
        Assert.Null(program.Failed);
        program.Input("\u001b");
        Assert.False(program.IsRunning);
    }
}
