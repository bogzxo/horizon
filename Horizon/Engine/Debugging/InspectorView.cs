using System.Collections;
using System.Globalization;
using System.Numerics;

using Horizon.Core;
using Horizon.Graphics;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

using Button = Horizon.UI.Components.Button;

namespace Horizon.Engine.Debugging;

/// <summary>
/// What the selected thing is made of, a row a field, and every row that can be changed is changed right here
/// while the game runs. A number is typed or dragged (and gets a slider if it says what it goes between, see
/// <see cref="InspectAttribute"/>), a yes or no is ticked, a word is typed, one of a few is picked from a list,
/// two to four numbers get a box each and a colour gets a picker as well. Anything with an inside of its own
/// (the ambient occlusion of a renderer, the lights of a scene, a list) opens up into the same thing one level
/// down, with the way back at the top.
/// <para>
/// The rows are put together when something is selected and the numbers in them are read again a few times a
/// second, so what the game changes by itself is seen changing. All of it happens on the simulation thread, in
/// the update of the UI, which is between two updates of the game, the same moment the game would set the field
/// itself. What only the render thread may touch is not made any safer by being set from here.
/// </para>
/// </summary>
internal sealed class InspectorView : StackPanel
{
    public const float WIDTH = 344.0f;

    private const float LABEL_WIDTH = 132.0f;
    private const float EDITOR_WIDTH = 204.0f;
    private const float ROW_HEIGHT = 24.0f;
    private const float GAP = 4.0f;
    private const float PICKER_HEIGHT = 112.0f;
    private const float PREVIEW_HEIGHT = 190.0f;
    private const float REFRESH = 0.12f;

    // How many things of a list get a row, the rest are said to be there and that is all
    private const int MOST_ITEMS = 64;

    private static readonly UIEdges BoxPadding = new(6.0f, 4.0f);

    private readonly record struct Level(object Target, string Name, Action<object>? WriteBack);

    private readonly SkylineDebugger suite;
    private readonly List<Level> path = [];
    private readonly List<Action> refreshers = [];

    private float timer;
    private bool dirty = true;

    // How long the list that is shown was when its rows were made, to notice it growing or shrinking
    private int shownItems = -1;

    public InspectorView(SkylineDebugger suite)
    {
        this.suite = suite;
        Spacing = GAP;
        Padding = new UIEdges(8.0f);
    }

    /// <summary>What is shown right now, the innermost thing that was opened.</summary>
    public object? Target => path.Count > 0 ? path[^1].Target : null;

    /// <summary>Shows something from its top, forgetting whatever was opened up before.</summary>
    public void Show(object? target)
    {
        path.Clear();
        if (target is not null)
            path.Add(new Level(target, Describe(target), null));

        dirty = true;
    }

    private void Open(object value, string name, Action<object>? writeBack)
    {
        path.Add(new Level(value, name, writeBack));
        dirty = true;
    }

    private void Back()
    {
        if (path.Count > 1)
            path.RemoveAt(path.Count - 1);

        dirty = true;
    }

    private static string Describe(object target) => target switch
    {
        Entity { Name.Length: > 0 } entity => entity.Name,
        Horizon.Core.Components.IGameComponent { Name.Length: > 0 } component => component.Name,
        GpuResource { Name.Length: > 0 } resource => DebugStyle.FileName(resource.Name),
        _ => Inspectable.NameOf(target.GetType())
    };

    protected override void Update(float dt)
    {
        // Something that is gone is not looked at any more, reading a dead entity proves nothing
        if (path.Count > 0 && path[0].Target is Entity { IsDisposed: true })
        {
            path.Clear();
            dirty = true;
        }

        if (dirty)
        {
            dirty = false;
            Rebuild();
        }

        if ((timer -= dt) > 0.0f)
            return;

        timer = REFRESH;
        foreach (Action refresh in refreshers)
            refresh();

        if (shownItems >= 0 && Target is IEnumerable sequence && CountOf(sequence) != shownItems)
            dirty = true;
    }

    private UISkin? Skin => suite.Skin;

    private Label Text(string text, Vector4? colour = null) => new(text)
    {
        Anchor = Origin.Left,
        Align = Origin.Left,
        Color = colour
    };

