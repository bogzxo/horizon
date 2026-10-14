using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

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

        // With several things selected the rows are those of the one picked last, and what is changed in them is
        // changed for all of them that have such a thing to change
        if (document.Selection.Count > 1)
        {
            inspector.Add(new Label($"{document.Selection.Count} things selected, a change here goes to\nall of them that have the property")
            {
                Anchor = Origin.Left,
                Align = Origin.TopLeft,
                TextScale = EDITOR_TEXT,
                Color = FolderColor
            });
        }

        var nameEditor = new TextBox(document.NameOf(selected))
        {
            Size = new Vector2(EditorWidth, EDITOR_HEIGHT),
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
        string kind = UIModule.KindOf(selected) ?? selected.GetType().Name;

        // The rows go into folding sections by what they are about, the kind's own after what everything has, so a
        // long list of properties is the part somebody is working on and not a wall of it
        var sections = new Dictionary<string, Collapsible>();
        foreach (string title in SectionOrder)
            sections[title == KIND_SECTION ? kind.Replace('_', ' ') : title] = null!;

        AddRow(Section(sections, SectionOf("name", kind)), "name", nameEditor);
        AddRow(Section(sections, SectionOf("kind", kind)), "kind", new Label(kind)
        {
            Align = Origin.Left,
            TextScale = EDITOR_TEXT,
            Size = new Vector2(EditorWidth, 24)
        });

        foreach (var (name, value) in HexDocument.ReadProperties(selected))
        {
            if (CreateEditor(selected, name, value) is { } editor)
                AddRow(Section(sections, SectionOf(name, kind)), name, editor);
        }
    }

    // The sections of the properties, in the order they come, the one named after the kind standing in for its own
    private const string KIND_SECTION = "*";
    private static readonly string[] SectionOrder = ["general", KIND_SECTION, "placement", "hover", "entrance", "resizing"];

    // Which sections were folded away, by title, kept from one thing selected to the next and the one after
    private readonly HashSet<string> foldedSections = [];

    /// <summary>Helper method to say which section of the properties a property goes in.</summary>
    private static string SectionOf(string name, string kind) => name switch
    {
        "name" or "kind" or "visible" or "enabled" or "layer" or "tooltip" => "general",
        "pos" or "size" or "anchor" or "pivot" or "fill" or "padding" or "scale" or "z" => "placement",
        "resize" or "min_size" or "max_size" => "resizing",
        _ when name.StartsWith("hover") => "hover",
        _ when name.StartsWith("intro") => "entrance",
        _ => kind.Replace('_', ' ')
    };

    /// <summary>
    /// Helper method to find the section a row goes into, making it the first time it is asked for. It is put in its
    /// place among the ones there are by the order they come in, whichever of them happened to be made first.
    /// </summary>
    private Collapsible Section(Dictionary<string, Collapsible> sections, string title)
    {
        if (sections.TryGetValue(title, out var section) && section is not null)
            return section;

        section = new Collapsible(title)
        {
            Fill = UIFill.Horizontal,
            TextScale = EDITOR_TEXT,
            HeaderHeight = 24,
            Spacing = 5,
            Padding = new UIEdges(0, 6, 0, 8),
            Expanded = !foldedSections.Contains(title)
        };
        section.OnToggled = open =>
        {
            if (open) foldedSections.Remove(title);
            else foldedSections.Add(title);
        };

        sections[title] = section;

        // Before the first one made already that comes after it, and after what is said over all of them
        int index = inspector.Children.TakeWhile(child => child is not Collapsible).Count();
        foreach (var (other, made) in sections)
        {
            if (other == title)
                break;
            if (made is not null && made.Parent == inspector)
                index = inspector.Children.ToList().IndexOf(made) + 1;
        }

        inspector.Insert(index, section);
        return section;
    }

    /// <summary>
    /// Helper method to play the entrance of everything in the layout from the start, the way loading it in a
    /// program does. The whole of it and not only what is selected, a delay means nothing without the rest.
    /// </summary>
    private void PlayIntros()
    {
        document.PlayIntros();
        Say("playing the entrances, F5 does it again");
    }

    // The properties that name a piece of art of the skin, the look of anything that has one, a panel's backdrop,
    // what an image shows, the handle of a slider
    private static readonly HashSet<string> ArtProperties = ["style", "background", "region", "handle"];

    // The menu of art that was opened last, for the self-test to find its tiles
    private ArtPicker? artPicker;

    private void AddRow(UIComponent section, string name, UIComponent editor)
    {
        // The row itself is the template the inspector names, the editor goes into the room it leaves for one
        var row = layout.Instantiate(inspector.Template, section);

        row.Get<Label>("caption").Text = name.Replace('_', ' ');
        row.Get<StackPanel>("editor").Add(editor);

        // What the property does, on its name and on whatever is in the editor that has nothing of its own to say
        string help = UIPropertyHelp.Describe(document.Selected is { } shown ? UIModule.KindOf(shown) : null, name);
        row.Get<Label>("caption").Tooltip = help;
        row.Get<StackPanel>("editor").Tooltip = help;
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
                    Size = new Vector2(EditorWidth - 72, EDITOR_HEIGHT - 6),
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
                return NumberEditor(EditorWidth, number.Value, changed => Set(target, name, new NumberValue(changed)));

            case BooleanValue boolean:
                return new Selector("no", "yes")
                {
                    Size = new Vector2(EditorWidth, EDITOR_HEIGHT),
                    TextScale = EDITOR_TEXT,
                    Value = boolean.Value ? "yes" : "no",
                    OnChanged = chosen => Set(target, name, new BooleanValue(chosen == "yes"))
                };

            case StringValue text when Choices.TryGetValue(name, out var choices):
                return new Dropdown(choices)
                {
                    Size = new Vector2(EditorWidth, EDITOR_HEIGHT),
                    TextScale = EDITOR_TEXT,
                    Value = text.Value,
                    OnChanged = chosen => Set(target, name, new StringValue(chosen))
                };

            case StringValue text:
            {
                // A quote can't be written into a script, so one never gets as far as the component
                var box = new TextBox(text.Value)
                {
                    Size = new Vector2(EditorWidth, EDITOR_HEIGHT),
                    TextScale = EDITOR_TEXT,
                    MaxLength = 200,
                    Padding = new UIEdges(8, 6),
                    OnChanged = typed => Set(target, name, new StringValue(typed.Replace('"', '\'')))
                };

                // What names a piece of the skin's art gets a button that shows the lot of it to pick from, the
                // art of the skin the layouts are shown in and not the plain one the editor wears
                if (ArtProperties.Contains(name) && stage.Skin is { } art)
                {
                    box.ButtonLabel = "...";
                    box.Tooltip = "the button shows every piece of art the skin has, to pick one by eye";
                    box.OnButtonPressed = () => artPicker = ArtPicker.Show(chrome, art, box.Text, chosen =>
                    {
                        box.Text = chosen;
                        Set(target, name, new StringValue(chosen));
                        Say($"{name} is {chosen}");
                    });
                }

                return box;
            }

            case Vector2Value vector:
            {
                Vector2 current = vector.Value;
                var row = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 4 };

                // The rest of what is selected only takes the number that was changed, each keeps its other one.
                // Setting the x of five things mustn't put them all at the same y
                row.Add(NumberEditor((EditorWidth - 4) / 2, current.X, x =>
                {
                    current.X = x;
                    Set(target, name, new Vector2Value(current), theirs => theirs is Vector2Value had ? new Vector2Value(had.Value with { X = x }) : null);
                }));
                row.Add(NumberEditor((EditorWidth - 4) / 2, current.Y, y =>
                {
                    current.Y = y;
                    Set(target, name, new Vector2Value(current), theirs => theirs is Vector2Value had ? new Vector2Value(had.Value with { Y = y }) : null);
                }));
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
                    numbers[i] = row.Add(NumberEditor((EditorWidth - 12) / 4, current[i], changed =>
                    {
                        current[component] = changed;
                        if (picker is not null) picker.Color = current;
                        Set(target, name, new Vector4Value(current), theirs =>
                        {
                            if (theirs is not Vector4Value had) return null;

                            Vector4 kept = had.Value;
                            kept[component] = changed;
                            return new Vector4Value(kept);
                        });
                    }));
                    numbers[i].DragStep = colour ? 0.02f : 1.0f;
                }

                if (!colour)
                    return row;

                // Picked by eye on top, the numbers it comes to underneath for when they have to be exact
                var both = new StackPanel { Spacing = 4 };
                picker = both.Add(new ColorPicker
                {
                    Size = new Vector2(EditorWidth, PICKER_HEIGHT),
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

    /// <summary>
    /// Helper method to set a property of what the inspector shows, and of everything else that is selected and has
    /// a property by that name, so a row changed with ten things selected is changed for the ten.
    /// </summary>
    /// <param name="forOthers">
    /// What the rest of the selection gets, worked out of what each of them has now, for a change that is only to a
    /// part of the value (one number of a position). Null from it leaves that one alone, left out they all get the value.
    /// </param>
    private void Set(UIComponent target, string name, IRuntimeValue value, Func<IRuntimeValue, IRuntimeValue?>? forOthers = null)
    {
        if (HexDocument.Write(target, name, value) is { } problem)
        {
            Say(problem, error: true);
            return;
        }

        int others = 0;
        foreach (var other in document.Selection)
        {
            if (other == target || !HexDocument.TryRead(other, name, out var theirs))
                continue;

            IRuntimeValue? wanted = forOthers is null ? value : forOthers(theirs);

            // One that won't have it (a kind of value it doesn't take) is left as it was, the rest still get theirs
            if (wanted is null || HexDocument.Write(other, name, wanted) is not null)
                continue;

            others++;
            if (name.StartsWith("intro") || name == "stagger")
                other.PlayIntro();
        }

        if (others > 0)
            Say($"{name.Replace('_', ' ')} changed for {others + 1} things");

        codeDirty = true;

        // The tree greys out what is hidden, and the layers are listed by what the components say they are on
        if (name is "visible" or "layer")
            treeDirty = true;

        // An entrance is easier to set up when it is played back every time something about it changes
        if (name.StartsWith("intro") || name == "stagger")
        {
            // The others were set off above, settling here would stop them again
            if (others == 0) document.SettleIntros();
            target.PlayIntro();
        }

        // What a container is shown filled with is up to these two
        if (name is "template" or "preview_count" && document.RefreshPreviews() is { } missing)
            Say(missing, error: true);
    }
}
