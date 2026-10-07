using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

namespace Horizon.Hex;

// The inspector, one row per property of whatever is selected.
internal sealed partial class HexScene
{
    /* The inspector: one row per property of whatever is selected, each with an editor for the kind of value it is */

    private void RebuildInspector()
    {
        editors.Clear();
        foreach (var child in inspector.Children.ToArray())
            inspector.Remove(child);

        inspectorScroll.Offset = 0;

        if (document.Selected is not { } selected)
        {
            inspector.Add(new Label("select something in the canvas\nor in the layout on the left")
            {
                Anchor = Origin.Left,
                Align = Origin.TopLeft,
                TextScale = EDITOR_TEXT,
                Color = DimColor
            });
            return;
        }

        var nameEditor = new TextBox(document.NameOf(selected))
        {
            Size = new Vector2(EDITOR_WIDTH, EDITOR_HEIGHT),
            TextScale = EDITOR_TEXT,
            MaxLength = 32,
            Padding = new UIEdges(8, 6)
        };
        nameEditor.OnChanged = text =>
        {
            if (document.Rename(selected, text))
            {
                treeDirty = codeDirty = true;
                Say($"renamed to {text}");
            }
            else
            {
                Say($"'{text}' can't be its name: taken, or not a name a variable can have", error: true);
            }
        };
        AddRow("name", nameEditor);
        AddRow("kind", new Label(UIModule.KindOf(selected) ?? selected.GetType().Name)
        {
            Align = Origin.Left,
            TextScale = EDITOR_TEXT,
            Size = new Vector2(EDITOR_WIDTH, 24)
        });

        foreach (var (name, value) in HexDocument.ReadProperties(selected))
        {
            if (CreateEditor(selected, name, value) is { } editor)
                AddRow(name, editor);
        }
    }

    private void AddRow(string name, UIComponent editor)
    {
        // The row itself is the template the inspector names, the editor goes into the room it leaves for one
        var row = layout.Instantiate(inspector.Template, inspector);

        row.Get<Label>("caption").Text = name.Replace('_', ' ');
        row.Get<StackPanel>("editor").Add(editor);
        editors[name] = editor;
    }

    private UIComponent? CreateEditor(UIComponent target, string name, IRuntimeValue value)
    {
        switch (value)
        {
            case NumberValue number when Ranges.ContainsKey(name) || (name == "value" && target is Slider):
            {
                // A slider for getting there quickly and the number next to it for getting there exactly
                var (min, max) = target is Slider ranged && name == "value" ? (ranged.Min, ranged.Max) : Ranges[name];
                var row = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 4 };

                NumberBox exact = null!;
                var slider = row.Add(new Slider
                {
                    Size = new Vector2(EDITOR_WIDTH - 72, EDITOR_HEIGHT - 6),
                    Min = min,
                    Max = max,
                    Value = number.Value
                });
                exact = row.Add(NumberEditor(68, number.Value, changed =>
                {
                    slider.Value = changed;
                    Set(target, name, new NumberValue(changed));
                }));
                exact.DragStep = (max - min) / 100.0f;

                slider.OnChanged = changed =>
                {
                    // To the hundredth, nobody wants 0.3478 seconds
                    changed = MathF.Round(changed, 2);
                    exact.Value = changed;
                    Set(target, name, new NumberValue(changed));
                };
                return row;
            }

            case NumberValue number:
                return NumberEditor(EDITOR_WIDTH, number.Value, changed => Set(target, name, new NumberValue(changed)));

            case BooleanValue boolean:
                return new Selector("no", "yes")
                {
                    Size = new Vector2(EDITOR_WIDTH, EDITOR_HEIGHT),
                    TextScale = EDITOR_TEXT,
                    Value = boolean.Value ? "yes" : "no",
                    OnChanged = chosen => Set(target, name, new BooleanValue(chosen == "yes"))
                };

            case StringValue text when Choices.TryGetValue(name, out var choices):
                return new Dropdown(choices)
                {
                    Size = new Vector2(EDITOR_WIDTH, EDITOR_HEIGHT),
                    TextScale = EDITOR_TEXT,
                    Value = text.Value,
                    OnChanged = chosen => Set(target, name, new StringValue(chosen))
                };

            case StringValue text:
                // A quote can't be written into a script, so one never gets as far as the component
                return new TextBox(text.Value)
                {
                    Size = new Vector2(EDITOR_WIDTH, EDITOR_HEIGHT),
                    TextScale = EDITOR_TEXT,
                    MaxLength = 200,
                    Padding = new UIEdges(8, 6),
                    OnChanged = typed => Set(target, name, new StringValue(typed.Replace('"', '\'')))
                };

            case Vector2Value vector:
            {
                Vector2 current = vector.Value;
                var row = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 4 };

                row.Add(NumberEditor((EDITOR_WIDTH - 4) / 2, current.X, x => { current.X = x; Set(target, name, new Vector2Value(current)); }));
                row.Add(NumberEditor((EDITOR_WIDTH - 4) / 2, current.Y, y => { current.Y = y; Set(target, name, new Vector2Value(current)); }));
                return row;
            }

            case Vector4Value vector:
            {
                // Colours go from 0 to 1, everything else with four numbers is edges in pixels
                bool colour = name is "color" or "tint";
                Vector4 current = vector.Value;
                var row = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 4 };
                var numbers = new NumberBox[4];
                ColorPicker? picker = null;

                for (int i = 0; i < 4; i++)
                {
                    int component = i;
                    numbers[i] = row.Add(NumberEditor((EDITOR_WIDTH - 12) / 4, current[i], changed =>
                    {
                        current[component] = changed;
                        if (picker is not null) picker.Color = current;
                        Set(target, name, new Vector4Value(current));
                    }));
                    numbers[i].DragStep = colour ? 0.02f : 1.0f;
                }

                if (!colour)
                    return row;

                // Picked by eye on top, the numbers it comes to underneath for when they have to be exact
                var both = new StackPanel { Spacing = 4 };
                picker = both.Add(new ColorPicker
                {
                    Size = new Vector2(EDITOR_WIDTH, PICKER_HEIGHT),
                    Color = current,
                    OnChanged = picked =>
                    {
                        current = new Vector4(MathF.Round(picked.X, 3), MathF.Round(picked.Y, 3), MathF.Round(picked.Z, 3), MathF.Round(picked.W, 3));
                        for (int i = 0; i < 4; i++)
                            numbers[i].Value = current[i];

                        Set(target, name, new Vector4Value(current));
                    }
                });
                both.Add(row);
                return both;
            }

            // Handlers are functions, there is nothing to type one into
            default:
                return null;
        }
    }

    private static NumberBox NumberEditor(float width, float value, Action<float> changed) => new(value)
    {
        Size = new Vector2(width, EDITOR_HEIGHT),
        TextScale = EDITOR_TEXT,
        Padding = new UIEdges(6, 6),
        OnValueChanged = changed
    };

    private void Set(UIComponent target, string name, IRuntimeValue value)
    {
        if (HexDocument.Write(target, name, value) is { } problem)
        {
            Say(problem, error: true);
            return;
        }

        codeDirty = true;

        // The tree greys out what is hidden, and the layers are listed by what the components say they are on
        if (name is "visible" or "layer")
            treeDirty = true;

        // An entrance is easier to set up when it is played back every time something about it changes
        if (name.StartsWith("intro") || name == "stagger")
        {
            document.SettleIntros();
            target.PlayIntro();
        }

        // What a container is shown filled with is up to these two
        if (name is "template" or "preview_count" && document.RefreshPreviews() is { } missing)
            Say(missing, error: true);
    }
}
