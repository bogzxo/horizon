using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>
/// The parts down the left of a settings page as a tree, folded and opened, opened by a search, the counts beside them
/// and the reset of a whole part. The parts here have names no other test's page has, folding is kept for the program's
/// life by the part's name and the other tests run alongside.
/// </summary>
public class SettingsNavTests
{
    private const string FIRST = "NavFirst", SECOND = "NavSecond";

    private static UISetting Row(string key, string group, string help = "") => new(key, key, UIFormKind.Text, help, group);

    // A page over two parts of two sections each, the changed ones by their keys, built the way its first update builds it
    private static (SettingsPage Page, Dictionary<string, IRuntimeValue> Values) Made(params string[] changed)
    {
        var values = changed.ToDictionary(key => key, key => (IRuntimeValue)new StringValue("changed"));
        var page = new SettingsPage
        {
            Settings =
            [
                Row("wrap_code", $"{FIRST} · Text", "Wraps long lines"),
                Row("tab_size", $"{FIRST} · Text", "How wide a tab is"),
                Row("zoom", $"{FIRST} · Code", "How big the code is"),
                Row("scrollback", $"{SECOND} · Scrollback", "How many lines are kept"),
                Row("bell", $"{SECOND} · Input", "What the bell does")
            ],
            ValueOf = key => values.GetValueOrDefault(key),
            IsModified = values.ContainsKey,
            OnChanged = (key, value) => values[key] = value,
            OnReset = key => values.Remove(key)
        };
        page.UpdateTree(1.0f / 60.0f);
        page.UpdateTree(1.0f / 60.0f);
        return (page, values);
    }

    [Fact]
    public void Folding_a_part_hides_its_links_and_never_its_rows()
    {
        var (page, _) = Made();
        try
        {
            page.SetPartCollapsed(SECOND, true);
            Assert.True(SettingsPage.IsPartCollapsed(SECOND));
            Assert.Equal([$"{FIRST} · Text", $"{FIRST} · Code"], page.SectionsShown);
            Assert.Equal([FIRST, SECOND], page.PartsShown);
            Assert.Equal(5, page.RowsShown);
            Assert.True(page.IsRowShown("scrollback"));

            page.TogglePart(SECOND);
            Assert.False(SettingsPage.IsPartCollapsed(SECOND));
            Assert.Equal(4, page.SectionsShown.Count);
        }
        finally
        {
            page.SetPartCollapsed(SECOND, false);
        }
    }

    [Fact]
    public void Folding_lasts_for_a_page_made_afresh()
    {
        var (page, _) = Made();
        try
        {
            page.SetPartCollapsed(FIRST, true);
            var (again, _) = Made();
            Assert.Equal([$"{SECOND} · Scrollback", $"{SECOND} · Input"], again.SectionsShown);
        }
        finally
        {
            page.SetPartCollapsed(FIRST, false);
        }
    }

    [Fact]
    public void A_search_opens_a_folded_part_it_found_something_in()
    {
        var (page, _) = Made();
        try
        {
            page.SetPartCollapsed(SECOND, true);
            page.Find("bell");
            page.UpdateTree(1.0f / 60.0f);
            Assert.Equal([$"{SECOND} · Input"], page.SectionsShown);
            Assert.Equal([SECOND], page.PartsShown);
            Assert.False(SettingsPage.IsPartCollapsed(SECOND));

            // Folded while the search is up, it stays folded till the search changes
            page.SetPartCollapsed(SECOND, true);
            page.UpdateTree(1.0f / 60.0f);
            Assert.Empty(page.SectionsShown);
            page.Find("bel");
            page.UpdateTree(1.0f / 60.0f);
            Assert.Equal([$"{SECOND} · Input"], page.SectionsShown);
        }
        finally
        {
            page.SetPartCollapsed(SECOND, false);
        }
    }

    [Fact]
    public void A_part_counts_its_changes_and_while_searching_its_hits()
    {
        var (page, _) = Made("wrap_code", "zoom", "bell");
        Assert.Equal(2, page.PartCount(FIRST));
        Assert.Equal(1, page.PartCount(SECOND));

        page.Find("a");
        page.UpdateTree(1.0f / 60.0f);
        Assert.Equal(page.Settings.Count(setting => setting.Group.StartsWith(FIRST, StringComparison.Ordinal) && page.IsRowShown(setting.Key)), page.PartCount(FIRST));

        page.Find(string.Empty);
        page.UpdateTree(1.0f / 60.0f);
        Assert.Equal(2, page.PartCount(FIRST));
    }

    [Fact]
    public void A_part_resets_all_its_changes_and_only_its_own()
    {
        var (page, values) = Made("wrap_code", "zoom", "bell");
        Menu menu = page.PartMenu(FIRST);
        Assert.Equal($"Reset 2 changes in {FIRST}", Assert.Single(menu.Items).Label);
        Assert.Empty(page.PartMenu("Nowhere").Items);

        Assert.Equal(2, page.ResetPart(FIRST));
        Assert.Equal(["bell"], values.Keys);
        page.UpdateTree(1.0f / 60.0f);
        Assert.Equal(0, page.PartCount(FIRST));
        Assert.Empty(page.PartMenu(FIRST).Items);

        Assert.Equal(1, page.ResetAll());
        Assert.Empty(values);
    }

    [Fact]
    public void The_part_of_a_section_is_what_comes_before_its_dot()
    {
        Assert.Equal("Editor", SettingsPage.PartOf("Editor · Text"));
        Assert.Equal("Plugins", SettingsPage.PartOf("Plugins"));
        Assert.Equal("General", SettingsPage.PartOf(string.Empty));
    }
}
