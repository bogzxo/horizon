using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI.Drawing;

namespace Horizon.UI.Components;

// What sets a row, by its kind, each handing back how to show a value
internal sealed partial class SettingRow
{
    private Action<IRuntimeValue?> MakeControl(UISetting setting, float width) => setting.Kind switch
    {
        UIFormKind.Flag => Toggle(setting),
        UIFormKind.Choice => Choice(setting, width),
        UIFormKind.Number or UIFormKind.Whole when setting.Slider && setting.Most > setting.Least => SliderFor(setting, width),
        UIFormKind.Number or UIFormKind.Whole => Number(setting, width),
        UIFormKind.Flags => Flags(setting, width),
        UIFormKind.Pairs => Pairs(setting, width),
        UIFormKind.List => ListOf(setting, width),
        _ => Text(setting, width)
    };

    // A switch and nothing written beside it, the switch says on or off itself the way a phone's does
    private Action<IRuntimeValue?> Toggle(UISetting setting)
    {
        // In the column down the right as wide as the switch it draws from its left (a pill 1.8 times as wide as it's
        // tall, 0.7 of the row's control), so the switch ends on the column's edge with the dropdowns and the boxes
        float wide = sideways ? MathF.Round(MathF.Round(page.RowControlHeight * 0.7f) * 1.8f) : 56.0f;
        var toggle = Put(new RowToggle(this) { Size = new Vector2(wide, page.RowControlHeight), LabelScale = page.RowBodyScale, Anchor = Origin.Left, Pivot = Origin.Left });
        toggle.OnPressed = () => page.Changed(setting.Key, Values.Bool(toggle.State));
        return value => toggle.State = value is BooleanValue { Value: true };
    }

    private Action<IRuntimeValue?> Choice(UISetting setting, float width)
    {
        // A choice says itself in words, its label when it has one ("Fast-forward only" for ff_only) and otherwise with a
        // capital, "None" and "Horizon", whatever its value is written as. They were lowercase beside the rest of the
        // page's words, and ff_only read "Ff only"
        // The whole of the column down the right, so its right end is where every other row's control ends
        var dropdown = Put(new RowDropdown(this, setting.Choices ?? []) { Size = new Vector2(width, page.RowControlHeight), TextScale = page.RowBodyScale, Anchor = Origin.Left, Pivot = Origin.Left, OptionText = setting.LabelOf });

        // What the one picked does, on a line under its sentence the way VS Code says what a value of a setting is, and
        // only for a setting whose choices say so. The colour of the sentence over it, dim as well it was hard to read
        Label? said = null;
        if (setting.ChoiceHelp is { Length: > 0 })
        {
            said = text.Add(new Label { Wrap = true, TextScale = page.RowBodyScale, Align = Origin.TopLeft, Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(helpWidth, 0.0f) });
            faded.Add(said);
        }

        void Describe(string chosen)
        {
            choiceText = setting.LabelOf(chosen);
            choiceHelp = setting.HelpOf(chosen);
            if (said is null)
                return;

            said.Text = choiceHelp.Length > 0 ? UIRichText.Literal(choiceHelp) : string.Empty;
            said.Visible = choiceHelp.Length > 0;
        }

        dropdown.OnChanged = chosen =>
        {
            Describe(chosen);
            page.Changed(setting.Key, new StringValue(chosen));
        };
        return value =>
        {
            dropdown.Value = value is StringValue text ? text.Value : string.Empty;
            Describe(dropdown.Value);
        };
    }

    // What its dropdown says and the line under it, for a test
    private string? choiceText, choiceHelp;

    /// <summary>What a choice's dropdown says now, null for a row that isn't a choice.</summary>
    public string? ChoiceText => choiceText;

    /// <summary>What the line under a choice says of the one picked, null for a row that isn't a choice.</summary>
    public string? ChoiceHelpText => choiceHelp;

    /// <summary>A choice as a person reads it, its first letter a capital and its underscores spaces, "solarized_dark" as "Solarized dark".</summary>
    public static string Capitalised(string choice)
    {
        string spaced = choice.Replace('_', ' ');
        return spaced.Length > 0 && char.IsLower(spaced[0]) ? char.ToUpperInvariant(spaced[0]) + spaced[1..] : spaced;
    }

    private Action<IRuntimeValue?> Number(UISetting setting, float width)
    {
        // In the column down the right the box is a fixed width against its right end with what it can be and its unit
        // in front of it, under a sentence they come after it
        if (sideways)
            Bounded(setting);
        var box = Put(new NumberBox { Size = new Vector2(MathF.Min(width, sideways ? NumberWidth : 160.0f), page.RowControlHeight), DragStep = setting.Step, TextScale = page.RowBodyScale, Anchor = Origin.Left, Pivot = Origin.Left });
        if (!sideways)
            Bounded(setting);
        if (setting.Most > setting.Least)
            box.Tooltip = SettingsTexts.Fill(page.Texts.Between, ("least", Invariant($"{setting.Least:0.##}")), ("most", Invariant($"{setting.Most:0.##}")));

        box.TakeKey = key => Stepped(box, setting, key) || Offer(box, key);
        box.OnValueChanged = number => page.Changed(setting.Key, new NumberValue(Kept(setting, number)));
        return value =>
        {
            if (value is NumberValue number)
                box.Value = number.Value;
        };
    }

    // A number as the setting keeps it, between its least and its most when it has them and whole for a whole one
    private static float Kept(UISetting setting, float number)
    {
        if (setting.Most > setting.Least)
            number = Math.Clamp(number, setting.Least, setting.Most);
        return setting.Kind == UIFormKind.Whole ? MathF.Round(number) : number;
    }

