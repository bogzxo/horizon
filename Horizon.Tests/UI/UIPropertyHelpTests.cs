using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Horizon.Tests;

public class UIPropertyHelpTests
{
    // Every kind a layout can make, by the name a layout makes it by (see UIModule.Runtime)
    public static TheoryData<string, UIComponent> Kinds() => new()
    {
        { "button", new Button() },
        { "toggle", new ToggleButton() },
        { "progress_bar", new ProgressBar() },
        { "slider", new Slider() },
        { "textbox", new TextBox() },
        { "label", new Label() },
        { "image", new Image() },
        { "panel", new Panel() },
        { "group", new Group() },
        { "tabs", new TabPanel() },
        { "stack", new StackPanel() },
        { "collapsible", new Collapsible() },
        { "grid", new GridPanel() },
        { "scroll", new ScrollPanel() },
        { "number_box", new NumberBox() },
        { "selector", new Selector() },
        { "dropdown", new Dropdown() },
        { "list", new ListBox() },
        { "divider", new Divider() },
        { "spacer", new Spacer() },
        { "color_picker", new ColorPicker() },
        { "menu_bar", new MenuBar() },
        { "code", new CodeView() },
        { "readout", new Readout() },
        { "meter", new Meter() },
        { "graph", new Graph() },
        { "segments", new SegmentBar() },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_property_a_layout_can_set_says_what_it_does(string kind, UIComponent component)
    {
        // What it is put inside of is said by where it is in an editor, nobody edits it as a property
        string[] unexplained = [.. component.Object.Properties.Keys.Where(name => name != "parent" && UIPropertyHelp.Describe(kind, name).Length == 0)];

        Assert.True(unexplained.Length == 0, $"{kind} has nothing to say about {string.Join(", ", unexplained)}");
    }

    [Fact]
    public void A_kind_can_mean_something_else_by_a_name()
    {
        Assert.NotEqual(UIPropertyHelp.Describe("slider", "value"), UIPropertyHelp.Describe("selector", "value"));
        Assert.Equal(UIPropertyHelp.Describe(null, "pos"), UIPropertyHelp.Describe("button", "pos"));
        Assert.Equal(string.Empty, UIPropertyHelp.Describe("button", "nothing_is_called_this"));
    }
}