    private void Rebuild()
    {
        refreshers.Clear();
        shownItems = -1;

        foreach (var child in Children.ToArray())
            Remove(child);

        if (Parent is ScrollPanel scroll)
            scroll.Offset = 0.0f;

        if (Target is not { } target)
        {
            Add(Text("nothing selected", DebugStyle.Dim));
            Add(Text("pick something in the scene tree,\nor a texture in the content drawer", DebugStyle.Dim));
            return;
        }

        Level level = path[^1];
        Type type = target.GetType();

        if (path.Count > 1)
        {
            Add(new Button(DebugStyle.Fit(Skin, "< " + path[^2].Name, WIDTH - 40.0f))
            {
                Anchor = Origin.Left,
                Size = new Vector2(0.0f, ROW_HEIGHT),
                OnPressed = Back
            });
        }

        Add(Text(DebugStyle.Fit(Skin, level.Name, WIDTH), Skin?.AccentColor));
        Add(Text(DebugStyle.Fit(Skin, type.Namespace is { Length: > 0 } space ? $"{Inspectable.NameOf(type)}   {space}" : Inspectable.NameOf(type), WIDTH), DebugStyle.Dim));

        // A picture is looked at before its numbers are read
        Texture? picture = target switch
        {
            Texture texture => texture,
            RenderTarget renderTarget => renderTarget.Color,
            _ => null
        };
        if (picture is not null && SkylineDebugger.CanShow(picture))
            Add(new TexturePreview(picture) { Anchor = Origin.Left, Size = new Vector2(WIDTH, PREVIEW_HEIGHT) });

        if (target is IEnumerable sequence and not string)
            AddItems(sequence);

        Type? group = null;
        foreach (InspectedMember member in Inspectable.MembersOf(type))
        {
            // What it has of its own comes first, then what it got from what it is derived from, each under its name
            if (member.DeclaredBy != group)
            {
                group = member.DeclaredBy;
                Add(new Spacer { Size = new Vector2(1.0f, 4.0f) });
                Add(Text(Inspectable.NameOf(group), DebugStyle.Dim with { W = 0.75f }));
                Add(new Divider { Fill = UIFill.Horizontal, Color = DebugStyle.Edge });
            }

            InspectedMember captured = member;
            AddRow(
                member.Name,
                member.Type,
                () => captured.Get(target),
                member.CanWrite
                    ? value =>
                    {
                        captured.Set!(target, value);

                        // A struct is a copy in a box, changing the box changes nothing until it is put back
                        level.WriteBack?.Invoke(target);
                    }
                    : null,
                member.Hint);
        }
    }

    private void AddItems(IEnumerable sequence)
    {
        int index = 0;
        try
        {
            foreach (object? item in sequence)
            {
                if (index == MOST_ITEMS)
                {
                    Add(Text($"... and {CountOf(sequence) - MOST_ITEMS} more", DebugStyle.Dim));
                    break;
                }

                object? held = item;
                AddRow($"[{index}]", item?.GetType() ?? typeof(object), () => held, null, null);
                index++;
            }
        }
        catch (Exception problem)
        {
            Add(Text(DebugStyle.Fit(Skin, $"couldn't be listed, {problem.Message}", WIDTH), DebugStyle.Bad));
        }

        shownItems = CountOf(sequence);
        if (shownItems == 0)
            Add(Text("empty", DebugStyle.Dim));
    }

    private static int CountOf(IEnumerable sequence)
    {
        if (sequence is ICollection collection)
            return collection.Count;

        int count = 0;
        try
        {
            foreach (object? _ in sequence)
                count++;
        }
        catch (Exception)
        {
            // Changed under us by another thread, the next look gets it
        }

        return count;
    }

    /// <summary>Helper method to read a value, which for a property is running somebody's code and can go wrong.</summary>
    private static object? Read(Func<object?> get, out string? problem)
    {
        try
        {
            problem = null;
            return get();
        }
        catch (Exception thrown)
        {
            problem = (thrown.InnerException ?? thrown).Message;
            return null;
        }
    }

    /// <summary>Helper method to write a value and say so in the bar when the thing it was written to won't have it.</summary>
    private void Write(Action<object?> set, object? value, string name)
    {
        try
        {
            set(value);
        }
        catch (Exception thrown)
        {
            suite.Say($"{name} wouldn't take that, {(thrown.InnerException ?? thrown).Message}", error: true);
        }
    }

