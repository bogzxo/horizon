using Horizon.Content;
using Horizon.Rendering;
using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// One out of a list of options, of which only the chosen one is shown until the dropdown is clicked. Then
/// the whole list drops open underneath it (or above, where there is no room underneath), on top of whatever
/// else is there. Clicking an option chooses it, clicking anywhere else leaves things as they were.
/// For more options than a <see cref="Selector"/> is any good for.
/// </summary>
public class Dropdown : UIComponent
{
    private string style = "button_flat";
    private string hoverStyle = "button_flat_hover";

    /// <summary>
    /// The name of the skin region this is drawn with, the one named after it with "_hover" on the end being
    /// what it wears under the pointer, if the skin has that. The names are put together here and not in the
    /// paint, which runs every tick and would make a string every time.
    /// </summary>
    public string Style
    {
        get => style;
        set
        {
            style = StyleStem(value, "_hover");
            hoverStyle = style + "_hover";
        }
    }
    private const float DEFAULT_WIDTH = 220.0f;
    private const float LIST_BORDER = 2.0f;

    private IRuntimeValue? changedHandler;
    private string[] options = [];
    private int index;

    // The first option that is shown while the list is open, for a list with more options than MaxRows.
    private int firstRow;

    // The first row as it's drawn, gliding to firstRow, the way a list's do (see ListBox)
    private float shownRow;

    /// <summary>What there is to choose from, in the order it is listed.</summary>
    public string[] Options
    {
        get => options;
        set
        {
            options = value ?? [];
            index = Math.Clamp(index, 0, Math.Max(0, options.Length - 1));
        }
    }

    /// <summary>
    /// What an option says on screen when it isn't itself, the value being what it is ("none" written "None"). Null for
    /// every option as it is. Worked out once for each option, not every time it's painted.
    /// </summary>
    public Func<string, string>? OptionText
    {
        get => shown;
        set
        {
            shown = value;
            shownFor = null;
        }
    }

    private Func<string, string>? shown;
    private string[]? shownFor, shownOptions;

    // What an option says, its OptionText worked out again only when the options are others
    private string Said(int option)
    {
        if (shown is null || option < 0 || option >= options.Length)
            return option >= 0 && option < options.Length ? options[option] : string.Empty;

        if (!ReferenceEquals(shownFor, options))
        {
            shownOptions = [.. options.Select(shown)];
            shownFor = options;
        }

        return shownOptions![option];
    }

    /// <summary>Which of the options is chosen.</summary>
    public int Index
    {
        get => index;
        set => index = options.Length == 0 ? 0 : Math.Clamp(value, 0, options.Length - 1);
    }

    /// <summary>
    /// The chosen option itself, empty if there are none. Setting it to something that isn't an option
    /// changes nothing.
    /// </summary>
    public string Value
    {
        get => options.Length > 0 ? options[index] : string.Empty;
        set
        {
            int found = Array.IndexOf(options, value);
            if (found >= 0)
                index = found;
        }
    }

    /// <summary>The scale of the text, or zero to use the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>How many options the open list shows at once, the mouse wheel gets to the rest.</summary>
    public int MaxRows { get; set; } = 8;

    /// <summary>The option the top row of the open list shows, which the mouse wheel moves on a list longer than <see cref="MaxRows"/>.</summary>
    public int FirstVisible => firstRow;

    /// <summary>Whether the list is open.</summary>
    public bool IsOpen => IsPopupOpen;

    /// <summary>Called with the newly chosen option whenever a click changes it.</summary>
    public Action<string>? OnChanged { get; set; }

    public Dropdown()
    { }

    public Dropdown(params string[] options)
    {
        Options = options;
    }

    protected override bool HitTestVisible => true;

    protected internal override bool Navigable => true;

    protected internal override void OnActivate()
    {
        if (IsOpen)
            Close();
        else
            Open();
    }

    protected internal override bool OnAdjust(int step)
    {
        // Left and right step through the options without opening the list, the way a selector does
        if (options.Length < 2 || !EnabledInHierarchy)
            return false;

        int chosen = Math.Clamp(index + Math.Sign(step), 0, options.Length - 1);
        if (chosen != index)
            Choose(chosen);

        return true;
    }

    // Tab gets to it, and up and down go through its options without opening it
    protected internal override bool TabStop => options.Length > 1;

    protected internal override void OnTextInput(char character)
    {
        if (!EnabledInHierarchy || options.Length == 0)
            return;

        int step = character switch
        {
            UIKeyboard.UP or UIKeyboard.LEFT => -1,
            UIKeyboard.DOWN or UIKeyboard.RIGHT => 1,
            _ => 0
        };
        if (step != 0)
            Choose(Math.Clamp(index + step, 0, options.Length - 1));
    }

