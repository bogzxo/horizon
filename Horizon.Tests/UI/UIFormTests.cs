using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Horizon.Tests;

public class UIFormTests
{
    private static void Tick(UIComponent component, float seconds)
    {
        const float step = 1.0f / 120.0f;
        for (float t = 0.0f; t < seconds; t += step)
            component.UpdateTree(step);
    }

    private static IEnumerable<UIComponent> Inside(UIComponent of)
    {
        foreach (UIComponent child in of.Children)
        {
            yield return child;
            foreach (UIComponent deeper in Inside(child))
                yield return deeper;
        }
    }

    private static FormPanel Made(params UIFormField[] fields)
    {
        var form = new FormPanel { Fields = fields };
        Tick(form, 0.01f);
        return form;
    }

    [Fact]
    public void A_row_a_field_and_a_section_a_group()
    {
        var form = Made(
            new UIFormField("damage", "damage", UIFormKind.Whole, Group: "hit"),
            new UIFormField("level", "level", UIFormKind.Choice, ["mid", "low"], Group: "hit"),
            new UIFormField("input", "input", UIFormKind.Text));

        Assert.Single(form.Children.OfType<Collapsible>());
        Assert.Equal(["damage", "level", "input"], Inside(form).OfType<Label>().Select(label => label.Text));
    }

    [Fact]
    public void Set_from_outside_it_says_nothing_and_shows_the_values()
    {
        var form = Made(new UIFormField("damage", "damage", UIFormKind.Number), new UIFormField("knockdown", "knockdown", UIFormKind.Flag));
        bool told = false;
        form.OnChanged = (_, _) => told = true;

        form.SetValues(new Dictionary<string, IRuntimeValue> { ["damage"] = new NumberValue(12), ["knockdown"] = Values.Bool(true) });

        Assert.False(told);
        Assert.Equal(12, Inside(form).OfType<NumberBox>().Single().Value);
        Assert.True(Inside(form).OfType<ToggleButton>().Single().State);
    }

    [Fact]
    public void A_number_changed_is_told_about_a_whole_one_rounded()
    {
        var form = Made(new UIFormField("damage", "damage", UIFormKind.Whole));
        (string Key, IRuntimeValue Value)? told = null;
        form.OnChanged = (key, value) => told = (key, value);

        Inside(form).OfType<NumberBox>().Single().OnValueChanged!(6.6f);

        Assert.Equal("damage", told?.Key);
        Assert.Equal(7.0f, Assert.IsType<NumberValue>(told?.Value).Value);
        Assert.Equal(7.0f, Assert.IsType<NumberValue>(form.ValueOf("damage")).Value);
    }

    [Fact]
    public void Unset_it_shows_its_default_and_has_no_x_and_the_x_unsets_it()
    {
        var form = Made(new UIFormField("level", "level", UIFormKind.Choice, ["mid", "low", "overhead"], new StringValue("mid")));
        string? cleared = null;
        form.OnCleared = key => cleared = key;
        var clear = Inside(form).OfType<Button>().Single();
        var dropdown = Inside(form).OfType<Dropdown>().Single();

        Assert.Equal("mid", dropdown.Value);
        Assert.False(clear.Visible);

        form.SetValues(new Dictionary<string, IRuntimeValue> { ["level"] = new StringValue("low") });
        Assert.Equal("low", dropdown.Value);
        Assert.True(clear.Visible);

        clear.OnPressed!();
        Assert.Equal("level", cleared);
        Assert.Null(form.ValueOf("level"));
        Assert.Equal("mid", dropdown.Value);
    }

    [Fact]
    public void Flags_are_a_toggle_a_choice_and_come_out_as_one_text()
    {
        var form = Made(new UIFormField("stances", "stances", UIFormKind.Flags, ["standing", "crouching", "jumping"]));
        form.SetValues(new Dictionary<string, IRuntimeValue> { ["stances"] = new StringValue("standing | jumping") });
        var buttons = Inside(form).OfType<Button>().Where(button => button.Label.Length > 0).ToList();
        IRuntimeValue? told = null;
        form.OnChanged = (_, value) => told = value;

        Assert.Equal([true, false, true], buttons.Select(button => button.Selected));

        buttons[1].OnPressed!();
        Assert.Equal("standing | crouching | jumping", Assert.IsType<StringValue>(told).Value);
    }

    [Fact]
    public void A_vec_is_a_box_a_number()
    {
        var form = Made(new UIFormField("knockback", "knockback", UIFormKind.Vec));
        form.SetValues(new Dictionary<string, IRuntimeValue> { ["knockback"] = new Vector2Value(new Vector2(550, 120)) });
        var boxes = Inside(form).OfType<NumberBox>().ToList();
        IRuntimeValue? told = null;
        form.OnChanged = (_, value) => told = value;

        Assert.Equal([550.0f, 120.0f], boxes.Select(box => box.Value));

        boxes[1].Value = 900;
        boxes[1].OnValueChanged!(900);
        Assert.Equal(new Vector2(550, 900), Assert.IsType<Vector2Value>(told).Value);
    }

    [Fact]
    public void Typing_is_told_about_once_it_stops()
    {
        var form = Made(new UIFormField("input", "input", UIFormKind.Text));
        var box = Inside(form).OfType<TextBox>().Single();
        IRuntimeValue? told = null;
        form.OnChanged = (_, value) => told = value;

        box.OnChanged!("A + B");
        Tick(form, 0.2f);
        Assert.Null(told);

        Tick(form, 0.6f);
        Assert.Equal("A + B", Assert.IsType<StringValue>(told).Value);
    }

    [Fact]
    public void Flags_go_onto_as_many_lines_as_their_words_need()
    {
        var form = Made(new UIFormField("stances", "stances", UIFormKind.Flags, ["standing", "crouching", "jumping", "falling"]));
        var lines = Inside(form).OfType<StackPanel>().Where(stack => stack.Children.Count > 0 && stack.Children.All(child => child is Button)).ToList();

        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.Equal(2, line.Children.Count));
    }
}