    private void AddRow(string name, Type type, Func<object?> get, Action<object?>? set, InspectAttribute? hint)
    {
        Type bare = Inspectable.Underlying(type);
        object? now = Read(get, out string? problem);

        var row = new StackPanel { Direction = UIDirection.Horizontal, Spacing = GAP, Anchor = Origin.Left };
        row.Add(new Label(DebugStyle.Fit(Skin, name, LABEL_WIDTH - 4.0f))
        {
            Size = new Vector2(LABEL_WIDTH, ROW_HEIGHT),
            Align = Origin.Left,
            Color = set is null ? DebugStyle.Dim : null,
            Tooltip = $"{Inspectable.NameOf(type)} {name}{(set is null ? ", read only" : string.Empty)}"
        });

        if (problem is not null)
        {
            row.Add(ReadOnly(() => $"threw, {problem}", DebugStyle.Bad, live: false));
            Add(row);
            return;
        }

        // Three or four numbers that are a colour get a picker under their row, folded away until the swatch is clicked
        if (set is not null && now is not null && (bare == typeof(Vector4) || bare == typeof(Vector3)) && Inspectable.IsColour(name, hint))
        {
            AddColour(row, name, bare, get, set);
            return;
        }

        row.Add(Editor(name, bare, now, get, set, hint));
        Add(row);
    }

    private UIComponent Editor(string name, Type bare, object? now, Func<object?> get, Action<object?>? set, InspectAttribute? hint)
    {
        if (set is not null && now is not null)
        {
            if (bare == typeof(bool))
            {
                var toggle = new ToggleButton(string.Empty, (bool)now);
                toggle.OnPressed = () => Write(set, toggle.State, name);
                refreshers.Add(() =>
                {
                    if (!toggle.IsPressed && Read(get, out _) is bool state) toggle.State = state;
                });
                return toggle;
            }

            if (Inspectable.IsNumber(bare))
                return Number(name, bare, now, get, set, hint);

            if (bare == typeof(string))
            {
                var box = new TextBox((string)now)
                {
                    Size = new Vector2(EDITOR_WIDTH, ROW_HEIGHT),
                    Padding = BoxPadding,
                    MaxLength = 512,
                    OnChanged = typed => Write(set, typed, name)
                };
                refreshers.Add(() =>
                {
                    if (!box.IsFocused && Read(get, out _) is string text) box.Text = text;
                });
                return box;
            }

            if (bare.IsEnum && !bare.IsDefined(typeof(FlagsAttribute), false))
            {
                var dropdown = new Dropdown(Enum.GetNames(bare))
                {
                    Size = new Vector2(EDITOR_WIDTH, ROW_HEIGHT),
                    Value = now.ToString() ?? string.Empty
                };
                dropdown.OnChanged = chosen =>
                {
                    if (Enum.TryParse(bare, chosen, out object? parsed)) Write(set, parsed, name);
                };
                refreshers.Add(() =>
                {
                    if (!dropdown.IsOpen && Read(get, out _) is { } value) dropdown.Value = value.ToString() ?? string.Empty;
                });
                return dropdown;
            }

            if (bare == typeof(Vector2) || bare == typeof(Vector3) || bare == typeof(Vector4))
                return Numbers(name, bare, EDITOR_WIDTH, get, set, out _);
        }

        // A string that is null can still be typed into
        if (set is not null && now is null && bare == typeof(string))
            return Editor(name, bare, string.Empty, get, set, hint);

        // Something with an inside of its own opens up, one level down. A few numbers that can't be set are read
        // quicker where they stand
        if (now is not null && now.GetType().Namespace != typeof(Vector2).Namespace && Inspectable.HasInside(now.GetType()))
        {
            bool isStruct = now.GetType().IsValueType;
            return new Button(DebugStyle.Fit(Skin, Summary(now), EDITOR_WIDTH - 44.0f) + "  >")
            {
                Size = new Vector2(EDITOR_WIDTH, ROW_HEIGHT),
                OnPressed = () =>
                {
                    if (Read(get, out _) is { } value)
                        Open(value, name, isStruct && set is not null ? changed => Write(set, changed, name) : null);
                }
            };
        }

        return ReadOnly(() => Format(Read(get, out _)), DebugStyle.Dim, live: true);
    }

    private Label ReadOnly(Func<string> text, Vector4 colour, bool live)
    {
        var label = new Label(DebugStyle.Fit(Skin, text(), EDITOR_WIDTH))
        {
            Size = new Vector2(EDITOR_WIDTH, ROW_HEIGHT),
            Align = Origin.Left,
            Color = colour
        };

        if (live)
        {
            string last = label.Text;
            refreshers.Add(() =>
            {
                // Only a text that changed is a new string, a label that says the same thing costs nothing
                string current = DebugStyle.Fit(Skin, text(), EDITOR_WIDTH);
                if (current != last) label.Text = last = current;
            });
        }

        return label;
    }