    /// <summary>Helper method to make an option the chosen one and tell whoever listens, if it is another one.</summary>
    private void Choose(int chosen)
    {
        if (chosen == index)
            return;

        index = chosen;
        OnChanged?.Invoke(Value);
        InvokeScript(changedHandler, new StringValue(Value));
    }

    private int VisibleRows => Math.Min(options.Length, Math.Max(1, MaxRows));

    /// <summary>
    /// Where the open list is, in the space of the module. Under the dropdown, or above it if it would
    /// run off the bottom of the screen there.
    /// </summary>
    public UIRect ListBounds
    {
        get
        {
            float height = VisibleRows * Bounds.Height + LIST_BORDER * 2.0f;
            bool below = Module is not { } owner || Bounds.Min.Y - height >= owner.Root.Bounds.Min.Y;

            return below
                ? new UIRect(new Vector2(Bounds.Min.X, Bounds.Min.Y - height), new Vector2(Bounds.Max.X, Bounds.Min.Y))
                : new UIRect(new Vector2(Bounds.Min.X, Bounds.Max.Y), new Vector2(Bounds.Max.X, Bounds.Max.Y + height));
        }
    }

    /// <summary>Where one of the rows of the open list is, counted from the top one that is showing.</summary>
    public UIRect RowBounds(int row)
    {
        UIRect list = ListBounds.Shrink(new UIEdges(LIST_BORDER));
        float top = list.Max.Y - row * Bounds.Height;

        return new UIRect(new Vector2(list.Min.X, top - Bounds.Height), new Vector2(list.Max.X, top));
    }

    public void Open()
    {
        if (options.Length == 0 || !EnabledInHierarchy)
            return;

        // The chosen option is in sight when the list opens.
        firstRow = Math.Clamp(index - VisibleRows / 2, 0, options.Length - VisibleRows);
        shownRow = firstRow;
        OpenPopup();
    }

    public void Close() => ClosePopup();

    protected override Vector2 Measure(UISkin skin)
    {
        float scale = TextScale > 0.0f ? TextScale : skin.TextScale;
        float height = skin.Font.LineHeight * scale + skin.ButtonPadding.Total.Y;

        return new Vector2(DEFAULT_WIDTH, skin.TryGetRegion(style, out var region) ? MathF.Max(height, region.Size.Y * 0.6f) : height);
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;

        bool enabled = EnabledInHierarchy;
        Vector4 tint = enabled ? Vector4.One : skin.DisabledTint;
        float scale = TextScale > 0.0f ? TextScale : skin.TextScale;

        // Art that isn't the colour the skin writes on says what to write on it in
        Vector4 ink = skin.TextColor;

        bool lit = enabled && (IsHovered || IsOpen || IsSelected);

        // The body and the words as parts of their own, the hover box comes and goes and counted with the words it had
        // them blended with it for a frame between two ticks, a white flash (see UIDrawList.BeginPart)
        list.BeginPart(0);
        if ((lit && skin.TryGetRegion(hoverStyle, out var art)) || skin.TryGetRegion(style, out art))
        {
            list.NineSlice(art, Bounds, tint);
            ink = art.TextOr(ink);
        }
        else
        {
            list.Box(Bounds, skin.ControlColor * tint);
            if (lit)
                list.Box(Bounds, skin.HoverColor);
            if (skin.BorderColor.W > 0.0f)
                list.Frame(Bounds, 1.0f, skin.BorderColor * tint);
        }

        list.BeginPart(1);
        UIRect content = Bounds.Shrink(new UIEdges(10.0f, 0.0f));

        list.Text(Resolve(options.Length > 0 ? Said(index) : string.Empty), content, Origin.Left, scale, ink * tint, markup: false);
        // Shut, the list is opened again from the start
        if (!IsOpen)
            listOpenedAt = -1.0f;

        float side = MathF.Round(content.Height * 0.55f);
        list.Symbol(IsOpen ? "chevron-up" : "chevron-down", new UIRect(new Vector2(content.Max.X - side, content.Center.Y - side * 0.5f), new Vector2(content.Max.X, content.Center.Y + side * 0.5f)), skin.AccentColor * tint);

        PaintSelection(list);
        list.EndParts();
    }

