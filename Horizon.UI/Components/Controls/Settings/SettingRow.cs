using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI.Drawing;

namespace Horizon.UI.Components;

/// <summary>
/// A setting on a <see cref="SettingsPage"/>, its section and name with a reset beside them while it is changed, the
/// sentence of what it does, and what sets it. Lit under the pointer, a line in the accent down its left edge while it
/// is not what it comes as.
/// </summary>
internal sealed class SettingRow : StackPanel
{
    private const float PAD = 10.0f, TYPED_AFTER = 0.6f;

    private readonly SettingsPage page;
    private readonly Button reset;
    private Action<IRuntimeValue?> show = _ => { };
    private bool modified;
    private string? typed;
    private float typedFor;

    public UISetting Setting { get; }

    protected override bool HitTestVisible => true;

    public SettingRow(SettingsPage page, UISetting setting, float width)
    {
        this.page = page;
        Setting = setting;
        Spacing = 4.0f;
        Anchor = Origin.TopLeft;
        Pivot = Origin.TopLeft;
        Padding = new UIEdges(PAD + 4.0f, PAD, PAD, PAD);
        float inner = width - PAD * 2.0f - 4.0f;

        var top = Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Anchor = Origin.Left, Pivot = Origin.Left });
        // Just its name, the section is the heading over it (and over what a search leaves)
        top.Add(new Label { Text = $"[b]{setting.Title}[/b]", TextScale = page.RowTitleScale, Align = Origin.Left, Size = new Vector2(inner - 34.0f, 26.0f) });
        reset = top.Add(new Button
        {
            Icon = "discard",
            Size = new Vector2(26.0f, 26.0f),
            Style = "button_flat",
            Animated = false,
            Tooltip = "Back to what it comes as",
            OnPressed = () => page.ResetPressed(setting.Key)
        });

        if (setting.Help.Length > 0)
            Add(new Label { Text = setting.Help, Wrap = true, TextScale = page.RowBodyScale, Align = Origin.TopLeft, Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(inner, 0.0f) });

        // What sets it and its buttons on one line, a slider with the number it's at beside it
        if (setting.Buttons is { Length: > 0 } || setting.Slider || setting.ButtonsOnly)
            line = Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Anchor = Origin.Left, Pivot = Origin.Left });

        show = setting.ButtonsOnly ? _ => { } : MakeControl(setting, Math.Min(inner, 420.0f));

        for (int i = 0; i < (setting.Buttons?.Length ?? 0); i++)
        {
            int which = i;
            string label = setting.Buttons![i];
            line!.Add(new Button
            {
                Label = label,
                Size = new Vector2(MathF.Max(90.0f, label.Length * 8.5f + 28.0f), SettingsPage.RowControlHeight),
                LabelScale = page.RowBodyScale,
                Animated = false,
                OnPressed = () => page.ButtonPressed(setting.Key, which)
            });
        }
    }

    // Where what sets it goes, a line of its own when it has buttons or is a slider, the row itself otherwise
    private StackPanel? line;

    private T Put<T>(T control) where T : UIComponent => line is not null ? line.Add(control) : Add(control);

    public void Show(IRuntimeValue? value, bool isModified)
    {
        modified = isModified;
        reset.Visible = isModified;
        show(value);
    }

    protected override void Update(float dt)
    {
        if (typed is { } text)
        {
            typedFor += dt;
            if (typedFor >= TYPED_AFTER)
            {
                typed = null;
                page.Changed(Setting.Key, new StringValue(text));
            }
        }

        base.Update(dt);
    }

    protected override void Paint(UIDrawList list)
    {
        // always there, see-through when not lit, or the row's title and help shift along a quad as the pointer comes
        // by and get blended with each other between ticks (ToggleButton has why)
        list.Rect(Bounds, list.Skin.TextColor with { W = IsHovered || HasHoveredChild() ? 0.04f : 0.0f });
        if (modified)
            list.Rect(new UIRect(Bounds.Min, new Vector2(Bounds.Min.X + 3.0f, Bounds.Max.Y)), list.Skin.AccentColor);
        base.Paint(list);
    }

    private bool HasHoveredChild()
    {
        foreach (UIComponent child in ChildSpan)
        {
            if (child.IsHovered)
                return true;
        }

        return false;
    }

    /* What sets it, by its kind, each handing back how to show a value */

    private Action<IRuntimeValue?> MakeControl(UISetting setting, float width) => setting.Kind switch
    {
        UIFormKind.Flag => Toggle(setting),
        UIFormKind.Choice => Choice(setting, width),
        UIFormKind.Number or UIFormKind.Whole when setting.Slider && setting.Most > setting.Least => SliderFor(setting, width),
        UIFormKind.Number or UIFormKind.Whole => Number(setting, width),
        UIFormKind.Flags => Flags(setting, width),
        _ => Text(setting, width)
    };

    // A switch and nothing written beside it, the switch says on or off itself the way a phone's does
    private Action<IRuntimeValue?> Toggle(UISetting setting)
    {
        var toggle = Put(new ToggleButton { Size = new Vector2(56.0f, SettingsPage.RowControlHeight), LabelScale = page.RowBodyScale, Anchor = Origin.Left, Pivot = Origin.Left });
        toggle.OnPressed = () => page.Changed(setting.Key, Values.Bool(toggle.State));
        return value => toggle.State = value is BooleanValue { Value: true };
    }

    private Action<IRuntimeValue?> Choice(UISetting setting, float width)
    {
        var dropdown = Put(new Dropdown(setting.Choices ?? []) { Size = new Vector2(MathF.Min(width, 280.0f), SettingsPage.RowControlHeight), TextScale = page.RowBodyScale, Anchor = Origin.Left, Pivot = Origin.Left });
        dropdown.OnChanged = chosen => page.Changed(setting.Key, new StringValue(chosen));
        return value => dropdown.Value = value is StringValue text ? text.Value : string.Empty;
    }

    private Action<IRuntimeValue?> Number(UISetting setting, float width)
    {
        var box = Put(new NumberBox { Size = new Vector2(MathF.Min(width, 160.0f), SettingsPage.RowControlHeight), DragStep = setting.Step, TextScale = page.RowBodyScale, Anchor = Origin.Left, Pivot = Origin.Left });
        box.OnValueChanged = number =>
        {
            if (setting.Most > setting.Least)
                number = Math.Clamp(number, setting.Least, setting.Most);
            page.Changed(setting.Key, new NumberValue(setting.Kind == UIFormKind.Whole ? MathF.Round(number) : number));
        };
        return value =>
        {
            if (value is NumberValue number)
                box.Value = number.Value;
        };
    }

    // A slider from its least to its most, the number it's at written beside it, told about as it's dragged
    private Action<IRuntimeValue?> SliderFor(UISetting setting, float width)
    {
        var slider = Put(new Slider { Min = setting.Least, Max = setting.Most, Size = new Vector2(MathF.Min(width, 260.0f), SettingsPage.RowControlHeight), Anchor = Origin.Left, Pivot = Origin.Left });
        var said = Put(new Label { TextScale = page.RowBodyScale, Align = Origin.Left, Size = new Vector2(70.0f, SettingsPage.RowControlHeight) });
        bool showing = false;
        slider.OnChanged = number =>
        {
            said.Text = Said(setting, number);
            if (!showing)
                page.Changed(setting.Key, new NumberValue(setting.Kind == UIFormKind.Whole ? MathF.Round(number) : number));
        };
        return value =>
        {
            if (value is not NumberValue number)
                return;
            showing = true;
            slider.Value = number.Value;
            showing = false;
            said.Text = Said(setting, number.Value);
        };
    }

    // How a slider's number is written, a share (nothing to all of it) as a percentage, anything else as it is. Anything up
    // to 1 was a share, and the caret's glide of a few hundredths of a second read 5%
    private static string Said(UISetting setting, float number) =>
        setting.Least == 0.0f && setting.Most == 1.0f ? $"{number * 100.0f:0}%" : setting.Step >= 1.0f ? $"{number:0}" : $"{number:0.###}";

    // A button a choice, lit when it is one of them, for a list of what there is (plugins, profiles)
    private Action<IRuntimeValue?> Flags(UISetting setting, float width)
    {
        var line = Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 4.0f, Anchor = Origin.Left, Pivot = Origin.Left });
        var buttons = new List<Button>();
        float used = 0.0f;
        foreach (string choice in setting.Choices ?? [])
        {
            float wide = MathF.Max(70.0f, choice.Length * 9.0f + 20.0f);
            if (used + wide > width && buttons.Count > 0)
            {
                line = Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 4.0f, Anchor = Origin.Left, Pivot = Origin.Left });
                used = 0.0f;
            }

            used += wide + 4.0f;
            var button = line.Add(new Button { Label = choice, Size = new Vector2(wide, SettingsPage.RowControlHeight), Style = "button_flat", Animated = false, LabelScale = page.RowBodyScale });
            button.OnPressed = () =>
            {
                button.Selected = !button.Selected;
                page.Changed(setting.Key, new ListValue([.. buttons.Where(on => on.Selected).Select(on => (IRuntimeValue)new StringValue(on.Label))]));
            };
            buttons.Add(button);
        }

        if (buttons.Count == 0)
            line.Add(new Label { Text = "[color:dim]nothing to pick from[/color]", TextScale = page.RowBodyScale, Size = new Vector2(200.0f, SettingsPage.RowControlHeight), Align = Origin.Left });

        return value =>
        {
            var on = value is ListValue list ? list.Items.OfType<StringValue>().Select(item => item.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
            foreach (Button button in buttons)
                button.Selected = on.Contains(button.Label);
        };
    }

    private Action<IRuntimeValue?> Text(UISetting setting, float width)
    {
        var box = Put(new TextBox { Size = new Vector2(width, SettingsPage.RowControlHeight), TextScale = page.RowBodyScale, Anchor = Origin.Left, Pivot = Origin.Left });
        box.OnChanged = text =>
        {
            typed = text;
            typedFor = 0.0f;
        };
        box.OnSubmitted = text =>
        {
            typed = null;
            page.Changed(setting.Key, new StringValue(text));
        };
        return value =>
        {
            if (!box.IsFocused)
                box.Text = value is StringValue text ? text.Value : string.Empty;
        };
    }
}