    private UIComponent Number(string name, Type bare, object now, Func<object?> get, Action<object?> set, InspectAttribute? hint)
    {
        float value = Convert.ToSingle(now, CultureInfo.InvariantCulture);
        bool whole = Inspectable.IsWhole(bare);

        // Dragging goes about as fast as the number is big, a hundredth at a time gets nowhere on a thousand
        float step = whole ? 1.0f : value == 0.0f ? 0.04f : Math.Clamp(MathF.Abs(value) * 0.02f, 0.004f, 4.0f);

        if (hint is not { HasRange: true })
        {
            var box = NumberEditor(EDITOR_WIDTH, value, step, changed => Write(set, Inspectable.ToNumber(changed, bare), name));
            refreshers.Add(() =>
            {
                if (!box.IsFocused && !box.IsPressed && Read(get, out _) is { } current) box.Value = Convert.ToSingle(current, CultureInfo.InvariantCulture);
            });
            return box;
        }

        // A slider for getting there quickly and the number next to it for getting there exactly
        const float EXACT_WIDTH = 64.0f;
        var both = new StackPanel { Direction = UIDirection.Horizontal, Spacing = GAP };
        NumberBox exact = null!;
        var slider = both.Add(new Slider
        {
            Size = new Vector2(EDITOR_WIDTH - EXACT_WIDTH - GAP, ROW_HEIGHT - 6.0f),
            Min = hint.Min,
            Max = hint.Max,
            Step = whole ? 1.0f : 0.0f,
            Value = value
        });
        exact = both.Add(NumberEditor(EXACT_WIDTH, value, whole ? 1.0f : (hint.Max - hint.Min) / 100.0f, changed =>
        {
            slider.Value = changed;
            Write(set, Inspectable.ToNumber(changed, bare), name);
        }));
        slider.OnChanged = changed =>
        {
            changed = MathF.Round(changed, 3);
            exact.Value = changed;
            Write(set, Inspectable.ToNumber(changed, bare), name);
        };
        refreshers.Add(() =>
        {
            if (slider.IsPressed || exact.IsFocused || exact.IsPressed || Read(get, out _) is not { } current) return;

            float read = Convert.ToSingle(current, CultureInfo.InvariantCulture);
            slider.Value = read;
            exact.Value = read;
        });
        return both;
    }

    private static NumberBox NumberEditor(float width, float value, float step, Action<float> changed) => new(value)
    {
        Size = new Vector2(width, ROW_HEIGHT),
        Padding = BoxPadding,
        DragStep = step,
        OnValueChanged = changed
    };

    /// <summary>Helper method for a box a number of a vector, written back as the whole vector with the one number changed.</summary>
    private StackPanel Numbers(string name, Type bare, float width, Func<object?> get, Action<object?> set, out NumberBox[] boxes)
    {
        float[] parts = Split(Read(get, out _));
        var row = new StackPanel { Direction = UIDirection.Horizontal, Spacing = GAP };
        var made = new NumberBox[parts.Length];
        float each = (width - GAP * (parts.Length - 1)) / parts.Length;

        for (int i = 0; i < parts.Length; i++)
        {
            int part = i;
            float step = parts[i] == 0.0f ? 0.04f : Math.Clamp(MathF.Abs(parts[i]) * 0.02f, 0.004f, 4.0f);
            made[i] = row.Add(NumberEditor(each, parts[i], step, changed =>
            {
                float[] current = Split(Read(get, out _));
                if (part >= current.Length) return;

                current[part] = changed;
                Write(set, Join(current, bare), name);
            }));
        }

        refreshers.Add(() =>
        {
            float[] current = Split(Read(get, out _));
            for (int i = 0; i < made.Length && i < current.Length; i++)
            {
                if (!made[i].IsFocused && !made[i].IsPressed) made[i].Value = current[i];
            }
        });

        boxes = made;
        return row;
    }

