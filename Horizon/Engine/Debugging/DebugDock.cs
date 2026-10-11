using System.Numerics;

using Horizon.Graphics;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Engine.Debugging;

/// <summary>The colours the suite is drawn in, on top of what the flat skin brings.</summary>
internal static class DebugStyle
{
    public static readonly Vector4 Window = new(0.045f, 0.05f, 0.065f, 1.0f);
    public static readonly Vector4 Panel = new(0.1f, 0.11f, 0.145f, 1.0f);
    public static readonly Vector4 Title = new(0.07f, 0.078f, 0.105f, 1.0f);
    public static readonly Vector4 Edge = new(1.0f, 1.0f, 1.0f, 0.08f);
    public static readonly Vector4 Dim = new(0.56f, 0.6f, 0.68f, 1.0f);
    public static readonly Vector4 Component = new(0.55f, 0.76f, 0.98f, 1.0f);
    public static readonly Vector4 Good = new(0.45f, 0.86f, 0.52f, 1.0f);
    public static readonly Vector4 Warn = new(1.0f, 0.78f, 0.32f, 1.0f);
    public static readonly Vector4 Bad = new(1.0f, 0.42f, 0.4f, 1.0f);

    /// <summary>What a path is called once the folders are taken off the front of it, anything else as it is.</summary>
    public static string FileName(string name)
    {
        int slash = name.LastIndexOfAny(['/', (char)92]);
        return slash >= 0 && slash < name.Length - 1 ? name[(slash + 1)..] : name;
    }

    /// <summary>
    /// Helper method to cut a text short so it fits a width, with two dots where the rest was. A label with a size
    /// of its own writes straight on past it otherwise, into whatever is next to it.
    /// </summary>
    public static string Fit(UISkin? skin, string text, float width, float scale = 0.0f)
    {
        if (skin is null || text.Length == 0)
            return text;

        scale = scale > 0.0f ? scale : skin.TextScale;
        if (skin.Font.Measure(text, scale, markup: false).X <= width)
            return text;

        int low = 0, high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (skin.Font.Measure(string.Concat(text.AsSpan(0, middle), ".."), scale, markup: false).X <= width) low = middle;
            else high = middle - 1;
        }

        return string.Concat(text.AsSpan(0, low), "..");
    }
}

/// <summary>
/// One of the panels docked around the game. A strip with what it is called along the top and whatever it holds
/// under that, given all the room there is. With more than one thing in it the strip is tabs, a page each.
/// </summary>
internal sealed class DockPanel : UIComponent
{
    public const float TITLE_HEIGHT = 26.0f;
    private const float TAB_PADDING = 12.0f;

    private readonly List<UIRect> tabs = [];

    /// <summary>What every page is called, in the order the pages were added.</summary>
    public string[] Titles { get; set; } = [];

    public int Selected { get; set; }

    protected override bool HitTestVisible => true;

    protected override bool ClipsChildren => true;

    protected internal override bool ShowsChild(UIComponent child) => Children.Count <= 1 || (Selected < Children.Count && Children[Selected] == child);

    protected override void Arrange(UIRect content)
    {
        var body = new UIRect(Bounds.Min, new Vector2(Bounds.Max.X, Bounds.Max.Y - TITLE_HEIGHT));
        foreach (var child in ChildSpan)
            child.ArrangeTree(body);
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;

        list.Rect(Bounds, DebugStyle.Panel);

        var strip = new UIRect(new Vector2(Bounds.Min.X, Bounds.Max.Y - TITLE_HEIGHT), Bounds.Max);
        list.Rect(strip, DebugStyle.Title);

        tabs.Clear();
        float left = strip.Min.X;
        for (int i = 0; i < Titles.Length; i++)
        {
            float width = skin.Font.Measure(Titles[i], skin.TextScale, markup: false).X + TAB_PADDING * 2.0f;
            var tab = new UIRect(new Vector2(left, strip.Min.Y), new Vector2(left + width, strip.Max.Y));
            tabs.Add(tab);

            // Only tabs say which one of them is up, a panel with one thing in it is that thing
            bool chosen = Titles.Length > 1 && i == Selected;
            if (chosen)
            {
                list.Rect(tab, DebugStyle.Panel);
                list.Rect(new UIRect(new Vector2(tab.Min.X, tab.Max.Y - 2.0f), tab.Max), skin.AccentColor);
            }

            list.Text(Titles[i], tab, Origin.Center, skin.TextScale, chosen || Titles.Length == 1 ? skin.TextColor : DebugStyle.Dim, markup: false);
            left += width;
        }

        list.Outline(Bounds, 1.0f, DebugStyle.Edge);
    }

    protected override void PaintChildren(UIDrawList list)
    {
        list.PushClip(new UIRect(Bounds.Min, new Vector2(Bounds.Max.X, Bounds.Max.Y - TITLE_HEIGHT)));
        base.PaintChildren(list);
        list.PopClip();
    }

    protected internal override void OnPointerDown(Vector2 point)
    {
        if (Titles.Length < 2)
            return;

        for (int i = 0; i < tabs.Count; i++)
        {
            if (tabs[i].Contains(point))
                Selected = i;
        }
    }
}

