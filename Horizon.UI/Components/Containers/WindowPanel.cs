using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A little window inside the UI. A panel with a small title bar along its top that drags it about its parent, an x
/// that closes it if it has one (<see cref="Closable"/>), and a grip in its bottom right corner that drags it bigger if
/// it has that (<see cref="ResizeGrip"/>). What is in it goes under the title bar, laid out the way a panel lays its
/// children out. Clicking anywhere on it brings it in front of the windows next to it.
/// <code>
/// let tools = compositor.window({ title: "Tools", size: vec(320, 240), closable: true, resize_grip: true });
/// let list = compositor.list({ parent: tools, fill: "both" });
/// </code>
/// Closing it hides it (<see cref="UIComponent.Visible"/>) and says so with <see cref="OnClosed"/>, showing it again is
/// the program's business.
/// </summary>
public class WindowPanel : Panel
{
    private const float GRIP = 14.0f;
    private const float CLOSE_WIDTH = 30.0f;

    private enum Drag { None, Move, Resize, Close }
    private Drag drag;
    private Vector2 dragFrom;
    private Vector2 startPosition, startSize;

    /// <summary>What its title bar says.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>One of the icons every skin has (<see cref="UISymbols"/>) in front of the title, empty for none.</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>Whether its title bar has an x that closes it.</summary>
    public bool Closable { get; set; } = true;

    /// <summary>Whether it has a grip in its bottom right corner that drags it bigger and smaller.</summary>
    public bool ResizeGrip { get; set; } = true;

    /// <summary>Whether its title bar drags it about.</summary>
    public bool Movable { get; set; } = true;

    /// <summary>How tall its title bar is.</summary>
    public float TitleHeight { get; set; } = 26.0f;

    /// <summary>The scale of the title, or zero for a little smaller than the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>Called once its x was clicked and it was hidden.</summary>
    public Action? OnClosed { get; set; }

    /// <summary>Called as it is dragged about, with where it is now.</summary>
    public Action<Vector2>? OnMoved { get; set; }

    public WindowPanel()
    {
        MinSize = new Vector2(120.0f, 80.0f);
    }

    public WindowPanel(string title)
        : this()
    {
        Title = title;
    }

    protected override bool HitTestVisible => true;

    private float ScaleOf(UISkin skin) => TextScale > 0.0f ? TextScale : skin.TextScale * 0.95f;

    protected override Vector2 Measure(UISkin skin) => base.Measure(skin) + new Vector2(0.0f, TitleHeight);

    protected override void Arrange(UIRect content) =>
        base.Arrange(new UIRect(content.Min, new Vector2(content.Max.X, content.Max.Y - TitleHeight)));

    /// <summary>The title bar, along the top, in the module's units.</summary>
    public UIRect TitleBounds => new(new Vector2(Bounds.Min.X, Bounds.Max.Y - TitleHeight), Bounds.Max);

    /// <summary>The x that closes it, empty when it has none.</summary>
    public UIRect CloseBounds => Closable
        ? new UIRect(new Vector2(Bounds.Max.X - CLOSE_WIDTH, Bounds.Max.Y - TitleHeight), Bounds.Max)
        : default;

    /// <summary>The grip in the bottom right corner, empty when it has none.</summary>
    public UIRect GripBounds => ResizeGrip
        ? new UIRect(new Vector2(Bounds.Max.X - GRIP, Bounds.Min.Y), new Vector2(Bounds.Max.X, Bounds.Min.Y + GRIP))
        : default;

    private Vector2 Pointer => Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : default;

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        Vector2 pointer = Pointer;

        // The body is the panel's own (a colour, a background of the skin's art, its outline), a box of the skin's without
        list.BeginPart(-1);
        if (HasBackdrop)
            base.Paint(list);
        else
            list.Box(Bounds, skin.PanelColor with { W = 1.0f });

        // The title bar, a shade darker, with a line of the accent under it
        list.BeginPart(-2);
        UIRect bar = TitleBounds;
        Vector4 panel = Color ?? skin.PanelColor;
        list.Rect(bar, new Vector4(panel.X * 0.75f, panel.Y * 0.75f, panel.Z * 0.75f, 1.0f));
        list.Rect(new UIRect(bar.Min, new Vector2(bar.Max.X, bar.Min.Y + 1.0f)), skin.AccentColor with { W = 0.6f });
        list.Frame(Bounds, 1.0f, skin.AccentColor with { W = 0.45f });

