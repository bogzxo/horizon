using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.Rendering;

namespace Horizon.UI.Components;

// The rows of a form, a name, what sets the field (made by its kind) and the x that unsets it.
public partial class FormPanel
{
    private const float ROW_HEIGHT = 26.0f, CLEAR_WIDTH = 24.0f, GAP = 6.0f, DEFAULT_WIDTH = 320.0f;

    // The least a button of a field of flags is given, wide enough for a word like "crouching" at a form's size
    private const float FLAG_WIDTH = 66.0f;

    // How long typing in a text field has to stop before it is told about
    private const float TYPED_AFTER = 0.6f;

    /// <summary>A row, what shows a value in it (null for unset) and what it was made of.</summary>
    private sealed record Row(UIFormField Field, Label Caption, Button Clear, Action<IRuntimeValue?> Show);

    private readonly Dictionary<string, Row> rows = new(StringComparer.Ordinal);

    // A text field being typed into, and how long ago the last key was
    private (UIFormField Field, string Text)? typing;
    private float typedFor;

    private void BuildRows()
    {
        foreach (UIComponent child in ChildSpan.ToArray())
            Remove(child);
        rows.Clear();

        float width = RowWidth > 0.0f ? RowWidth : DEFAULT_WIDTH;
        float editor = MathF.Max(60.0f, width - CaptionWidth - CLEAR_WIDTH - GAP * 2.0f);
        var sections = new Dictionary<string, UIComponent>(StringComparer.Ordinal);

        foreach (UIFormField field in fields)
        {
            UIComponent into = field.Group.Length == 0 ? this : SectionOf(field.Group, width, sections);
            var row = into.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = GAP, Anchor = Origin.Left, Pivot = Origin.Left });

            var caption = row.Add(new Label { Text = field.Title, Size = new Vector2(CaptionWidth, ROW_HEIGHT), Align = Origin.Left, TextScale = TextScale, Tooltip = field.Help });
            Action<IRuntimeValue?> show = MakeEditor(field, row, editor);
            var clear = row.Add(new Button
            {
                Icon = "close",
                Size = new Vector2(CLEAR_WIDTH, ROW_HEIGHT),
                Style = "button_flat",
                Animated = false,
                Tooltip = field.Unset.Length > 0 ? $"unset, {field.Unset}" : "unset, back to what it is when it isn't said",
                OnPressed = () => Cleared(field)
            });

