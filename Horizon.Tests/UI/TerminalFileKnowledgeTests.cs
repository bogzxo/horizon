using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>What an editor knows about Dusk's files out of api.hor, the description written and read, completion, hover and the checks.</summary>
public class TerminalFileKnowledgeTests
{
    private static TerminalApiDescription Api()
    {
        var api = TerminalApiDescription.Engine();
        api.Commands["new_tab"] = ("New tab", "Ctrl+Shift+T");
        api.Commands["quit"] = ("Quit", "Ctrl+Shift+F4");
        api.Functions["quit"] = (new TerminalApiFunction("quit", "", "Closes Dusk."), "dusk");
        return api;
    }

    private static TerminalFileKnowledge Knowing() => new(Api());

    [Fact]
    public void The_description_goes_round_through_its_file()
    {
        TerminalApiDescription api = Api();
        TerminalApiDescription read = TerminalApiDescription.Read(api.Write())!;

        Assert.Equal(api.Settings.Select(setting => setting.Key), read.Settings.Select(setting => setting.Key));
        Assert.Equal(("New tab", "Ctrl+Shift+T"), read.Commands["new_tab"]);
        Assert.Equal("dusk", read.Functions["quit"].Programs);
        Assert.Equal(string.Empty, read.Functions["split"].Programs);
        Assert.Contains(read.Hooks, hook => hook.Name == "on_line");
        Assert.Equal(["horizon", "dark", "graphite", "light", "midnight", "slate", "sunset"], read.Settings.Find(setting => setting.Key == "theme")!.Choices!);
        Assert.Null(TerminalApiDescription.Read("not hidl {{"));
    }

    [Fact]
    public void Two_programs_written_over_each_other_have_both_their_functions()
    {
        var dusk = Api();
        var hex = TerminalApiDescription.Engine();
        hex.Functions["file"] = (new TerminalApiFunction("file", "", "The file on screen."), "hex");
        hex.Functions["quit"] = (new TerminalApiFunction("quit", "", "Closes Hex's terminal."), "hex");
        hex.Commands["hex_panel"] = ("Hex: the panel away", "Ctrl+J");

        dusk.Merge(hex);
        Assert.Equal("hex", dusk.Functions["file"].Programs);
        Assert.Equal("dusk, hex", dusk.Functions["quit"].Programs);
        Assert.Equal(string.Empty, dusk.Functions["split"].Programs);
        Assert.True(dusk.Commands.ContainsKey("hex_panel") && dusk.Commands.ContainsKey("quit"));
    }

    [Theory]
    [InlineData("C:/x/settings.hor", "#define dusk\nlet settings = {};", TerminalFileKind.Settings)]
    [InlineData("C:/x/keys.hor", "#define dusk\nlet keys = {};", TerminalFileKind.Keys)]
    [InlineData("C:/x/keys.hor", "let keys = { Undo: \"Ctrl+Z\" };", TerminalFileKind.None)]
    [InlineData("C:/x/mine/plugin.hor", "let plugin = {};\nterminal.command(\"a\", \"A\", func(t) {}, \"\");", TerminalFileKind.Plugin)]
    [InlineData("C:/x/other.hor", "#define dusk\nlet plugin = { name: \"x\" };", TerminalFileKind.Plugin)]
    [InlineData("C:/x/menu.hor", "#define layout\nlet x = 1;", TerminalFileKind.None)]
    public void A_file_is_known_by_its_name_and_its_define(string path, string text, TerminalFileKind kind) =>
        Assert.Equal(kind, TerminalFileKnowledge.KindOf(path, text));

    [Fact]
    public void A_plugin_completes_the_functions_of_the_object()
    {
        var (start, items) = Knowing().Complete(TerminalFileKind.Plugin, "    terminal.spl")!.Value;
        Assert.Equal(13, start);
        Assert.Contains(items, item => item.Label == "split" && item.Insert == "split(");
        Assert.Contains(items, item => item.Label == "quit" && item.Detail.Contains("dusk only"));

        var (_, dusk) = Knowing().Complete(TerminalFileKind.Plugin, "dusk.")!.Value;
        Assert.Contains(dusk, item => item.Label == "new_tab");

        var (_, hooks) = Knowing().Complete(TerminalFileKind.Plugin, "func on_")!.Value;
        Assert.Contains(hooks, item => item.Label == "on_exit" && item.Insert == "on_exit(terminal, code) ");
        Assert.Null(Knowing().Complete(TerminalFileKind.Plugin, "let x = 1 + "));
    }