    private void AddColour(StackPanel row, string name, Type bare, Func<object?> get, Action<object?> set)
    {
        const float SWATCH_WIDTH = 26.0f;

        row.Add(Numbers(name, bare, EDITOR_WIDTH - SWATCH_WIDTH - GAP, get, set, out NumberBox[] boxes));

        var picker = new ColorPicker
        {
            Anchor = Origin.Left,
            Size = new Vector2(WIDTH - 8.0f, PICKER_HEIGHT),
            ShowAlpha = bare == typeof(Vector4),
            Visible = false
        };
        picker.OnChanged = picked =>
        {
            float[] parts = [MathF.Round(picked.X, 3), MathF.Round(picked.Y, 3), MathF.Round(picked.Z, 3), MathF.Round(picked.W, 3)];
            for (int i = 0; i < boxes.Length; i++)
                boxes[i].Value = parts[i];

            Write(set, Join(parts[..boxes.Length], bare), name);
        };

        Vector4 Current()
        {
            float[] parts = Split(Read(get, out _));
            return parts.Length >= 3 ? new Vector4(parts[0], parts[1], parts[2], parts.Length > 3 ? parts[3] : 1.0f) : Vector4.One;
        }

        row.Add(new Swatch
        {
            Size = new Vector2(SWATCH_WIDTH, ROW_HEIGHT),
            Colour = Current,
            Tooltip = "pick it by eye",
            OnPressed = () =>
            {
                picker.Visible = !picker.Visible;
                if (picker.Visible) picker.Color = Vector4.Clamp(Current(), Vector4.Zero, Vector4.One);
            }
        });

        Add(row);
        Add(picker);
    }

    private static float[] Split(object? value) => value switch
    {
        Vector2 two => [two.X, two.Y],
        Vector3 three => [three.X, three.Y, three.Z],
        Vector4 four => [four.X, four.Y, four.Z, four.W],
        _ => []
    };

    private static object Join(float[] parts, Type bare) =>
        bare == typeof(Vector2) ? new Vector2(parts[0], parts[1])
        : bare == typeof(Vector3) ? new Vector3(parts[0], parts[1], parts[2])
        : new Vector4(parts[0], parts[1], parts[2], parts[3]);

    private static string Summary(object value) => value switch
    {
        string text => text,
        Entity entity => Describe(entity),
        ICollection collection => $"{Inspectable.NameOf(value.GetType())} ({collection.Count})",
        _ => Inspectable.NameOf(value.GetType())
    };

    private static string Format(object? value)
    {
        switch (value)
        {
            case null: return "null";
            case float single: return single.ToString("0.###", CultureInfo.InvariantCulture);
            case double number: return number.ToString("0.###", CultureInfo.InvariantCulture);
            case Vector2 two: return $"{Format(two.X)}, {Format(two.Y)}";
            case Vector3 three: return $"{Format(three.X)}, {Format(three.Y)}, {Format(three.Z)}";
            case Vector4 four: return $"{Format(four.X)}, {Format(four.Y)}, {Format(four.Z)}, {Format(four.W)}";
            case IFormattable formattable: return formattable.ToString(null, CultureInfo.InvariantCulture);
        }

        try
        {
            string text = value.ToString() ?? string.Empty;
            int line = text.IndexOfAny(['\r', '\n']);
            return line >= 0 ? text[..line] : text;
        }
        catch (Exception thrown)
        {
            return $"threw, {thrown.Message}";
        }
    }

    /// <summary>A little square in the colour a row is about, which opens the picker when it is clicked.</summary>
    private sealed class Swatch : UIComponent
    {
        public Func<Vector4>? Colour { get; set; }

        public Action? OnPressed { get; set; }

        protected override bool HitTestVisible => true;

        protected override void Paint(UIDrawList list)
        {
            Vector4 colour = Colour?.Invoke() ?? Vector4.One;

            // Lights go past one, a swatch can only be so bright
            colour = Vector4.Clamp(colour, Vector4.Zero, Vector4.One) with { W = 1.0f };
            list.Box(Bounds, colour);
            list.Frame(Bounds, 1.0f, IsHovered ? list.Skin.HighlightColor : DebugStyle.Edge);
        }

        protected internal override void OnClick() => OnPressed?.Invoke();
    }
}

/// <summary>A texture shown as big as it fits into a box, in its own shape, with nothing behind it but dark.</summary>
internal sealed class TexturePreview : UIComponent
{
    private readonly Texture texture;

    public TexturePreview(Texture texture)
    {
        this.texture = texture;
    }

    protected override void Paint(UIDrawList list)
    {
        list.Box(Bounds, new Vector4(0.0f, 0.0f, 0.0f, 0.45f));

        if (!SkylineDebugger.CanShow(texture))
            return;

        UIRect room = Bounds.Shrink(new UIEdges(4.0f));
        float fit = MathF.Min(room.Width / texture.Width, room.Height / texture.Height);
        list.Image(texture, UIRect.FromCenter(room.Center, new Vector2(texture.Width, texture.Height) * fit), Vector4.One);
    }
}
