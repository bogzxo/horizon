using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// Where something is as a path of pieces with an arrow between them, the folders of a file, the file, and where in
/// it the caret is, VS Code's line under the tabs. Each piece lights up under the pointer and says so when clicked.
/// The last is written brighter, it is where you are. Too long for its room, it loses pieces off the front, and says
/// so with dots where they were.
/// <code>
/// crumbs.Set(["Fighter2D", "Assets", "$(file-code) en-gb.hor", "strings", "main_menu"]);
/// crumbs.OnPressed = index => GoTo(index);
/// </code>
/// </summary>
public class Breadcrumbs : UIComponent
{
    private const float PIECE_PADDING = 4.0f;
    private const float ARROW_SHARE = 0.7f;

    // What stands for the pieces lost off the front
    private const string LOST = "...";

    private readonly List<string> parts = [];
    private int pressed = -1;

    /// <summary>The pieces, from the outermost in. $(icons) are fine.</summary>
    public IReadOnlyList<string> Parts => parts;

    /// <summary>Called with the piece that was clicked.</summary>
    public Action<int>? OnPressed { get; set; }

    /// <summary>The scale of the text, or zero for a little smaller than the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>
    /// A box the whole path can be typed into, under the crumbs, the way Explorer's address bar and a Finder window's
    /// path work. A click past the last piece gives it the keyboard with what's in it picked, and the crumbs stand aside
    /// while it has it. Give the box <see cref="TextBox.TextWhileTyped"/> or its text shows through them.
    /// </summary>
    public TextBox? Editor { get; set; }

    // Whether the box under them is being typed in, the crumbs are neither drawn nor clicked then
    private bool Editing => Editor is { IsFocused: true };

    protected override bool HitTestVisible => !Editing;

    /// <summary>Puts the pieces in, nothing changes if they are the ones there already.</summary>
    public void Set(IReadOnlyList<string> pieces)
    {
        bool same = pieces.Count == parts.Count;
        for (int i = 0; same && i < pieces.Count; i++)
            same = pieces[i] == parts[i];

        if (same)
            return;

        parts.Clear();
        parts.AddRange(pieces);
    }

    private float ScaleOf(UISkin skin) => TextScale > 0.0f ? TextScale : skin.TextScale * 0.95f;

    private float Arrow(UISkin skin) => MathF.Round(skin.Font.LineHeight * ScaleOf(skin) * ARROW_SHARE);

    protected override Vector2 Measure(UISkin skin) => new(0.0f, MathF.Round(skin.Font.LineHeight * ScaleOf(skin) + PIECE_PADDING * 2.0f));

    private float WidthOf(UISkin skin, string part) => skin.Font.Measure(part, ScaleOf(skin)).X + PIECE_PADDING * 2.0f;

    // How wide the dots for the pieces lost are, with their arrow
    private float LostWidth(UISkin skin) => WidthOf(skin, LOST) + Arrow(skin);

    // The first piece that is drawn, the ones before it don't fit. Once one is lost the dots take room too. They went
    // without a word, and a path looked like it started at whatever fitted
    private int FirstShown(UISkin skin)
    {
        float room = Bounds.Width, all = 0.0f;
        for (int i = 0; i < parts.Count; i++)
            all += WidthOf(skin, parts[i]) + (i > 0 ? Arrow(skin) : 0.0f);
        if (all <= room)
            return 0;

        int last = parts.Count - 1;
        float used = LostWidth(skin);
        for (int i = last; i >= 0; i--)
        {
            used += WidthOf(skin, parts[i]) + (i < last ? Arrow(skin) : 0.0f);
            if (used > room)
                return Math.Min(last, i + 1);
        }

        return last;
    }

    /// <summary>Where a piece is, in the module's units. Empty for one that is off the front for want of room.</summary>
    public UIRect PartBounds(int index)
    {
        if (Module?.Compositor.Skin is not { } skin || index < 0 || index >= parts.Count)
            return default;

        int first = FirstShown(skin);
        if (index < first)
            return default;

        float x = Bounds.Min.X + (first > 0 ? LostWidth(skin) : 0.0f);
        for (int i = first; i < index; i++)
            x += WidthOf(skin, parts[i]) + Arrow(skin);

        return new UIRect(new Vector2(x, Bounds.Min.Y), new Vector2(x + WidthOf(skin, parts[index]), Bounds.Max.Y));
    }

    private int PartAt(Vector2 point)
    {
        for (int i = 0; i < parts.Count; i++)
        {
            if (PartBounds(i).Contains(point))
                return i;
        }

        return -1;
    }

    private Vector2 Pointer => Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : default;

    protected override void Paint(UIDrawList list)
    {
        if (Editing)
            return;

        UISkin skin = list.Skin;
        float scale = ScaleOf(skin);
        float arrow = Arrow(skin);
        Vector2 pointer = Pointer;
        Vector4 dim = skin.DimTextColor;

        list.PushClip(Bounds);

        // The dots for what's lost off the front, always painted (see-through with nothing lost) so the quads after don't shift
        int first = FirstShown(skin);
        var lost = new UIRect(Bounds.Min, new Vector2(Bounds.Min.X + WidthOf(skin, LOST), Bounds.Max.Y));
        list.Text(LOST, lost, Origin.Center, scale, dim with { W = first > 0 ? dim.W : 0.0f });
        var lostArrow = new UIRect(new Vector2(lost.Max.X, lost.Center.Y - arrow * 0.5f), new Vector2(lost.Max.X + arrow, lost.Center.Y + arrow * 0.5f));
        list.Symbol("chevron-right", lostArrow, dim with { W = first > 0 ? dim.W : 0.0f });

        for (int i = 0; i < parts.Count; i++)
        {
            list.BeginPart(i);
            UIRect area = PartBounds(i);
            if (area.IsEmpty)
                continue;

            // always there, see-through when not lit, or the quads after it shift (ToggleButton has why)
            list.Box(area, OnPressed is not null && IsHovered && area.Contains(pointer) ? skin.HoverColor : skin.HoverColor with { W = 0.0f });

            list.Text(parts[i], area, Origin.Center, scale, i == parts.Count - 1 ? skin.TextColor : dim);

            if (i < parts.Count - 1)
            {
                var after = new UIRect(new Vector2(area.Max.X, area.Center.Y - arrow * 0.5f), new Vector2(area.Max.X + arrow, area.Center.Y + arrow * 0.5f));
                list.Symbol("chevron-right", after, dim);
            }
        }

        list.EndParts();
        list.PopClip();
    }

    protected internal override void OnPointerDown(Vector2 point) => pressed = PartAt(point);

    protected internal override void OnClick()
    {
        int index = pressed;
        pressed = -1;

        if (index >= 0 && PartAt(Pointer) == index)
        {
            OnPressed?.Invoke(index);
        }
        else if (index < 0 && Editor is { } editor && Bounds.Contains(Pointer))
        {
            editor.Focus();
            editor.SelectAll();
        }
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("text_scale", () => TextScale, value => TextScale = value);
    }
}