    [Fact]
    public void A_settings_file_completes_its_keys_and_the_choices_of_one()
    {
        var (_, keys) = Knowing().Complete(TerminalFileKind.Settings, "    the")!.Value;
        Assert.Contains(keys, item => item.Label == "theme" && item.Insert == "theme: ");

        var (start, choices) = Knowing().Complete(TerminalFileKind.Settings, "    theme: \"mid")!.Value;
        Assert.Equal(12, start);
        Assert.Contains(choices, item => item.Label == "midnight");
        Assert.Null(Knowing().Complete(TerminalFileKind.Settings, "    font: \"ha"));
    }

    [Fact]
    public void A_keys_file_completes_the_commands_and_a_key()
    {
        var (_, commands) = Knowing().Complete(TerminalFileKind.Keys, "    new")!.Value;
        Assert.Contains(commands, item => item.Label == "new_tab" && item.Detail.Contains("Ctrl+Shift+T"));

        var (start, keys) = Knowing().Complete(TerminalFileKind.Keys, "    quit: \"Ctrl+Sh")!.Value;
        Assert.Equal(16, start);
        Assert.Contains(keys, item => item.Label == "Shift+");
        Assert.DoesNotContain(keys, item => item.Label == "Ctrl+");
        Assert.Contains(keys, item => item.Label == "PgUp");
    }

    [Fact]
    public void A_word_is_explained_under_the_pointer()
    {
        var knowing = Knowing();
        string plugin = "terminal.split(\"right\");";
        Assert.Contains("Splits the focused pane", knowing.Hover(TerminalFileKind.Plugin, plugin, 9, 14));
        Assert.Contains("isn't something", knowing.Hover(TerminalFileKind.Plugin, "terminal.fly();", 9, 12));

        string setting = "    scrollback: 5000,";
        Assert.Contains("How many rows", knowing.Hover(TerminalFileKind.Settings, setting, 4, 14));

        string key = "    quit: \"F4\",";
        Assert.Contains("Quit", knowing.Hover(TerminalFileKind.Keys, key, 4, 8));
        Assert.Equal(["\"right\" or \"down\"", "profile?"], knowing.Parameters("terminal.split")!);
        Assert.Null(knowing.Parameters("terminal.nothing"));
    }

    [Fact]
    public void What_is_wrong_is_found_line_by_line()
    {
        var knowing = Knowing();

        var settings = knowing.Check(TerminalFileKind.Settings, "let settings = {\n    theme: \"neon\",\n    frobnicate: 3,\n    font: \"hack\",\n    nested: { theme: 1 }\n};");
        Assert.Contains(settings, problem => problem.Line == 1 && problem.Message.Contains("one of"));
        Assert.Contains(settings, problem => problem.Line == 2 && problem.Message.Contains("frobnicate is no setting"));
        Assert.Equal(3, settings.Count);

        // Hex's file in sections, the terminal's keys checked in its section and the editor's left to the editor
        var sectioned = knowing.Check(TerminalFileKind.Settings, "let settings = {\n    terminal: {\n        theme: \"neon\",\n        frobnicate: 3\n    },\n    editor: { code_zoom: 1, wobble: 2 }\n};");
        Assert.Contains(sectioned, problem => problem.Line == 2 && problem.Message.Contains("one of"));
        Assert.Contains(sectioned, problem => problem.Line == 3 && problem.Message.Contains("frobnicate is no setting"));
        Assert.Equal(2, sectioned.Count);

        var keys = knowing.Check(TerminalFileKind.Keys, "let keys = {\n    new_tab: \"Ctrl+Shift+Banana\",\n    fly: \"F3\",\n    quit: \"\" // none\n};");
        Assert.Contains(keys, problem => problem.Line == 1 && problem.Message.Contains("isn't a key") && problem.Column == 14);
        Assert.Contains(keys, problem => problem.Line == 2 && problem.Message.Contains("no plugin registers"));
        Assert.Equal(2, keys.Count);

        var plugin = knowing.Check(TerminalFileKind.Plugin, "terminal.command(\"a\", \"A\", func(terminal) { terminal.fly(); }, \"Ctrl+Nope\");\n// terminal.commented()\ndusk.split(\"right\");");
        Assert.Contains(plugin, problem => problem.Message.Contains("nothing called fly"));
        Assert.Contains(plugin, problem => problem.Message.Contains("Ctrl+Nope isn't a key"));
        Assert.Equal(2, plugin.Count);
    }
}