    // What a number between its least and its most can be, dim beside its box (1-16) so it's plain without asking, and
    // its unit (px, ms) so a bare 14 says 14 of what. Nothing for a number with neither
    private void Bounded(UISetting setting)
    {
        string range = setting.Most > setting.Least ? RangeOf(setting) : string.Empty;
        string said = string.Join(' ', new[] { range, setting.Unit }.Where(part => part.Length > 0));
        if (said.Length > 0)
            Put(new Label { Text = $"[color:dim]{UIRichText.Literal(said)}[/color]", TextScale = page.RowBodyScale * 0.9f, Align = Origin.Left, Size = new Vector2(0.0f, page.RowControlHeight) });
    }

    /// <summary>What a number from its least to its most is said as after its box, "1-16". A plain dash, the fonts haven't all got the long one.</summary>
    public static string RangeOf(UISetting setting) => Invariant($"{setting.Least:0.##}-{setting.Most:0.##}");

    // A slider from its least to its most, the number it's at written beside it, told about as it's dragged. In the
    // column down the right the number is in front of it and the slider takes the rest of the column, so it ends where
    // every other row's control does
    private Action<IRuntimeValue?> SliderFor(UISetting setting, float width)
    {
        const float SAID = 70.0f;
        Label? before = sideways ? Put(new Label { TextScale = page.RowBodyScale, Align = Origin.Right, Size = new Vector2(SAID, page.RowControlHeight) }) : null;
        float wide = sideways ? width - SAID - 8.0f : MathF.Min(width - SAID - 8.0f, 260.0f);
        var slider = Put(new RowSlider(this) { Min = setting.Least, Max = setting.Most, Size = new Vector2(wide, page.RowControlHeight), Anchor = Origin.Left, Pivot = Origin.Left });
        var said = before ?? Put(new Label { TextScale = page.RowBodyScale, Align = Origin.Left, Size = new Vector2(SAID, page.RowControlHeight) });
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

    /// <summary>
    /// How a slider's number is written, a share (nothing to all of it) as a percentage, seconds as milliseconds under
    /// one, and anything else as it is with its unit after. Anything up to 1 was a share, and the caret's glide of a few
    /// hundredths of a second read 5%, then 0.036 with nothing to say what of.
    /// </summary>
    public static string Said(UISetting setting, float number)
    {
        if (setting.Unit == "s")
            return number < 1.0f ? Invariant($"{number * 1000.0f:0} ms") : Invariant($"{number:0.#} s");
        if (setting.Least == 0.0f && setting.Most == 1.0f && setting.Unit.Length == 0)
            return Invariant($"{number * 100.0f:0}%");

        string written = setting.Step >= 1.0f ? Invariant($"{number:0}") : Invariant($"{number:0.##}");
        return setting.Unit.Length == 0 ? written : setting.Unit == "×" ? written + "×" : $"{written} {setting.Unit}";
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    // A button a choice, lit when it is one of them, for a list of what there is (plugins, profiles)
    private Action<IRuntimeValue?> Flags(UISetting setting, float width)
    {
        var line = Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 4.0f, Anchor = Origin.Left, Pivot = Origin.Left });
        faded.Add(line);
        var buttons = new List<Button>();
        float used = 0.0f;
        foreach (string choice in setting.Choices ?? [])
        {
            float wide = MathF.Max(70.0f, choice.Length * 9.0f + 20.0f);
            if (used + wide > width && buttons.Count > 0)
            {
                line = Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 4.0f, Anchor = Origin.Left, Pivot = Origin.Left });
                faded.Add(line);
                used = 0.0f;
            }

            used += wide + 4.0f;
            var button = line.Add(new RowButton(this) { Label = choice, Size = new Vector2(wide, page.RowControlHeight), Style = "button_flat", Animated = false, LabelScale = page.RowBodyScale });
            button.OnPressed = () =>
            {
                button.Selected = !button.Selected;
                page.Changed(setting.Key, new ListValue([.. buttons.Where(on => on.Selected).Select(on => (IRuntimeValue)new StringValue(on.Label))]));
            };
            buttons.Add(button);
        }

        if (buttons.Count == 0)
            line.Add(new Label { Text = $"[color:dim]{UIRichText.Literal(page.Texts.NothingToPick)}[/color]", TextScale = page.RowBodyScale, Size = new Vector2(200.0f, page.RowControlHeight), Align = Origin.Left });

        return value =>
        {
            var on = value is ListValue list ? list.Items.OfType<StringValue>().Select(item => item.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
            foreach (Button button in buttons)
                button.Selected = on.Contains(button.Label);
        };
    }

    private Action<IRuntimeValue?> Text(UISetting setting, float width)
    {
        var box = Put(new TextBox { Size = new Vector2(width, page.RowControlHeight), TextScale = page.RowBodyScale, Anchor = Origin.Left, Pivot = Origin.Left, Placeholder = setting.Placeholder });
        box.TakeKey = key => Offer(box, key);

        // One that's only read is there to select and copy, as long as it likes, with nothing to tell anybody about
        if (setting.ReadOnly)
        {
            box.ReadOnly = true;
            box.MaxLength = 4096;
            return value => box.Text = value switch
            {
                StringValue text => text.Value,
                NumberValue number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                null or NullValue => string.Empty,
                _ => value.ToString() ?? string.Empty
            };
        }

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