/// <summary>
/// The frame of the game as it was drawn, shown in a part of the window. The suite takes the picture of the
/// window once everything is in it (see <see cref="SkylineDebugger.Draw"/>) and this is where it goes.
/// </summary>
internal sealed class GameView : UIComponent
{
    public Func<Texture?>? Picture { get; set; }

    protected override void Paint(UIDrawList list)
    {
        list.Rect(Bounds, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));

        if (Picture?.Invoke() is { IsValid: true } picture)
            list.Image(picture, Bounds, Vector2.Zero, new Vector2(picture.Width, picture.Height), Vector4.One);

        list.Outline(Bounds.Shrink(new UIEdges(-1.0f)), 1.0f, DebugStyle.Edge);
    }
}

/// <summary>
/// What shares the window out. The menu bar along the top, the scene tree down the left, the inspector down the
/// right, the drawer (content, metrics, log) along the bottom between the two, and whatever is left in the middle
/// for the game. Nothing in here has a size of its own to go wrong, every part is handed its rectangle.
/// </summary>
internal sealed class DebugDock : UIComponent
{
    public const float BAR_HEIGHT = 30.0f;

    private const float LEFT_WIDTH = 300.0f;
    private const float RIGHT_WIDTH = 372.0f;
    private const float BOTTOM_HEIGHT = 250.0f;
    private const float GAME_MARGIN = 10.0f;
    private const float CAPTION_HEIGHT = 22.0f;

    // No panel takes more of the window than this, however small the window is
    private const float MOST_OF_WIDTH = 0.28f;
    private const float MOST_OF_HEIGHT = 0.34f;

    public required Panel Bar { get; init; }
    public required DockPanel Left { get; init; }
    public required DockPanel Right { get; init; }
    public required DockPanel Bottom { get; init; }
    public required GameView Game { get; init; }

    public bool ShowLeft { get; set; } = true;
    public bool ShowRight { get; set; } = true;
    public bool ShowBottom { get; set; } = true;

    /// <summary>Where the game is on screen, in the units of the layout.</summary>
    public UIRect GameRect { get; private set; }

    /// <summary>What is written over the game in its container, how big it really is and how big it is shown.</summary>
    public string Caption { get; set; } = string.Empty;

    /// <summary>Helper method to switch the parts on and off the way the flags say, before they are laid out.</summary>
    public void Sync()
    {
        Left.Visible = ShowLeft;
        Right.Visible = ShowRight;
        Bottom.Visible = ShowBottom;
    }

    protected override void Arrange(UIRect content)
    {
        var bar = new UIRect(new Vector2(content.Min.X, content.Max.Y - BAR_HEIGHT), content.Max);
        Bar.ArrangeTree(bar);

        var body = new UIRect(content.Min, new Vector2(content.Max.X, bar.Min.Y));
        float left = Left.Visible ? MathF.Min(LEFT_WIDTH, body.Width * MOST_OF_WIDTH) : 0.0f;
        float right = Right.Visible ? MathF.Min(RIGHT_WIDTH, body.Width * MOST_OF_WIDTH) : 0.0f;
        float bottom = Bottom.Visible ? MathF.Min(BOTTOM_HEIGHT, body.Height * MOST_OF_HEIGHT) : 0.0f;

        Left.ArrangeTree(new UIRect(body.Min, new Vector2(body.Min.X + left, body.Max.Y)));
        Right.ArrangeTree(new UIRect(new Vector2(body.Max.X - right, body.Min.Y), body.Max));
        Bottom.ArrangeTree(new UIRect(new Vector2(body.Min.X + left, body.Min.Y), new Vector2(body.Max.X - right, body.Min.Y + bottom)));

        // The game keeps the shape of the window, as big as it fits into the middle with a line of text over it
        var middle = new UIRect(new Vector2(body.Min.X + left, body.Min.Y + bottom), new Vector2(body.Max.X - right, body.Max.Y - CAPTION_HEIGHT))
            .Shrink(new UIEdges(GAME_MARGIN));
        float fit = content.IsEmpty || middle.IsEmpty ? 0.0f : MathF.Min(middle.Width / content.Width, middle.Height / content.Height);
        Vector2 size = new(MathF.Floor(content.Width * fit), MathF.Floor(content.Height * fit));
        Vector2 corner = new(MathF.Round(middle.Center.X - size.X * 0.5f), MathF.Round(middle.Max.Y - size.Y));

        GameRect = new UIRect(corner, corner + size);
        Game.ArrangeTree(GameRect);
    }

    protected override void Paint(UIDrawList list)
    {
        // Everything the game drew is under this, it is only seen in its container from here on
        list.Rect(Bounds, DebugStyle.Window);

        var caption = new UIRect(new Vector2(GameRect.Min.X, GameRect.Max.Y), new Vector2(GameRect.Max.X, GameRect.Max.Y + CAPTION_HEIGHT));
        list.Text(Caption, caption, Origin.Left, list.Skin.TextScale, DebugStyle.Dim, markup: false);
    }
}
