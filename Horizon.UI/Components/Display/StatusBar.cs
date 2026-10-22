using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// One piece of a <see cref="StatusBar"/>, a few words that can start with an icon (<c>"$(git-branch) main"</c>) and
/// can be clicked.
/// </summary>
public sealed class StatusItem
{
    /// <summary>What it says, $(icons) and all. Set it as often as it changes, it is only drawn.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>What comes up when the pointer rests on it.</summary>
    public string Tooltip { get; set; } = string.Empty;

    /// <summary>What clicking it does, null for an item that only says something.</summary>
    public Action? OnPressed { get; set; }

    /// <summary>Whether it is there right now. Always, if nothing is set.</summary>
    public Func<bool>? IsVisible { get; set; }

    /// <summary>Whether it goes at the right end of the bar rather than the left.</summary>
    public bool Right { get; init; }

    /// <summary>The colour of its words, null for the bar's.</summary>
    public Vector4? Color { get; set; }

    /// <summary>A colour behind it, an error that wants seeing, null for none.</summary>
    public Vector4? Background { get; set; }

    /// <summary>How wide it is at the least, so a number that changes doesn't push everything after it about.</summary>
    public float MinWidth { get; set; }

    internal bool Shown => Text.Length > 0 && (IsVisible?.Invoke() ?? true);
}

/// <summary>
/// The strip along the bottom of an editor that says where things stand, the branch, how many problems there are,
/// where the caret is, the line endings, VS Code's and Visual Studio's. Pieces on the left and on the right, each a few
/// words with an icon if it likes, lit under the pointer and doing something when clicked if it does anything.
/// <code>
/// var caret = bar.Add("Ln 1, Col 1", right: true, pressed: GoToLine, tooltip: "Go to a line");
/// caret.Text = $"Ln {line}, Col {column}";
/// </code>
/// A layout says where it goes (<c>compositor.status_bar</c>), what is on it is up to the program.
/// </summary>
public class StatusBar : UIComponent
{
    // How much the words on it are seen through, a bar that says where things stand is said quietly, and the least
    // contrast an item's own colour is allowed on it, the web's for text beside the point
    private const float TEXT_SHARE = 0.78f;
    private const float LEAST_CONTRAST = 3.0f;

    private const float ITEM_PADDING = 8.0f;
    private const float HEIGHT_PADDING = 6.0f;

    private readonly List<StatusItem> items = [];
    private StatusItem? pressed;

    // What each item was last cut to, by what it said and the room it had, made again only when either changes. Cut
    // every paint it made a string a letter tried, a kilobyte a tick in the designer with a long message
    private readonly Dictionary<StatusItem, (string Text, float Room, string Fitted)> fitted = [];

    public IReadOnlyList<StatusItem> Items => items;

    /// <summary>The colour of the strip, null for the skin's panel.</summary>
    public Vector4? Color { get; set; }

    /// <summary>Whether a hairline runs along its top, between it and what it's under. Off for a bar that's part of a frame of one tone.</summary>
    public bool Hairline { get; set; } = true;

    /// <summary>The colour of the words on it, null for the skin's text colour.</summary>
    public Vector4? TextColor { get; set; }

    /// <summary>The scale of the text, or zero for a little smaller than the skin's.</summary>
    public float TextScale { get; set; }

    protected override bool HitTestVisible => true;

    /// <summary>Adds a piece at the end of the left or of the right.</summary>
    public StatusItem Add(string text, bool right = false, Action? pressed = null, string tooltip = "")
    {
        var item = new StatusItem { Text = text, Right = right, OnPressed = pressed, Tooltip = tooltip };
        items.Add(item);
        return item;
    }

    public void Clear()
    {
        items.Clear();
        fitted.Clear();
    }

    private float ScaleOf(UISkin skin) => TextScale > 0.0f ? TextScale : skin.TextScale * 0.95f;

    protected override Vector2 Measure(UISkin skin) => new(0.0f, MathF.Round(skin.Font.LineHeight * ScaleOf(skin) + HEIGHT_PADDING * 2.0f));

    private float WidthOf(UISkin skin, StatusItem item) =>
        MathF.Max(item.MinWidth, skin.Font.Measure(item.Text, ScaleOf(skin)).X + ITEM_PADDING * 2.0f);

    /// <summary>Where an item is on the bar, in the module's units. Empty for one that isn't there right now.</summary>
    public UIRect ItemBounds(StatusItem item)
    {
        if (Module?.Compositor.Skin is not { } skin || !item.Shown)
            return default;

        // The left ones from the left in their order, the right ones end at the right in theirs
        float x = Bounds.Min.X;
        if (item.Right)
        {
            x = Bounds.Max.X;
            foreach (StatusItem other in items)
            {
                if (other.Right && other.Shown)
                    x -= WidthOf(skin, other);
            }
        }

        // The left ones stop short of where the right ones start, a long message was written over them
        float rightStart = Bounds.Max.X;
        if (!item.Right)
        {
            foreach (StatusItem other in items)
            {
                if (other.Right && other.Shown)
                    rightStart -= WidthOf(skin, other);
            }
        }

        foreach (StatusItem other in items)
        {
            if (other.Right != item.Right || !other.Shown)
                continue;

            float width = WidthOf(skin, other);
            if (other == item)
            {
                if (!item.Right)
                    width = MathF.Min(width, rightStart - LEFT_GAP - x);
                return width <= ITEM_PADDING * 2.0f ? default : new UIRect(new Vector2(x, Bounds.Min.Y), new Vector2(x + width, Bounds.Max.Y));
            }

            x += width;
        }

        return default;
    }

