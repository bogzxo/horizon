using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>
/// A settings page as a page of properties, the way Hex's Properties panel has one. No sections down the left, sections
/// that fold under their heading, values only to read and copy, and a tree that picks several rows.
/// </summary>
public class PropertiesPageTests
{
    private static SettingsPage Made(Dictionary<string, IRuntimeValue> values)
    {
        var page = new SettingsPage
        {
            ShowNav = false,
            CollapsibleSections = true,
            CopyValues = true,
            Settings =
            [
                new UISetting("path", "Path", UIFormKind.Text, "Where it is", "File", ReadOnly: true),
                new UISetting("size", "Size", UIFormKind.Text, "How big", "File", ReadOnly: true),
                new UISetting("copy", "Copy to Output Directory", UIFormKind.Choice, "Whether it's copied", "Project",
                    Choices: ["Never", "PreserveNewest"], ChoiceLabels: ["Do not copy", "Copy if newer"])
            ],
            ValueOf = key => values.GetValueOrDefault(key),
            IsModified = key => key == "copy" && values.GetValueOrDefault(key) is StringValue { Value: not "Never" }
        };
        page.UpdateTree(1.0f / 60.0f);
        return page;
    }

    [Fact]
    public void A_section_folds_under_its_heading_and_a_search_still_looks_in_it()
    {
        var page = Made(new() { ["path"] = new StringValue("C:\\a.txt"), ["size"] = new StringValue("1 byte"), ["copy"] = new StringValue("Never") });
        Assert.Equal(3, page.RowsShown);

        page.ToggleSection("File");
        Assert.True(page.IsFolded("File"));
        Assert.Equal(1, page.RowsShown);

        page.Find("size");
        Assert.Equal(1, page.RowsShown);
        page.Find(string.Empty);
        Assert.Equal(1, page.RowsShown);

        page.ToggleSection("File");
        Assert.Equal(3, page.RowsShown);
    }

    [Fact]
    public void A_rows_right_click_copies_its_value_as_it_reads()
    {
        var page = Made(new() { ["path"] = new StringValue("C:\\a.txt"), ["copy"] = new StringValue("PreserveNewest") });

        Menu menu = page.MenuOf("copy")!;
        Assert.Contains(menu.Items, item => item.Label == "Copy value");
        Assert.DoesNotContain(menu.Items, item => item.Label == "Copy setting ID");
        Assert.Equal("Copy if newer", page.ValueText("copy"));
        Assert.Equal("C:\\a.txt", page.ValueText("path"));
    }

    [Fact]
    public void A_read_only_box_selects_and_copies_but_takes_nothing_typed()
    {
        var box = new TextBox { Text = "C:\\a.txt", ReadOnly = true };
        Assert.False(box.Insert('x'));
        box.SelectAll();
        box.Backspace();
        box.Delete();
        Assert.Equal("C:\\a.txt", box.Text);
        Assert.Equal("C:\\a.txt", box.Selection);
    }

    [Fact]
    public void A_tree_picks_several_rows_and_a_plain_pick_is_one_again()
    {
        var a = new UITreeItem("a.json");
        var b = new UITreeItem("b.json");
        var c = new UITreeItem("c.json");
        IReadOnlyList<UITreeItem>? told = null;
        var tree = new TreeView { Items = [a, b, c], MultiSelect = true, OnSelectionChanged = picked => told = picked };

        tree.SelectMany([a, c]);
        Assert.Equal([a, c], tree.SelectedItems);
        Assert.Equal([a, c], told);
        Assert.True(tree.IsSelected(c));
        Assert.False(tree.IsSelected(b));

        tree.Selected = b;
        Assert.Equal([b], tree.SelectedItems);

        // A row gone from the tree isn't picked any more
        tree.SelectMany([a, c]);
        tree.Items = [a, b];
        Assert.Equal([a], tree.SelectedItems);
    }
}
