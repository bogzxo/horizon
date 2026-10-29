using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// Helper class for the buttons of a window drawn by the UI, the minimise, maximise and close of a <see cref="TitleBar"/>
/// and the close of a <see cref="WindowPanel"/>. A Codicon each, a box behind it under the pointer, red for a close.
/// </summary>
internal static class UIWindowButtons
{
    public static readonly Vector4 CloseRed = new(0.9f, 0.17f, 0.2f, 1.0f);

    /// <param name="corner">How round its top right corner is, the window's own corner when it's the close button in it, 0 for square.</param>
    public static void Paint(UIDrawList list, UIRect area, string icon, bool hovered, bool pressed, bool close, Vector4 color, float corner = 0.0f)
    {
        UISkin skin = list.Skin;
        if (hovered)
        {
            Vector4 fill = close ? CloseRed with { W = pressed ? 0.75f : 1.0f } : pressed ? skin.HoverColor * 1.6f : skin.HoverColor;
            if (corner <= 0.0f)
                list.Rect(area, fill);
            else
                CornerRect(list, area, MathF.Min(corner, MathF.Min(area.Width, area.Height) * 0.5f), fill);
        }

        float side = MathF.Round(MathF.Min(area.Height, area.Width) * 0.42f);
        Vector2 middle = Vector2.Round(area.Center);
        list.Symbol(icon, new UIRect(middle - new Vector2(side * 0.5f), middle + new Vector2(side * 0.5f)), hovered && close ? Vector4.One : color);
    }

    // A box with only its top right corner rounded, in three pieces that don't overlap, so one that's partly see through
    // (a close button held down) has no darker seams. The red of a close in the corner of a round window was a square
    // stuck out past the curve of the window
    private static void CornerRect(UIDrawList list, UIRect area, float radius, Vector4 fill)
    {
        Vector2 bend = new(area.Max.X - radius, area.Max.Y - radius);
        list.Rect(new UIRect(area.Min, new Vector2(bend.X, area.Max.Y)), fill);
        list.Rect(new UIRect(new Vector2(bend.X, area.Min.Y), new Vector2(area.Max.X, bend.Y)), fill);

        list.PushClip(new UIRect(bend, area.Max));
        list.RoundRect(new UIRect(bend - new Vector2(radius), area.Max), radius, fill);
        list.PopClip();
    }
}

/// <summary>
/// The title bar of a window that has none of the system's (<c>WindowManagerConfiguration.CustomFrame</c>), drawn by the
/// UI in its skin. The window's icon and title, its minimise, maximise and close at the right end, and whatever a layout
/// puts in it in between (a menu bar, VS Code's way). Dragging it anywhere nothing else takes the press moves the window,
/// a double click maximises it or brings it back. It works on a window with the system's frame as well, for a
/// fullscreen game that wants a bar of its own.
/// <code>
/// let bar = compositor.title_bar({ title: "Hex", icon: "layout" });
/// let menu = compositor.menu_bar({ parent: bar });
/// </code>
/// </summary>
public class TitleBar : StackPanel
{
    private const float BUTTON_WIDTH = 46.0f;
    private const double DOUBLE_CLICK = 0.4;

    private enum Part { None, Bar, Minimize, Maximize, Close }
    private Part pressedPart;
    private double lastPress = -1.0;

    /// <summary>What it says, the window's own title when it is left empty.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>One of the icons every skin has (<see cref="UISymbols"/>) at the left end, empty for none.</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>The colour of the icon, null for the skin's accent.</summary>
    public Vector4? IconColor { get; set; }

    /// <summary>Whether it has the window's minimise, maximise and close at the right end.</summary>
    public bool WindowButtons { get; set; } = true;

    /// <summary>How tall it is when nothing in it is taller, in the window's pixels (see <see cref="PixelSized"/>).</summary>
    public float Height { get; set; } = 32.0f;

    /// <summary>
    /// Whether its height, its title and its buttons are in the window's pixels rather than the UI's units, so a UI that
    /// is drawn bigger (a bigger screen, <see cref="UICompositor.Scale"/>) doesn't have a title bar that grows with it, the
    /// system's never does. On a screen the system scales (<see cref="UICompositor.DisplayScale"/>) they're that much
    /// bigger, the same as the system's own title bars there. What is put in it is the program's to size.
    /// </summary>
    public bool PixelSized { get; set; } = true;

    // A window's pixel in the UI's units, times the scale of the screen it's on when the window follows that
    private float Unit => PixelSized && Module?.Compositor.Scale is > 0.0f and var scale ? 1.0f / scale : 1.0f;