        float x = bar.Min.X + 8.0f;
        if (Icon.Length > 0)
        {
            float side = MathF.Round(TitleHeight * 0.55f);
            list.Symbol(Icon, new UIRect(new Vector2(x, bar.Center.Y - side * 0.5f), new Vector2(x + side, bar.Center.Y + side * 0.5f)), skin.AccentColor);
            x += side + 6.0f;
        }

        list.PushClip(new UIRect(bar.Min, new Vector2(bar.Max.X - (Closable ? CLOSE_WIDTH : 0.0f), bar.Max.Y)));
        list.Text(Resolve(Title), new UIRect(new Vector2(x, bar.Min.Y), bar.Max), Origin.Left, ScaleOf(skin), skin.TextColor, markup: false);
        list.PopClip();

        list.BeginPart(-3);
        if (Closable)
        {
            UIRect close = CloseBounds;
            bool over = IsHovered && close.Contains(pointer);
            UIWindowButtons.Paint(list, close, "chrome-close", over, over && IsPressed, true, skin.TextColor);
        }

        // The grip, three short lines across the corner, VS's
        list.BeginPart(-4);
        if (ResizeGrip)
        {
            UIRect grip = GripBounds;
            bool over = (IsHovered && grip.Contains(pointer)) || drag == Drag.Resize;
            Vector4 shade = over ? skin.AccentColor : skin.TextColor with { W = 0.35f };
            for (int i = 1; i <= 3; i++)
            {
                float step = i * 4.0f;
                list.Rect(new UIRect(new Vector2(grip.Max.X - step, grip.Min.Y + 2.0f), new Vector2(grip.Max.X - step + 2.0f, grip.Min.Y + 4.0f)), shade);
                list.Rect(new UIRect(new Vector2(grip.Max.X - 4.0f, grip.Min.Y + step - 2.0f), new Vector2(grip.Max.X - 2.0f, grip.Min.Y + step)), shade);
            }
        }

        list.EndParts();
    }

    protected internal override void OnPointerDown(Vector2 point)
    {
        // In front of the others in the same parent, the way a window is when it is clicked
        BringToFront();

        dragFrom = point;
        startPosition = Position;
        startSize = Size == Vector2.Zero ? Bounds.Size : Size;

        if (CloseBounds.Contains(point))
            drag = Drag.Close;
        else if (GripBounds.Contains(point))
            drag = Drag.Resize;
        else if (Movable && TitleBounds.Contains(point))
            drag = Drag.Move;
        else
            drag = Drag.None;
    }

    protected internal override void OnPointerDrag(Vector2 point)
    {
        Vector2 moved = point - dragFrom;

        if (drag == Drag.Move)
        {
            Position = startPosition + moved;
            OnMoved?.Invoke(Position);
        }
        else if (drag == Drag.Resize)
        {
            // The top left corner stays where it is, whichever point of it the window hangs off
            Vector2 most = MaxSize;
            var size = new Vector2(
                Math.Clamp(startSize.X + moved.X, MinSize.X, most.X > 0.0f ? most.X : float.MaxValue),
                Math.Clamp(startSize.Y - moved.Y, MinSize.Y, most.Y > 0.0f ? most.Y : float.MaxValue));

            Vector2 pivot = (Pivot ?? Anchor).ToVector();
            Vector2 grown = size - startSize;
            Size = size;
            Position = startPosition + new Vector2(grown.X * (pivot.X + 0.5f), -grown.Y * (0.5f - pivot.Y));
        }
    }

    protected internal override void OnPointerUp(Vector2 point)
    {
        if (drag == Drag.Close && CloseBounds.Contains(point))
        {
            Visible = false;
            OnClosed?.Invoke();
        }

        drag = Drag.None;
    }

    /// <summary>Puts it after the others in its parent, which is in front of them.</summary>
    public void BringToFront()
    {
        if (Parent is { } parent && parent.Children.Count > 1 && parent.Children[^1] != this)
            parent.MoveChild(this, parent.Children.Count);
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("title", () => Title, value => Title = value);
        Expose("icon", () => Icon, value => Icon = value);
        Expose("closable", () => Closable, value => Closable = value);
        Expose("resize_grip", () => ResizeGrip, value => ResizeGrip = value);
        Expose("movable", () => Movable, value => Movable = value);
        Expose("title_height", () => TitleHeight, value => TitleHeight = MathF.Max(12.0f, value));
        Expose("text_scale", () => TextScale, value => TextScale = value);
    }
}