            rows[field.Key] = new Row(field, caption, clear, show);
        }

        ShowAll();
    }

    private UIComponent SectionOf(string group, float width, Dictionary<string, UIComponent> sections)
    {
        if (!sections.TryGetValue(group, out UIComponent? section))
        {
            section = Add(new Collapsible(group) { Size = new Vector2(width, 0.0f), TextScale = TextScale, Spacing = 4.0f });
            sections[group] = section;
        }

        return section;
    }

    private void ShowAll()
    {
        foreach (string key in rows.Keys)
            ShowRow(key);
    }

    /// <summary>A row showing what its field is now, its name dimmed and no x while it isn't set.</summary>
    private void ShowRow(string key)
    {
        if (!rows.TryGetValue(key, out Row? row))
            return;

        IRuntimeValue? value = values.GetValueOrDefault(key);
        row.Show(value ?? row.Field.Default);
        row.Caption.Color = value is null ? new Vector4(1.0f, 1.0f, 1.0f, 0.5f) : null;
        row.Clear.Visible = value is not null && !row.Field.ReadOnly;
    }

    /* What sets a field, by its kind. Each puts its parts in the row and hands back how to show a value */

    private Action<IRuntimeValue?> MakeEditor(UIFormField field, StackPanel row, float width) => field.Kind switch
    {
        UIFormKind.Number or UIFormKind.Whole => NumberEditor(field, row, width),
        UIFormKind.Flag => FlagEditor(field, row, width),
        UIFormKind.Choice => ChoiceEditor(field, row, width),
        UIFormKind.Flags => FlagsEditor(field, row, width),
        UIFormKind.Vec => VecEditor(field, row, width),
        UIFormKind.Pairs => PairsEditor(field, row, width),
        _ => TextEditor(field, row, width)
    };

    private Action<IRuntimeValue?> NumberEditor(UIFormField field, StackPanel row, float width)
    {
        var box = row.Add(new NumberBox { Size = new Vector2(width, ROW_HEIGHT), DragStep = field.Step, Enabled = !field.ReadOnly, TextScale = TextScale, Placeholder = field.Unset });
        box.OnValueChanged = number => Changed(field, new NumberValue(field.Kind == UIFormKind.Whole ? MathF.Round(number) : number));

        return value =>
        {
            if (value is NumberValue number)
                box.Value = number.Value;
            else
                box.Empty = true;
        };
    }

    private Action<IRuntimeValue?> FlagEditor(UIFormField field, StackPanel row, float width)
    {
        var toggle = row.Add(new ToggleButton { Size = new Vector2(width, ROW_HEIGHT), Enabled = !field.ReadOnly, LabelScale = TextScale, Anchor = Origin.Left, Pivot = Origin.Left });
        toggle.OnPressed = () => Changed(field, Values.Bool(toggle.State));

        return value =>
        {
            toggle.State = value is BooleanValue { Value: true };
            toggle.Label = value is null && field.Unset.Length > 0 ? field.Unset : toggle.State ? "yes" : "no";
        };
    }

    private Action<IRuntimeValue?> ChoiceEditor(UIFormField field, StackPanel row, float width)
    {
        string[] choices = field.Choices ?? [];
        var dropdown = row.Add(new Dropdown(field.Unset.Length > 0 ? ["", .. choices] : choices) { Size = new Vector2(width, ROW_HEIGHT), Enabled = !field.ReadOnly, TextScale = TextScale });
        dropdown.OnChanged = chosen =>
        {
            if (chosen.Length == 0)
                Cleared(field);
            else
                Changed(field, new StringValue(chosen));
        };

        return value => dropdown.Value = value is StringValue text ? text.Value : string.Empty;
    }

    // A button a choice, lit when it's one of them, which fits the words of four stances in a row where toggles didn't
    private Action<IRuntimeValue?> FlagsEditor(UIFormField field, StackPanel row, float width)
    {
        // As many to a line as fit with room for a word like "crouching", the rest on the lines under
        string[] choices = field.Choices ?? [];
        int across = Math.Clamp((int)((width + 2.0f) / (FLAG_WIDTH + 2.0f)), 1, Math.Max(1, choices.Length));
        float each = (width - 2.0f * (across - 1)) / across;
        var lines = row.Add(new StackPanel { Spacing = 2.0f });
        StackPanel stack = null!;
        var buttons = new List<Button>();

        foreach (string choice in choices)
        {
            if (buttons.Count % across == 0)
                stack = lines.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 2.0f, Anchor = Origin.Left, Pivot = Origin.Left });

            var button = stack.Add(new Button { Label = choice, Size = new Vector2(each, ROW_HEIGHT), Style = "button_flat", Animated = false, LabelScale = TextScale, Enabled = !field.ReadOnly });
            button.OnPressed = () =>
            {
                button.Selected = !button.Selected;
                Changed(field, new StringValue(string.Join(" | ", buttons.Where(on => on.Selected).Select(on => on.Label))));
            };
            buttons.Add(button);
        }

        return value =>
        {
            var on = (value as StringValue?)?.Value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                ?? (value is ListValue list ? [.. list.Items.Select(item => item.ToString() ?? string.Empty)] : []);
            foreach (Button button in buttons)
                button.Selected = on.Contains(button.Label, StringComparer.OrdinalIgnoreCase);
        };
    }

    private Action<IRuntimeValue?> VecEditor(UIFormField field, StackPanel row, float width)
    {
        int count = Math.Clamp(field.Components, 2, 4);
        var stack = row.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 4.0f });
        var boxes = new NumberBox[count];
        float each = (width - 4.0f * (count - 1)) / count;

        for (int i = 0; i < count; i++)
        {
            boxes[i] = stack.Add(new NumberBox { Size = new Vector2(each, ROW_HEIGHT), DragStep = field.Step, Enabled = !field.ReadOnly, TextScale = TextScale });
            boxes[i].OnValueChanged = _ =>
            {
                Span<float> components = stackalloc float[count];
                for (int c = 0; c < count; c++)
                    components[c] = boxes[c].Value;
                Changed(field, Values.Vector(components));
            };
        }

        return value =>
        {
            Span<float> components = stackalloc float[4];
            if (value is null || !Values.TryGetComponents(value, components, out _))
                components.Clear();
            for (int i = 0; i < count; i++)
                boxes[i].Value = components[i];
        };
    }

    private Action<IRuntimeValue?> TextEditor(UIFormField field, StackPanel row, float width)
    {
        var box = row.Add(new TextBox { Size = new Vector2(width, ROW_HEIGHT), Enabled = !field.ReadOnly, TextScale = TextScale, Placeholder = field.Unset });
        box.OnChanged = text =>
        {
            typing = (field, text);
            typedFor = 0.0f;
        };
        box.OnSubmitted = text =>
        {
            typing = null;
            Typed(field, text);
        };

        return value =>
        {
            // What is being typed is left alone, it's told about once it stops
            if (!box.IsFocused)
                box.Text = value is StringValue text ? text.Value : value is null ? string.Empty : value.ToString() ?? string.Empty;
        };
    }

    // Pairs on one line, "*.orig = csharp, *.log = plaintext", told about when Enter is pressed. A form is a narrow
    // thing, the settings page has a line a pair with buttons of its own
    private Action<IRuntimeValue?> PairsEditor(UIFormField field, StackPanel row, float width)
    {
        var box = row.Add(new TextBox { Size = new Vector2(width, ROW_HEIGHT), Enabled = !field.ReadOnly, TextScale = TextScale, Placeholder = field.Unset, MaxLength = 4096 });
        box.OnSubmitted = text =>
        {
            if (text.Trim().Length == 0)
                Cleared(field);
            else
                Changed(field, UIFormPairs.Parse(text));
        };

        return value =>
        {
            if (!box.IsFocused)
                box.Text = UIFormPairs.Line(value);
        };
    }

    // Typing that has stopped for a moment is told about, as if it had been entered
    private void SendTyped(float dt)
    {
        if (typing is not { } typed)
            return;

        typedFor += dt;
        if (typedFor < TYPED_AFTER)
            return;

        typing = null;
        Typed(typed.Field, typed.Text);
    }

    private void Typed(UIFormField field, string text)
    {
        if (text.Length == 0)
            Cleared(field);
        else
            Changed(field, new StringValue(text));
    }
}