    protected internal override void PaintPopup(UIDrawList list)
    {
        UISkin skin = list.Skin;
        float scale = TextScale > 0.0f ? TextScale : skin.TextScale;

        // It unfolds out of the box, down or up whichever way it opens, on what every popup sits on
        UIRect bounds = ListBounds;
        bool below = bounds.Max.Y <= Bounds.Min.Y + 0.5f;
        bool entering = list.BeginEntrance(ref listOpenedAt, new Vector2(bounds.Center.X, below ? bounds.Max.Y : bounds.Min.Y), new Vector2(0.0f, below ? 6.0f : -6.0f));
        list.Popup(bounds, Module?.Compositor.PopupGlass);

        Vector2 pointer = Module is { } owner ? owner.ToLocal(owner.Compositor.Pointer.Position) : default;

        // From where the glide has got to, the row half out at the top cut off at the list's edge and one more at the
        // bottom, in steps while it glides (see UIScrollEase)
        int from = (int)MathF.Floor(shownRow);
        float slide = (shownRow - from) * Bounds.Height;
        bool gliding = shownRow != firstRow;
        list.PushClip(bounds.Shrink(new UIEdges(LIST_BORDER)));
        if (gliding)
            list.BeginSteps();
        for (int row = 0; row <= VisibleRows && from + row < options.Length; row++)
        {
            int option = from + row;
            list.BeginPart(option);
            UIRect area = RowBounds(row);
            area = new UIRect(area.Min + new Vector2(0.0f, slide), area.Max + new Vector2(0.0f, slide));

            if (area.Contains(pointer))
                list.RoundRect(area.Shrink(new UIEdges(3.0f, 1.0f)), skin.CornerRadius, skin.HoverColor);

            list.Text(
                Resolve(Said(option)),
                area.Shrink(new UIEdges(10.0f, 0.0f)),
                Origin.Left,
                scale,
                option == index ? skin.AccentColor : skin.TextColor,
                markup: false);
        }

        if (gliding)
            list.EndSteps();
        list.PopClip();
        list.EndParts();

        // More options than fit. A mark on the side says where in the list this is.
        if (options.Length > VisibleRows)
        {
            UIRect track = bounds.Shrink(new UIEdges(LIST_BORDER));
            float length = track.Height * VisibleRows / options.Length;
            float top = track.Max.Y - (track.Height - length) * shownRow / (options.Length - VisibleRows);

            list.RoundRect(new UIRect(new Vector2(track.Max.X - 6.0f, top - length + 2.0f), new Vector2(track.Max.X - 2.0f, top - 2.0f)), 2.0f, skin.TextColor with { W = 0.25f });
        }

        if (entering)
            list.EndEntrance();
    }

    // When the list that drops down was first painted, by the draw list's clock, less than 0 while it's shut
    private float listOpenedAt = -1.0f;

    // Whether the list is being painted going, after it was shut
    private bool listClosing;

    protected internal override void PopupClosed() => listClosing = true;

    protected internal override bool PaintClosing(UIDrawList list, ref float closedAt)
    {
        UIRect bounds = ListBounds;
        bool below = bounds.Max.Y <= Bounds.Min.Y + 0.5f;
        if (!listClosing || !list.BeginExit(ref closedAt, new Vector2(bounds.Center.X, below ? bounds.Max.Y : bounds.Min.Y), new Vector2(0.0f, below ? 6.0f : -6.0f)))
        {
            listClosing = false;
            return false;
        }

        // Painted as it was, its entrance long over
        listOpenedAt = -1000.0f;
        PaintPopup(list);
        listOpenedAt = -1.0f;
        list.EndExit();
        return true;
    }

    protected internal override bool PopupContains(Vector2 point) => IsOpen && ListBounds.Contains(point);

    protected internal override bool OnScroll(float delta)
    {
        if (!IsOpen || options.Length <= VisibleRows)
            return IsOpen;

        // A row per notch, and never less than one for a wheel that turns in smaller steps than that.
        int rows = Math.Max(1, (int)MathF.Round(MathF.Abs(delta))) * MathF.Sign(delta);
        firstRow = Math.Clamp(firstRow - rows, 0, options.Length - VisibleRows);
        return true;
    }

    protected override void Update(float dt)
    {
        base.Update(dt);
        if (shownRow == firstRow)
            return;

        shownRow += (firstRow - shownRow) * (1.0f - MathF.Exp(-UIScrollEase.RATE * dt));
        if (MathF.Abs(firstRow - shownRow) < 0.01f)
            shownRow = firstRow;
    }

    protected internal override void OnPointerUp(Vector2 point)
    {
        if (!EnabledInHierarchy)
            return;

        if (!IsOpen)
        {
            if (Bounds.Contains(point))
                Open();
            return;
        }

        // The option under the pointer, where the rows are drawn this moment
        UIRect inside = ListBounds.Shrink(new UIEdges(LIST_BORDER));
        if (inside.Contains(point) && (int)MathF.Floor(shownRow + (inside.Max.Y - point.Y) / Bounds.Height) is var chosen && chosen >= 0 && chosen < options.Length)
        {
            Close();
            Choose(chosen);
            return;
        }

        // On the dropdown itself, or let go of somewhere else altogether. Never mind then.
        Close();
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        // A list of texts, or one text with commas in it the way the old layouts wrote it
        Expose("options", () => options, value => Options = value);
        Expose("value", () => Value, value => Value = value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("style", () => Style, value => Style = value);
        Expose("max_rows", () => MaxRows, value => MaxRows = Math.Max(1, (int)value));
        Expose("on_changed", () => changedHandler ?? new NullValue(), value => changedHandler = value);
    }
}
