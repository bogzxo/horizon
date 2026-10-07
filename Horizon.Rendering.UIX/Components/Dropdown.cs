using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX.Components;

/// <summary>
/// One out of a list of options, of which only the chosen one is shown until the dropdown is clicked. Then
/// the whole list drops open underneath it (or above, where there is no room underneath), on top of whatever
/// else is there. Clicking an option chooses it, clicking anywhere else leaves things as they were.
/// For more options than a <see cref="Selector"/> is any good for.
/// </summary>
public class Dropdown : UIComponent
{
    private const string REGION = "button_flat";
    private const string HOVER_REGION = "button_flat_hover";
    private const float DEFAULT_WIDTH = 220.0f;
    private const float LIST_BORDER = 2.0f;

    private IRuntimeValue? changedHandler;
    private string[] options = [];
    private int index;

    // The first option that is shown while the list is open, for a list with more options than MaxRows.
    private int firstRow;

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
        OpenPopup();
    }

    public void Close() => ClosePopup();

    protected override Vector2 Measure(UISkin skin)
    {
        float scale = TextScale > 0.0f ? TextScale : skin.TextScale;
        float height = skin.Font.LineHeight * scale + skin.ButtonPadding.Total.Y;

        return new Vector2(DEFAULT_WIDTH, skin.TryGetRegion(REGION, out var region) ? MathF.Max(height, region.Size.Y * 0.6f) : height);
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;

        bool enabled = EnabledInHierarchy;
        Vector4 tint = enabled ? Vector4.One : skin.DisabledTint;
        float scale = TextScale > 0.0f ? TextScale : skin.TextScale;

        if ((enabled && (IsHovered || IsOpen) && skin.TryGetRegion(HOVER_REGION, out var art)) || skin.TryGetRegion(REGION, out art))
            list.NineSlice(art, Bounds, tint);
        else
        {
            list.Box(Bounds, skin.ControlColor * tint);
            if (enabled && (IsHovered || IsOpen))
                list.Box(Bounds, skin.HoverColor);
            if (skin.BorderColor.W > 0.0f)
                list.Frame(Bounds, 1.0f, skin.BorderColor * tint);
        }

        UIRect content = Bounds.Shrink(new UIEdges(10.0f, 0.0f));

        list.Text(Value, content, Origin.Left, scale, skin.TextColor * tint, markup: false);
        list.Text(IsOpen ? "^" : "v", content, Origin.Right, scale, skin.AccentColor * tint, markup: false);
    }

    protected internal override void PaintPopup(UIDrawList list)
    {
        UISkin skin = list.Skin;
        float scale = TextScale > 0.0f ? TextScale : skin.TextScale;

        // Whatever is under the list must not show through it.
        UIRect bounds = ListBounds;
        list.Box(bounds, new Vector4(skin.PanelColor.X, skin.PanelColor.Y, skin.PanelColor.Z, 1.0f));
        list.Frame(bounds, LIST_BORDER, skin.AccentColor);

        Vector2 pointer = Module is { } owner ? owner.ToLocal(owner.Compositor.Pointer.Position) : default;

        for (int row = 0; row < VisibleRows; row++)
        {
            int option = firstRow + row;
            UIRect area = RowBounds(row);

            if (area.Contains(pointer))
                list.Rect(area, skin.HoverColor);

            list.Text(
                options[option],
                area.Shrink(new UIEdges(10.0f, 0.0f)),
                Origin.Left,
                scale,
                option == index ? skin.AccentColor : skin.TextColor,
                markup: false);
        }

        // More options than fit. A mark on the side says where in the list this is.
        if (options.Length > VisibleRows)
        {
            UIRect track = bounds.Shrink(new UIEdges(LIST_BORDER));
            float length = track.Height * VisibleRows / options.Length;
            float top = track.Max.Y - (track.Height - length) * firstRow / (options.Length - VisibleRows);

            list.Rect(new UIRect(new Vector2(track.Max.X - 4.0f, top - length), new Vector2(track.Max.X, top)), skin.AccentColor);
        }
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

        for (int row = 0; row < VisibleRows; row++)
        {
            if (!RowBounds(row).Contains(point))
                continue;

            int chosen = firstRow + row;
            Close();

            if (chosen != index)
            {
                index = chosen;
                OnChanged?.Invoke(Value);
                InvokeScript(changedHandler, new StringValue(Value));
            }
            return;
        }

        // On the dropdown itself, or let go of somewhere else altogether. Never mind then.
        Close();
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        // Scripts have no lists, so the options are one text with commas in it.
        Expose(
            "options",
            () => string.Join(", ", options),
            value => Options = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Expose("value", () => Value, value => Value = value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("max_rows", () => MaxRows, value => MaxRows = Math.Max(1, (int)value));
        Expose("on_changed", () => changedHandler ?? new NullValue(), value => changedHandler = value);
    }
}