    /// <summary>
    /// How round the window's top right corner is, in the window's pixels, for the close button in it to be the same shape
    /// under the pointer. Windows 11 rounds a window by 8 pixels, nothing else rounds one the UI hasn't drawn round.
    /// </summary>
    public float WindowCorner { get; set; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? 8.0f : 0.0f;

    private float BarHeight => Height * Unit;
    private float ButtonWidth => BUTTON_WIDTH * Unit;

    /// <summary>The scale of the title, or zero for a little smaller than the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>
    /// What the close does, null for asking the window to close (which a program can say no to, see
    /// <c>WindowManager.CloseRequested</c>).
    /// </summary>
    public Action? OnClose { get; set; }

    public TitleBar()
    {
        Direction = UIDirection.Horizontal;
        Fill = UIFill.Horizontal;
    }

    protected override bool HitTestVisible => true;

    private float ScaleOf(UISkin skin) => (TextScale > 0.0f ? TextScale : skin.TextScale) * Unit;

    // The room at the left for the icon, and at the right for the buttons
    private float IconRoom => Icon.Length > 0 ? BarHeight : 0.0f;
    private float ButtonsRoom => WindowButtons ? ButtonWidth * 3.0f : 0.0f;

    protected override Vector2 Measure(UISkin skin)
    {
        Vector2 inside = base.Measure(skin);
        return new Vector2(inside.X + IconRoom + ButtonsRoom, MathF.Max(inside.Y, BarHeight));
    }

    protected override void Arrange(UIRect content)
    {
        var inside = new UIRect(new Vector2(content.Min.X + IconRoom, content.Min.Y), new Vector2(content.Max.X - ButtonsRoom, content.Max.Y));
        base.Arrange(inside);
        // Right-anchored accessories live beside the window controls, independently of the menus on the left.
        float right = inside.Max.X;
        float gap = Spacing >= 0 ? Spacing : Module?.Compositor.Skin?.Spacing ?? 0;
        for (int i = ChildSpan.Length - 1; i >= 0; i--)
        {
            var child = ChildSpan[i];
            if (!child.Visible || child.Anchor.ToVector().X <= 0) continue;
            var size = child.DesiredSize;
            child.ArrangeTree(UIRect.FromCenter(new Vector2(right - size.X * 0.5f, inside.Center.Y), size));
            right -= size.X + gap;
        }
    }

    /// <summary>Where one of the window's buttons is, in the module's units.</summary>
    public UIRect ButtonBounds(int index)
    {
        float right = Bounds.Max.X - (2 - index) * ButtonWidth;
        return new UIRect(new Vector2(right - ButtonWidth, Bounds.Min.Y), new Vector2(right, Bounds.Max.Y));
    }

    private Part PartAt(Vector2 point)
    {
        if (!Bounds.Contains(point))
            return Part.None;

        if (WindowButtons)
        {
            if (ButtonBounds(0).Contains(point)) return Part.Minimize;
            if (ButtonBounds(1).Contains(point)) return Part.Maximize;
            if (ButtonBounds(2).Contains(point)) return Part.Close;
        }

        return Part.Bar;
    }

    private Vector2 Pointer => Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : default;

    private static bool Maximized => GameEngine.Instance?.WindowManager?.IsMaximized ?? false;

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        float scale = ScaleOf(skin);
        Vector2 pointer = Pointer;

        list.BeginPart(-1);
        list.Rect(Bounds, Color ?? skin.PanelColor with { W = 1.0f });

        if (Icon.Length > 0)
        {
            float side = MathF.Round(BarHeight * 0.5f);
            Vector2 middle = Vector2.Round(new Vector2(Bounds.Min.X + BarHeight * 0.5f, Bounds.Center.Y));
            list.Symbol(Icon, new UIRect(middle - new Vector2(side * 0.5f), middle + new Vector2(side * 0.5f)), IconColor ?? skin.AccentColor);
        }

        // The title in the middle of the window, or after what is in the bar when that runs past where the middle would put it
        list.BeginPart(-2);
        string title = Title.Length > 0 ? Resolve(Title) : GameEngine.Instance?.WindowManager?.Title ?? string.Empty;
        if (title.Length > 0)
        {
            Vector2 size = skin.Font.Measure(title, scale, markup: false);
            float contentEnd = Bounds.Min.X + IconRoom;
            float titleEnd = Bounds.Max.X - ButtonsRoom;
            foreach (UIComponent child in ChildSpan)
            {
                if (child.Visible)
                {
                    if (child.Anchor.ToVector().X > 0)
                        titleEnd = MathF.Min(titleEnd, child.Bounds.Min.X - 16.0f);
                    else
                        contentEnd = MathF.Max(contentEnd, child.Bounds.Max.X);
                }
            }

            float left = MathF.Max(Bounds.Center.X - size.X * 0.5f, contentEnd + 16.0f);
            if (left + size.X < titleEnd)
                list.Text(title, new UIRect(new Vector2(left, Bounds.Min.Y), new Vector2(left + size.X, Bounds.Max.Y)), Origin.Left, scale, skin.TextColor * new Vector4(1, 1, 1, 0.8f), markup: false);
        }

        if (WindowButtons)
        {
            bool down = IsPressed;
            for (int i = 0; i < 3; i++)
            {
                list.BeginPart(i);
                UIRect area = ButtonBounds(i);
                bool over = IsHovered && area.Contains(pointer);
                string icon = i switch { 0 => "chrome-minimize", 1 => Maximized ? "chrome-restore" : "chrome-maximize", _ => "chrome-close" };
                UIWindowButtons.Paint(list, area, icon, over, over && down, i == 2, skin.TextColor, i == 2 && !Maximized ? WindowCorner * Unit : 0.0f);
            }
        }

        // The window is told where its maximise button is, for Windows 11 to bring Snap Layouts up over it, and where the
        // bar is and what on it isn't, for the desktop to move the window by it (SDL's, see WindowManager.HitTest)
        if (GameEngine.Instance?.WindowManager is { CustomFrame: true } window && Module is { } module)
        {
            window.MaximizeButton = WindowButtons ? InWindow(module, ButtonBounds(1)) : default;
            if (Core.WindowManager.UsesSdl)
                window.SetDragArea(Visible ? InWindow(module, Bounds) : default, Holes(module));
        }

        list.EndParts();
    }

