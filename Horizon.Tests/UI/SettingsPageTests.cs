using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>A settings page's search, @modified, what Escape does, the sections down the left and a row's menu.</summary>
public class SettingsPageTests
{
    private static UISetting Row(string key, string group, string help = "") => new(key, key, UIFormKind.Text, help, group);

    // A page over a few settings, the changed ones by their keys, built the way its first update builds it
    private static (SettingsPage Page, Dictionary<string, IRuntimeValue> Values, List<string> Closed) Made(params string[] changed)
    {
        var values = changed.ToDictionary(key => key, key => (IRuntimeValue)new StringValue("changed"));
        var closed = new List<string>();
        var page = new SettingsPage
        {
            Settings =
            [
                Row("wrap_code", "Editor · Text", "Wraps long lines"),
                Row("wrap_flow", "Editor · Text", "How quickly wrapped words glide"),
                Row("tab_size", "Editor · Text", "How wide a tab is"),
                Row("scrollback", "Terminal · Text", "How many lines are kept"),
                Row("theme", "Look · Theme", "The colours")
            ],
            ValueOf = key => values.GetValueOrDefault(key),
            IsModified = values.ContainsKey,
            OnChanged = (key, value) => values[key] = value,
            OnReset = key => values.Remove(key),
            OnClose = () => closed.Add("closed")
        };
        page.UpdateTree(1.0f / 60.0f);
        return (page, values, closed);
    }

    [Fact]
    public void Modified_leaves_only_the_changed_rows_and_words_narrow_it_further()
    {
        var (page, _, _) = Made("wrap_code", "scrollback");
        Assert.Equal(5, page.RowsShown);

        page.Find("@modified");
        Assert.Equal(2, page.RowsShown);
        Assert.Equal("2 settings found", page.FoundText);

        page.Find("@Modified wrap");
        Assert.Equal(1, page.RowsShown);
        Assert.Equal("1 setting found", page.FoundText);

        page.Find(string.Empty);
        Assert.Equal(5, page.RowsShown);
        Assert.Equal(string.Empty, page.FoundText);
    }

    [Fact]
    public void Escape_empties_the_search_first_and_closes_the_page_after()
    {
        var (page, _, closed) = Made();
        page.Find("wrap");

        Assert.True(page.Escape());
        Assert.Equal(string.Empty, page.Search.Text);
        Assert.Equal(5, page.RowsShown);
        Assert.Empty(closed);

        Assert.True(page.Escape());
        Assert.Single(closed);
    }

    [Fact]
    public void The_sections_down_the_left_narrow_to_what_the_search_found_and_dot_the_changed_ones()
    {
        var (page, _, _) = Made("scrollback");
        page.UpdateTree(1.0f / 60.0f);
        Assert.Equal(["Editor · Text", "Terminal · Text", "Look · Theme"], page.SectionsShown);
        Assert.Equal(["Terminal · Text"], page.SectionsChanged);

        page.Find("wrap");
        page.UpdateTree(1.0f / 60.0f);
        Assert.Equal(["Editor · Text"], page.SectionsShown);

        // No dots while searching, the count says what's there
        Assert.Empty(page.SectionsChanged);
    }

    [Fact]
    public void A_rows_menu_has_its_reset_only_while_its_changed_and_its_line_of_hidl()
    {
        var (page, _, _) = Made("theme");

        Assert.Equal(["Reset setting", "Copy setting ID", "Copy as HIDL line"], page.MenuOf("theme")!.Items.Where(item => item.Label.Length > 0).Select(item => item.Label));
        Assert.Equal(["Copy setting ID"], page.MenuOf("tab_size")!.Items.Select(item => item.Label));
        Assert.Equal("theme: \"changed\"", page.LineOf("theme"));
        Assert.Null(page.MenuOf("nothing"));

        page.MenuOf("theme")!.Items[0].OnPicked!();
        Assert.False(page.IsModified!("theme"));
    }

