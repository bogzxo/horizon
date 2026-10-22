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

    /// <summary>
    /// Asked first when Escape comes to it, true when that dealt with it and the window stays (a file dialog naming a new
    /// folder lets go of the name). Null, or false, and Escape closes it.
    /// </summary>
    public Func<bool>? BeforeEscape { get; set; }

    // How far the shadow under one reaches, a window floats over what's behind it the way a popup does
    private const float LIFT = 28.0f;

    public WindowPanel()
    {
        MinSize = new Vector2(120.0f, 80.0f);
        Lift = LIFT;
    }

    public WindowPanel(string title)
        : this()
    {
        Title = title;
    }

    protected override bool HitTestVisible => true;

    // Tab goes round what's in it, never out to what's behind
    protected internal override bool HoldsTab => true;

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

    // As round as a popup unless it says otherwise, a window over everything is a popup that stays
    private float CornerOf(UISkin skin) => Radius > 0.0f ? Radius : skin.TryGetRegion(Background, out _) ? skin.CornerRadius : skin.PopupRadius;

    protected override float GlassRadius(UISkin skin) => CornerOf(skin);

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        Vector2 pointer = Pointer;

        // The body is the panel's own (a colour, a background of the skin's art, its outline), on its glass when it has
        // some, as round as a popup, a box of the skin's without. With no colour of its own it's the palette's raised
        // tone, the one every popup is made of, a window is a popup that stays
        list.BeginPart(-1);
        float corner = CornerOf(skin);
        if (skin.TryGetRegion(Background, out _))
            base.Paint(list);
        else
        {
            Vector4 body = Color ?? skin.Palette.Raised;
            list.RoundRect(Bounds, corner, Glass is { } glass ? glass.Over(body) : body);
        }

        // One surface with the title bar, Apple's way, a hairline under the title and round the whole of it in the colour
        // of the skin's edges. It was a darker bar with a line of the accent under it and a frame of the accent round the
        // window, which in Horizon was a gold cage round the settings
        list.BeginPart(-2);
        UIRect bar = TitleBounds;
        list.Rect(new UIRect(bar.Min, new Vector2(bar.Max.X, bar.Min.Y + 1.0f)), skin.BorderColor);
        list.RoundOutline(Bounds, corner, 1.0f, skin.BorderColor);

        float x = bar.Min.X + MathF.Max(10.0f, corner * 0.75f);
        if (Icon.Length > 0)
        {
            float side = MathF.Round(TitleHeight * 0.55f);
            list.Symbol(Icon, new UIRect(new Vector2(x, bar.Center.Y - side * 0.5f), new Vector2(x + side, bar.Center.Y + side * 0.5f)), skin.DimTextColor);
            x += side + 6.0f;
        }

        // The title in bold, it's the heading of what's in it, as a dialog's is
        string title = Resolve(Title);
        float scale = ScaleOf(skin);
        float tall = list.Font.Measure(title, scale, false).Y;
        list.PushClip(new UIRect(bar.Min, new Vector2(bar.Max.X - (Closable ? CLOSE_WIDTH : 0.0f), bar.Max.Y)));
        list.Text(title, new Vector2(x, bar.Center.Y + tall * 0.5f), scale, skin.TextColor, false, UITextStyle.Bold, default);
        list.PopClip();

        list.BeginPart(-3);
        if (Closable)
        {
            UIRect close = CloseBounds;
            bool over = IsHovered && close.Contains(pointer);
            UIWindowButtons.Paint(list, close, "chrome-close", over, over && IsPressed, true, skin.TextColor);
        }

        // The grip, three dots along the curve of the corner, and only while the pointer's on it or it's being dragged.
        // Dots sat square in the corner always, out past the curve of a round one, a corner of a box stuck on a round window
        list.BeginPart(-4);
        if (ResizeGrip)
        {
            UIRect grip = GripBounds;
            bool over = (IsHovered && grip.Contains(pointer)) || drag == Drag.Resize;
            if (over)
            {
                // Round the centre of the corner's curve a little way in from it, from near the bottom edge to near the side
                Vector4 shade = skin.TextColor with { W = drag == Drag.Resize ? 0.7f : 0.45f };
                float bend = MathF.Max(corner, 8.0f);
                Vector2 middle = new(Bounds.Max.X - bend, Bounds.Min.Y + bend);
                float reach = bend - 4.0f;
                for (int i = 0; i < 3; i++)
                {
                    float angle = MathF.PI * (-0.4f + 0.15f * i);
                    Vector2 dot = middle + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * reach;
                    list.RoundRect(new UIRect(dot - new Vector2(1.25f), dot + new Vector2(1.25f)), 1.25f, shade);
                }
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
            Close();

        drag = Drag.None;
    }

    /// <summary>Hides it and says so, what its x does.</summary>
    public void Close()
    {
        Visible = false;
        GiveKeyboardBack();
        OnClosed?.Invoke();
    }

    // Whoever had the keyboard before the window took it, see TakeKeyboard
    private UIComponent? keyboardBefore;

    /// <summary>
    /// Has the keyboard come to the window itself, for one with nothing in it that takes it (About, a page of help), so
    /// the first Escape closes it and no key goes on to what's behind. It took two, the first only let go of the code
    /// under it. Whoever had the keyboard gets it back as the window closes, while it's still somewhere in the window.
    /// </summary>
    public void TakeKeyboard()
    {
        if (Module is not { } module)
            return;

        keyboardBefore = module.Compositor.Focus;
        Focus();
    }

    private void GiveKeyboardBack()
    {
        UIComponent? before = keyboardBefore;
        keyboardBefore = null;
        if (before is not { Module: not null } || Module is not { } module)
            return;

        for (UIComponent? at = module.Compositor.Focus; at is not null; at = at.Parent)
        {
            if (at == this)
            {
                before.Focus();
                return;
            }
        }
    }

    // Escape with the keyboard in it (its filter box, say) closes one with an x, as the x would. The first press only let
    // go of the box and it took a second
    protected internal override bool OnEscape()
    {
        if (BeforeEscape?.Invoke() == true)
            return true;

        if (!Closable || !Visible)
            return false;

        Close();
        return true;
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