    // What on the bar a press goes to rather than moving the window, its buttons and whatever is in it (a menu bar, tabs),
    // into a list kept from one paint to the next
    private readonly List<System.Drawing.RectangleF> holes = [];

    private ReadOnlySpan<System.Drawing.RectangleF> Holes(UIModule module)
    {
        holes.Clear();
        if (WindowButtons)
        {
            for (int i = 0; i < 3; i++)
                holes.Add(InWindow(module, ButtonBounds(i)));
        }

        foreach (UIComponent child in Children)
        {
            if (child.Visible)
                holes.Add(InWindow(module, child.Bounds));
        }

        return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(holes);
    }

    /// <summary>Helper method to say where something of the UI is in the window, in its pixels.</summary>
    private static System.Drawing.RectangleF InWindow(UIModule module, UIRect area)
    {
        var mouse = GameEngine.Instance.Input.Mouse;
        Vector2 a = module.Compositor.viewportCamera.WorldToScreen(module.ToWorld(area.Min)) / mouse.ViewScale + mouse.ViewOrigin;
        Vector2 b = module.Compositor.viewportCamera.WorldToScreen(module.ToWorld(area.Max)) / mouse.ViewScale + mouse.ViewOrigin;
        return System.Drawing.RectangleF.FromLTRB(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
    }

    protected internal override void OnPointerDown(Vector2 point)
    {
        pressedPart = PartAt(point);
        if (pressedPart != Part.Bar)
            return;

        // A double click maximises it or brings it back, a press and a drag moves it
        double now = Environment.TickCount64 / 1000.0;
        if (now - lastPress < DOUBLE_CLICK)
        {
            lastPress = -1.0;
            GameEngine.Instance?.WindowManager?.ToggleMaximized();
            return;
        }

        lastPress = now;
        GameEngine.Instance?.WindowManager?.BeginMove();
    }

    protected internal override void OnClick()
    {
        Part part = pressedPart;
        pressedPart = Part.None;
        if (part == Part.Bar || part != PartAt(Pointer))
            return;

        var window = GameEngine.Instance?.WindowManager;
        switch (part)
        {
            case Part.Minimize:
                window?.Minimize();
                break;
            case Part.Maximize:
                window?.ToggleMaximized();
                break;
            case Part.Close:
                if (OnClose is { } close) close();
                else window?.Close();
                break;
        }
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("title", () => Title, value => Title = value);
        Expose("icon", () => Icon, value => Icon = value);
        Expose("icon_color", () => IconColor ?? Vector4.Zero, value => IconColor = value == Vector4.Zero ? null : value);
        Expose("window_buttons", () => WindowButtons, value => WindowButtons = value);
        Expose("height", () => Height, value => Height = MathF.Max(16.0f, value));
        Expose("pixel_sized", () => PixelSized, value => PixelSized = value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
    }
}