    [Fact]
    public void A_search_lights_what_it_found_in_the_name_and_the_sentence_and_lets_go_once_its_emptied()
    {
        var (page, _, _) = Made();

        page.Find("wrap");
        Assert.Equal("[b][color:accent]wrap[/color]_code[/b]", page.TitleShown("wrap_code"));
        Assert.Equal("[color:accent]Wrap[/color]s long lines", page.HelpShown("wrap_code"));

        page.Find(string.Empty);
        Assert.Equal("[b]wrap_code[/b]", page.TitleShown("wrap_code"));
        Assert.Equal("Wraps long lines", page.HelpShown("wrap_code"));
    }

    [Fact]
    public void Lighting_words_makes_markup_in_the_text_plain_and_runs_overlapping_words_together()
    {
        Assert.Equal("(b)[color:accent]Wrap[/color](/b) it", SettingsPage.Highlighted("[b]Wrap[/b] it", ["wrap"]));
        Assert.Equal("[color:accent]wrap[/color] code", SettingsPage.Highlighted("wrap code", ["wra", "rap"]));
        Assert.Equal("wrap code", SettingsPage.Highlighted("wrap code", ["tab"]));
        Assert.Equal("wrap code", SettingsPage.Highlighted("wrap code", []));
    }

    [Fact]
    public void The_search_finds_a_setting_by_one_of_its_choices_as_written_or_as_the_dropdown_says_it()
    {
        var page = new SettingsPage
        {
            Settings =
            [
                new("effects", "Effects", UIFormKind.Choice, "How much moves", "Look · Effects", ["none", "subtle", "all"]),
                new("pull", "Pull", UIFormKind.Choice, "What a pull does", "Git · Sync", ["merge", "fast_forward_only"]),
                Row("tab_size", "Editor · Text", "How wide a tab is")
            ]
        };
        page.UpdateTree(1.0f / 60.0f);

        page.Find("subtle");
        Assert.Equal(1, page.RowsShown);
        page.Find("forward only");
        Assert.Equal(1, page.RowsShown);
        Assert.Equal("1 setting found", page.FoundText);
    }

    [Fact]
    public void At_id_leaves_only_the_setting_of_that_key_and_modified_narrows_it_to_nothing_unchanged()
    {
        var (page, _, _) = Made();

        page.Find("@id:wrap_code");
        Assert.Equal(1, page.RowsShown);
        Assert.Equal("1 setting found", page.FoundText);

        // The key whole, wrap alone is no key
        page.Find("@id:wrap");
        Assert.Equal(0, page.RowsShown);
        Assert.True(page.SaysNothingFound);

        page.Find("@id:wrap_code @modified");
        Assert.Equal(0, page.RowsShown);

        // Picked off the funnel with no key after it yet it narrows nothing
        page.Find("@id:");
        Assert.Equal(5, page.RowsShown);
        Assert.False(page.SaysNothingFound);
    }

    [Fact]
    public void The_funnel_puts_its_word_on_the_end_of_the_search_once()
    {
        var (page, _, _) = Made("theme");
        Assert.Equal(["Modified settings", "Setting by ID", "Settings in a section"], page.FilterMenu().Items.Select(item => item.Label));

        page.FilterMenu().Items[0].OnPicked!();
        Assert.Equal("@modified ", page.Search.Text);
        Assert.Equal(1, page.RowsShown);

        page.AddToSearch(SettingsPage.MODIFIED);
        Assert.Equal("@modified ", page.Search.Text);

        page.AddToSearch(SettingsPage.ID);
        Assert.Equal("@modified @id:", page.Search.Text);

        page.Find(string.Empty);
        page.FilterMenu().Items[2].OnPicked!();
        Assert.Equal("@section:", page.Search.Text);
    }

    [Fact]
    public void At_section_leaves_only_the_sections_with_the_word_in_their_name_and_hides_the_panels_of_the_rest()
    {
        var (page, _, _) = Made();
        var panel = new Label { Text = "Pick a theme" };
        page.SetPanel("Look · Theme", panel);
        page.UpdateTree(1.0f / 60.0f);

        // The section after the dot or the part before it, whatever the case
        page.Find("@section:EDITOR");
        Assert.Equal(3, page.RowsShown);
        Assert.False(panel.Visible);

        page.Find("@section:text");
        Assert.Equal(4, page.RowsShown);

        page.Find("@section:text wrap");
        Assert.Equal(2, page.RowsShown);

        page.Find("@section:theme");
        Assert.Equal(1, page.RowsShown);
        Assert.True(panel.Visible);

        // With nothing after it yet it narrows nothing
        page.Find("@section:");
        Assert.Equal(5, page.RowsShown);
        Assert.True(panel.Visible);
    }

