using Horizon.UI.Components;

namespace Horizon.Tests;

public class UICollapsibleTests
{
    /// <summary>Helper method to move a component along the way its updates would, a tick at a time.</summary>
    private static void Tick(UIComponent component, float seconds)
    {
        const float step = 1.0f / 120.0f;
        for (float t = 0.0f; t < seconds; t += step)
            component.UpdateTree(step);
    }

    [Fact]
    public void Folded_in_the_layout_starts_folded_and_doesnt_fold_away_in_front_of_everybody()
    {
        var section = new Collapsible("Audio") { Expanded = false };
        var inside = section.Add(new Label("volume"));

        Assert.Equal(0.0f, section.Openness);
        Assert.False(section.ShowsChild(inside));
    }

    [Fact]
    public void A_toggle_folds_it_over_its_time_and_says_so()
    {
        var section = new Collapsible("Audio") { CollapseTime = 0.2f };
        var inside = section.Add(new Label("volume"));
        bool? said = null;
        section.OnToggled = open => said = open;

        // The first update is what says it has settled, after that a change is animated
        Tick(section, 0.01f);
        section.Toggle();

        Assert.False(section.Expanded);
        Assert.False(said);

        Tick(section, 0.1f);
        Assert.InRange(section.Openness, 0.3f, 0.7f);
        Assert.True(section.ShowsChild(inside));

        Tick(section, 0.2f);
        Assert.Equal(0.0f, section.Openness);
        Assert.False(section.ShowsChild(inside));
    }

    [Fact]
    public void Right_opens_it_and_left_folds_it_and_neither_twice()
    {
        var section = new Collapsible("Video");

        Assert.False(section.OnAdjust(1));
        Assert.True(section.OnAdjust(-1));
        Assert.False(section.Expanded);
        Assert.False(section.OnAdjust(-1));
        Assert.True(section.OnAdjust(1));
        Assert.True(section.Expanded);
    }

    [Fact]
    public void A_hover_comes_on_over_its_time_and_goes_off_the_same()
    {
        var button = new Button("Play") { Hover = UIHover.Grow, HoverTime = 0.1f };

        button.IsSelected = true;
        Tick(button, 0.05f);
        Assert.InRange(button.HoverProgress, 0.3f, 0.7f);

        Tick(button, 0.1f);
        Assert.Equal(1.0f, button.HoverProgress);

        button.IsSelected = false;
        Tick(button, 0.15f);
        Assert.Equal(0.0f, button.HoverProgress);
    }

    [Fact]
    public void A_button_a_game_marks_as_chosen_hovers_the_way_the_pointer_makes_it()
    {
        // Fighter2D walks its menus with the pad itself and says which button it is on with Selected, no navigator
        var button = new Button("Fight") { Hover = UIHover.Nudge, HoverTime = 0.1f, Selected = true };

        Tick(button, 0.2f);
        Assert.Equal(1.0f, button.HoverProgress);

        var row = new StackPanel { Hover = UIHover.Nudge, HoverTime = 0.1f, HoverHeld = true };
        Tick(row, 0.2f);
        Assert.Equal(1.0f, row.HoverProgress);
    }

    [Fact]
    public void A_disabled_thing_doesnt_hover_and_neither_does_one_without_a_hover()
    {
        var off = new Button("Quit") { Hover = UIHover.Lift, Enabled = false };
        var plain = new Button("Back");

        off.IsSelected = plain.IsSelected = true;
        Tick(off, 0.5f);
        Tick(plain, 0.5f);

        Assert.Equal(0.0f, off.HoverProgress);
        Assert.Equal(0.0f, plain.HoverProgress);
    }

    [Fact]
    public void A_layout_names_the_hover_and_the_title()
    {
        var section = new Collapsible();
        var properties = section.Object.Properties;

        Assert.Contains("hover", properties.Keys);
        Assert.Contains("hover_amount", properties.Keys);
        Assert.Contains("hover_time", properties.Keys);
        Assert.Contains("title", properties.Keys);
        Assert.Contains("expanded", properties.Keys);
        Assert.Contains("spacing", properties.Keys);
    }
}