    // The room kept between the last of the left items and the first of the right
    private const float LEFT_GAP = 12.0f;

    /// <summary>
    /// Helper method for what an item says cut to its room with three dots, all of it when it fits. A pixel either way
    /// is a fit, the room is worked out of the same measure and came out a hair short of it, every item on the right lost
    /// its last letter. Three dots and not the one character of them, the UI's font has none and drew a question mark. A
    /// text with an icon in it is left whole, cut it would be half a tag.
    /// </summary>
    private static string Fitted(UIFont font, string text, float room, float scale)
    {
        if (font.Measure(text, scale).X <= room + FIT_SLACK || text.Contains("$(", StringComparison.Ordinal))
            return text;

        int length = text.Length;
        while (length > 0 && font.Measure(string.Concat(text.AsSpan(0, length), CUT), scale).X > room + FIT_SLACK)
            length--;
        return length == 0 ? string.Empty : string.Concat(text.AsSpan(0, length).TrimEnd(), CUT);
    }

    // What's put where an item was cut, and how far over its room a text may be and still be a fit
    private const string CUT = "...";
    private const float FIT_SLACK = 1.0f;

    private StatusItem? ItemAt(Vector2 point)
    {
        foreach (StatusItem item in items)
        {
            if (ItemBounds(item).Contains(point))
                return item;
        }

        return null;
    }

    private Vector2 Pointer => Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : default;

    protected override void Update(float dt)
    {
        // The tooltip of whichever piece the pointer is on
        StatusItem? over = IsHovered ? ItemAt(Pointer) : null;
        Tooltip = over?.Tooltip ?? string.Empty;
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        float scale = ScaleOf(skin);
        Vector2 pointer = Pointer;
        // The panel unless told, the chrome the rest of the window's edge is. It was the accent, a blinding bar of gold in
        // the Horizon theme, and then the panel tinted with it, which came out olive. The text is whichever of the skin's
        // two reads best on what the bar is, a little seen through
        Vector4 bar = Color ?? skin.PanelColor with { W = 1.0f };
        Vector4 text = TextColor ?? (UIPalette.Contrast(skin.TextColor, bar) >= UIPalette.Contrast(skin.BarTextColor, bar) ? skin.TextColor : skin.BarTextColor);
        if (TextColor is null)
            text = text with { W = text.W * TEXT_SHARE };

        list.BeginPart(-1);
        list.Rect(Bounds, bar);

        // A hairline along its top in the colour of the skin's edges, between it and what it's under
        list.Rect(new UIRect(new Vector2(Bounds.Min.X, Bounds.Max.Y - 1.0f), Bounds.Max), skin.BorderColor with { W = Hairline ? skin.BorderColor.W : 0.0f });

        for (int i = 0; i < items.Count; i++)
        {
            list.BeginPart(i);
            StatusItem item = items[i];
            UIRect area = ItemBounds(item);
            if (area.IsEmpty)
                continue;

            // What's behind an item (the red of errors) and its hover are pills inside the bar, not slabs the bar's height.
            // A square block of red on a window of round cards was the one hard corner left on it
            UIRect pill = Pill(area);
            float round = pill.Height * 0.5f;
            if (item.Background is { } background)
                list.RoundRect(pill, round, background);
            // always there, see-through when not lit, or the quads after it shift (ToggleButton has why)
            list.RoundRect(pill, round, item.OnPressed is not null && IsHovered && area.Contains(pointer) ? skin.HoverColor : skin.HoverColor with { W = 0.0f });

            // A colour of its own that can't be read on the bar (red on the accent) is the bar's text instead
            Vector4 said = item.Color is { } own && UIPalette.Contrast(own, item.Background ?? bar) >= LEAST_CONTRAST ? own : text;
            float room = area.Width - ITEM_PADDING * 2.0f;
            if (!fitted.TryGetValue(item, out var was) || !ReferenceEquals(was.Text, item.Text) || was.Room != room)
                fitted[item] = was = (item.Text, room, Fitted(skin.Font, item.Text, room, scale));
            list.Text(was.Fitted, area, Origin.Center, scale, said);
        }

        list.EndParts();
    }

    // An item's area a little in from the bar's top and bottom and its own sides
    private static UIRect Pill(UIRect area)
    {
        float down = MathF.Min(3.0f, MathF.Max(0.0f, (area.Height - 12.0f) * 0.5f));
        return new UIRect(area.Min + new Vector2(1.0f, down), area.Max - new Vector2(1.0f, down));
    }

    protected internal override void OnPointerDown(Vector2 point) => pressed = ItemAt(point);

    protected internal override void OnClick()
    {
        if (pressed is { OnPressed: { } press } item && ItemBounds(item).Contains(Pointer))
            press();

        pressed = null;
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("color", () => Color ?? Vector4.Zero, value => Color = value == Vector4.Zero ? null : value);
        Expose("text_color", () => TextColor ?? Vector4.Zero, value => TextColor = value == Vector4.Zero ? null : value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
    }
}