    [Fact]
    public void A_panel_isnt_found_by_what_a_hidden_part_of_it_says()
    {
        var (page, _, _) = Made();
        var panel = new StackPanel();
        panel.Add(new Label { Text = "Profiles" });
        panel.Add(new Label { Text = "Hidden words", Visible = false });
        page.SetPanel("Look · Profiles", panel);
        page.UpdateTree(1.0f / 60.0f);

        page.Find("hidden");
        Assert.False(panel.Visible);

        page.Find("profiles");
        Assert.True(panel.Visible);
    }

    [Fact]
    public void A_sections_menu_resets_every_change_in_it_and_has_nothing_for_a_section_with_none()
    {
        var (page, values, _) = Made("wrap_code", "tab_size", "theme");

        Assert.Equal(["Reset 2 settings in Text"], page.SectionMenu("Editor · Text").Items.Select(item => item.Label));
        Assert.Equal(["Reset 1 setting in Theme"], page.SectionMenu("Look · Theme").Items.Select(item => item.Label));
        Assert.Empty(page.SectionMenu("Terminal · Text").Items);

        page.SectionMenu("Editor · Text").Items[0].OnPicked!();
        Assert.Equal(["theme"], values.Keys);
        Assert.Empty(page.SectionMenu("Editor · Text").Items);
    }

    [Fact]
    public void A_section_at_a_time_passes_over_the_ones_a_search_left_nothing_in()
    {
        var (page, _, _) = Made();

        Assert.True(page.MoveSection(1));
        Assert.Equal("Editor · Text", page.CurrentGroup);
        Assert.True(page.MoveSection(1));
        Assert.Equal("Terminal · Text", page.CurrentGroup);
        Assert.True(page.MoveSection(-1));
        Assert.Equal("Editor · Text", page.CurrentGroup);
        Assert.False(page.MoveSection(-1));

        // Terminal has no o in it, the search leaves it out and a section on goes straight past it
        page.Find("@section:o");
        page.UpdateTree(1.0f / 60.0f);
        Assert.Equal(["Editor · Text", "Look · Theme"], page.SectionsShown);
        Assert.True(page.MoveSection(1));
        Assert.Equal("Look · Theme", page.CurrentGroup);
        Assert.False(page.MoveSection(1));
    }

    [Fact]
    public void Nothing_has_the_keyboard_in_a_page_thats_in_no_ui_so_no_row_or_section_is_focused()
    {
        var (page, _, _) = Made();
        Assert.Null(page.FocusedKey);
        Assert.Null(page.FocusedSection);
        Assert.False(page.Press(UIKeyboard.DOWN));
        Assert.False(page.MoveRow(-1));
    }

    [Fact]
    public void Reveal_empties_the_search_and_flashes_the_row_till_it_fades()
    {
        // Where it scrolls to needs a skin to lay the rows out, the settings self test of Hex sees that
        var (page, _, _) = Made();
        page.Find("wrap");

        Assert.True(page.Reveal("theme"));
        Assert.False(page.Reveal("nothing"));
        Assert.Equal(string.Empty, page.Search.Text);
        Assert.Equal(5, page.RowsShown);
        Assert.Equal("theme", page.FlashingKey);

        for (int i = 0; i < 60; i++)
            page.UpdateTree(1.0f / 60.0f);
        Assert.Null(page.FlashingKey);
    }

    [Fact]
    public void Reveal_asked_for_before_the_page_is_built_flashes_once_it_is()
    {
        var page = new SettingsPage { Settings = [Row("wrap_code", "Editor · Text"), Row("theme", "Look · Theme")] };
        Assert.True(page.Reveal("theme"));
        Assert.Null(page.FlashingKey);

        page.UpdateTree(1.0f / 60.0f);
        Assert.Equal("theme", page.FlashingKey);
    }

