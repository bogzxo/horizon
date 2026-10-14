using Horizon.UI.Components;

namespace Horizon.Tests;

public class UIStyleTests
{
    public static TheoryData<UIComponent> Styled() =>
    [
        new Button(),
        new ToggleButton(),
        new Selector(),
        new Dropdown(),
        new TextBox(),
        new NumberBox(),
        new ProgressBar(),
        new Slider(),
        new ListBox()
    ];

    [Theory]
    [MemberData(nameof(Styled))]
    public void Everything_with_art_of_its_own_says_which_in_a_layout(UIComponent component)
    {
        // Which is all an editor needs to offer the skin's art for it, Hex goes by the name of the property
        Assert.True(component.Object.Properties.ContainsKey("style"), $"{component.GetType().Name} has no style to set");
    }

    [Fact]
    public void A_slider_names_its_handle_apart_from_its_bar()
    {
        var slider = new Slider { Style = "boss", HandleStyle = "knob" };

        Assert.Equal("boss", slider.Style);
        Assert.Equal("knob", slider.HandleStyle);
        Assert.True(slider.Object.Properties.ContainsKey("handle"));
    }

    [Fact]
    public void A_piece_picked_by_eye_means_the_style_it_is_a_piece_of()
    {
        // The art picker hands back what was clicked on, which is one piece, the lit one as likely as not
        Assert.Equal("radio", new ToggleButton { Style = "radio_on" }.Style);
        Assert.Equal("radio", new ToggleButton { Style = "radio_off" }.Style);
        Assert.Equal("boss", new ProgressBar { Style = "boss_fill" }.Style);
        Assert.Equal("boss", new ProgressBar { Style = "boss_frame" }.Style);
        Assert.Equal("plank", new TextBox { Style = "plank_focused" }.Style);
        Assert.Equal("button", new Selector { Style = "button_hover" }.Style);
        Assert.Equal("button", new Dropdown { Style = "button_hover" }.Style);

        // And a whole name is left as it is
        Assert.Equal("button_flat", new Selector { Style = "button_flat" }.Style);
    }

    [Fact]
    public void What_nobody_styled_wears_what_it_always_did()
    {
        Assert.Equal("toggle", new ToggleButton().Style);
        Assert.Equal("progress", new ProgressBar().Style);
        Assert.Equal("textbox", new TextBox().Style);
        Assert.Equal("button_flat", new Selector().Style);
        Assert.Equal("button_flat", new Dropdown().Style);
        Assert.Equal("slider_handle", new Slider().HandleStyle);
        Assert.Equal(string.Empty, new ListBox().Style);
    }
}
