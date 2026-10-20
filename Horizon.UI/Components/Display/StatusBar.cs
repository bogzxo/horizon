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
    // How much of the accent the bar is tinted with when it isn't given a colour, and the least contrast an item's own
    // colour is allowed on it, the web's for text beside the point
    private const float ACCENT_SHARE = 0.16f;
    private const float LEAST_CONTRAST = 3.0f;

    private const float ITEM_PADDING = 8.0f;
    private const float HEIGHT_PADDING = 6.0f;

    private readonly List<StatusItem> items = [];
    private StatusItem? pressed;

    public IReadOnlyList<StatusItem> Items => items;

    /// <summary>The colour of the strip, null for the skin's accent.</summary>
    public Vector4? Color { get; set; }

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

    public void Clear() => items.Clear();

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

        foreach (StatusItem other in items)
        {
            if (other.Right != item.Right || !other.Shown)
                continue;

            float width = WidthOf(skin, other);
            if (other == item)
                return new UIRect(new Vector2(x, Bounds.Min.Y), new Vector2(x + width, Bounds.Max.Y));

            x += width;
        }

        return default;
    }

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
        // The panel tinted with the accent unless told, the accent itself was a blinding bar of gold in the Horizon theme
        // with the skin's white text on it. The text is whichever of the skin's two reads best on what the bar is
        Vector4 bar = Color ?? Vector4.Lerp(skin.PanelColor with { W = 1.0f }, skin.AccentColor with { W = 1.0f }, ACCENT_SHARE);
        Vector4 text = TextColor ?? (UIPalette.Contrast(skin.TextColor, bar) >= UIPalette.Contrast(skin.BarTextColor, bar) ? skin.TextColor : skin.BarTextColor);

        list.BeginPart(-1);
        list.Rect(Bounds, bar);

        // A hairline in the accent along its top, which is what's left of the accent colour of the whole bar
        if (Color is null)
            list.Rect(new UIRect(new Vector2(Bounds.Min.X, Bounds.Max.Y - 1.0f), Bounds.Max), skin.AccentColor with { W = 0.6f });

        for (int i = 0; i < items.Count; i++)
        {
            list.BeginPart(i);
            StatusItem item = items[i];
            UIRect area = ItemBounds(item);
            if (area.IsEmpty)
                continue;

            if (item.Background is { } background)
                list.Rect(area, background);
            // always there, see-through when not lit, or the quads after it shift (ToggleButton has why)
            list.Rect(area, item.OnPressed is not null && IsHovered && area.Contains(pointer) ? skin.HoverColor : skin.HoverColor with { W = 0.0f });

            // A colour of its own that can't be read on the bar (red on the accent) is the bar's text instead
            Vector4 said = item.Color is { } own && UIPalette.Contrast(own, item.Background ?? bar) >= LEAST_CONTRAST ? own : text;
            list.Text(item.Text, area, Origin.Center, scale, said);
        }

        list.EndParts();
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