    // A page over settings with keywords, a switch and what hangs off it, the way an editor's catalog has them
    private static (SettingsPage Page, Dictionary<string, IRuntimeValue> Values) Linked(Func<string, IRuntimeValue?>? defaults = null)
    {
        var values = new Dictionary<string, IRuntimeValue> { ["auto_save"] = Values.Bool(false), ["auto_save_delay"] = new NumberValue(1.0f), ["tab_size"] = new NumberValue(4.0f) };
        var page = new SettingsPage
        {
            Settings =
            [
                new("wrap_code", "Wrap code", UIFormKind.Flag, "Whether long lines wrap", "Editor · Code", Keywords: "word wrap wordwrap line wrap"),
                new("code_zoom", "Code zoom", UIFormKind.Number, "How big the code is", "Editor · Code", Least: 0.5f, Most: 3.0f, Step: 0.05f, Slider: true, Unit: "×", Keywords: "font size zoom"),
                new("tab_size", "Tab size", UIFormKind.Whole, "How wide a tab is", "Editor · Text", Least: 1.0f, Most: 16.0f, Keywords: "tab width indent size"),
                new("auto_save", "Save by itself", UIFormKind.Flag, "Whether a file saves itself", "Editor · Files"),
                new("auto_save_delay", "Auto save delay", UIFormKind.Number, "How long typing has stopped for", "Editor · Files", Least: 0.5f, Most: 10.0f, Step: 0.5f, Slider: true, Unit: "s", DependsOn: "auto_save"),
                new("pull", "Pull with", UIFormKind.Choice, "What a pull does", "Editor · Git", ["merge", "ff_only"], ChoiceLabels: ["Merge", "Fast-forward only"]),
                new("picture", "Picture", UIFormKind.Text, "The picture's file", "Look · Backdrop")
            ],
            ValueOf = values.GetValueOrDefault,
            IsModified = key => false,
            OnChanged = (key, value) => values[key] = value,
            DefaultOf = defaults
        };
        page.UpdateTree(1.0f / 60.0f);
        return (page, values);
    }

    [Fact]
    public void The_search_finds_a_setting_by_its_keywords_and_by_words_run_together()
    {
        var (page, _) = Linked();

        foreach (var (typed, key) in new[] { ("word wrap", "wrap_code"), ("wordwrap", "wrap_code"), ("font size", "code_zoom"), ("tabsize", "tab_size"), ("tab width", "tab_size"), ("autosave", "auto_save"), ("TAB_SIZE", "tab_size") })
        {
            page.Find(typed);
            Assert.True(page.IsRowShown(key), $"{typed} should find {key}");
            Assert.NotEqual("0 settings found", page.FoundText);
        }

        // Every word still has to be in it, word wrap leaves the zoom out and font size the wrap
        page.Find("word wrap");
        Assert.False(page.IsRowShown("code_zoom"));
        page.Find("font size");
        Assert.False(page.IsRowShown("wrap_code"));

        // Keywords are searched and never shown, the row says what it always did
        page.Find("wordwrap");
        Assert.Equal("[b]Wrap code[/b]", page.TitleShown("wrap_code"));
        Assert.Equal("wordwrap", SettingsPage.Compact("Word-Wrap"));
        Assert.Equal("tabsize", SettingsPage.Compact("tab_size"));

        // A word with no letter in it runs together to nothing, which finds nothing rather than everything
        page.Find("--");
        Assert.Equal(0, page.RowsShown);
    }

    [Fact]
    public void A_row_that_hangs_off_a_switch_is_in_a_little_and_faint_while_the_switch_is_off()
    {
        var (page, _) = Linked();
        Assert.Equal((true, true), page.DependentShown("auto_save_delay"));
        Assert.Equal((false, false), page.DependentShown("auto_save"));

        // Switched on, it comes back to full strength and stays in its place
        page.Enter("auto_save", Values.Bool(true));
        Assert.Equal((true, false), page.DependentShown("auto_save_delay"));
        page.Enter("auto_save", Values.Bool(false));
        Assert.Equal((true, true), page.DependentShown("auto_save_delay"));
        Assert.Null(page.DependentShown("nothing"));
    }

    [Fact]
    public void A_reset_says_what_it_puts_back()
    {
        var usual = new Dictionary<string, IRuntimeValue>
        {
            ["tab_size"] = new NumberValue(4.0f),
            ["wrap_code"] = Values.Bool(false),
            ["auto_save"] = Values.Bool(true),
            ["pull"] = new StringValue("ff_only"),
            ["code_zoom"] = new NumberValue(1.0f),
            ["auto_save_delay"] = new NumberValue(0.5f),
            ["picture"] = new StringValue(string.Empty)
        };
        var (page, _) = Linked(usual.GetValueOrDefault);

        Assert.Equal("Reset to 4", page.ResetText("tab_size"));
        Assert.Equal("Reset to Off", page.ResetText("wrap_code"));
        Assert.Equal("Reset to On", page.ResetText("auto_save"));
        Assert.Equal("Reset to Fast-forward only", page.ResetText("pull"));
        Assert.Equal("Reset to 1×", page.ResetText("code_zoom"));
        Assert.Equal("Reset to 500 ms", page.ResetText("auto_save_delay"));

        // Nothing written, or nobody saying, is the plain one
        Assert.Equal(SettingsPage.RESET_TO_DEFAULT, page.ResetText("picture"));
        Assert.Equal(SettingsPage.RESET_TO_DEFAULT, page.ResetText("nothing"));
        Assert.Equal(SettingsPage.RESET_TO_DEFAULT, Linked().Page.ResetText("tab_size"));
    }

    [Fact]
    public void A_file_section_says_what_a_setting_comes_as_and_the_window_passes_it_on()
    {
        var settings = new UISetting[] { new("tab_size", "Tab size", UIFormKind.Whole, "How wide a tab is", "Editor · Text") };
        var source = new FileSectionSource(null, "editor", settings, new Dictionary<string, IRuntimeValue> { ["tab_size"] = new NumberValue(4.0f) });
        Assert.Equal(new NumberValue(4.0f), source.Default("tab_size"));
        Assert.Null(source.Default("nothing"));

        var window = new SettingsWindow([source], () => { });
        Assert.Equal("Reset to 4", window.Page.ResetText("tab_size"));
    }

    [Fact]
    public void A_panel_isnt_found_by_a_sentence_down_in_its_list_only_by_its_headings_its_buttons_and_what_it_opens_with()
    {
        var (page, _, _) = Made();
        var panel = new StackPanel();
        panel.Add(new Label { Text = "[b]Add a language server[/b]" });
        panel.Add(new Label { Text = "Every shortcut (keybindings) is in a window of its own", Wrap = true });
        var card = panel.Add(new StackPanel()).Add(new StackPanel());
        card.Add(new Label { Text = "Soft wraps the long lines of a card", Wrap = true });
        card.Add(new Button { Label = "Install" });
        page.SetPanel("Extensions · Available", panel);
        page.UpdateTree(1.0f / 60.0f);

        page.Find("wraps");
        Assert.False(panel.Visible);
        page.Find("add a language");
        Assert.True(panel.Visible);
        page.Find("install");
        Assert.True(panel.Visible);
        page.Find("keybinding");
        Assert.True(panel.Visible);
    }

    [Fact]
    public void The_window_opens_at_most_of_what_its_in_between_its_least_and_its_most()
    {
        var least = new System.Numerics.Vector2(640.0f, 420.0f);
        var full = SettingsWindow.SizeFor(new(1920.0f, 1080.0f), least);
        Assert.Equal(1382.4f, full.X, 0.01f);
        Assert.Equal(842.4f, full.Y, 0.01f);
        // Past the usual most a big screen gets most of it, up to a most of its own
        Assert.Equal(new System.Numerics.Vector2(3072.0f, 1728.0f), SettingsWindow.SizeFor(new(3840.0f, 2160.0f), least));
        Assert.Equal(new System.Numerics.Vector2(1152.0f, 702.0f), SettingsWindow.SizeFor(new(1600.0f, 900.0f), least));
        Assert.Equal(least, SettingsWindow.SizeFor(new(700.0f, 400.0f), least));
    }
}
