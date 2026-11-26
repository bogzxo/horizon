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

    /// <summary>
    /// What is written after the title, smaller and quieter: the name of the program whose window this is, where the
    /// title is what's open in it ("Program.cs" and then "Hex3"). Empty for none. It gives way first when room is short,
    /// the title is what somebody is looking for.
    /// </summary>
    public string Caption { get; set; } = string.Empty;

    // How much smaller than the title its caption is written, how see-through, and the gap between the two
    private const float CAPTION_SCALE = 0.86f, CAPTION_ALPHA = 0.42f, CAPTION_GAP = 10.0f;

    // The least room a caption is cut down to fit, in less it isn't written at all. At 48 a narrow window's caption was
    // its first two letters and an ellipsis, which said nothing
    private const float CAPTION_LEAST = 120.0f;

    private (string Text, UIFont? Font, float Scale, float Room) captionFitFor;
    private string fittedCaption = string.Empty;

    // The caption cut to the room it has, kept for as long as nothing about it changes like the title's
    private string FitCaption(string caption, UIFont font, float scale, float room)
    {
        var key = (caption, font, scale, room);
        if (captionFitFor == key)
            return fittedCaption;
        captionFitFor = key;
        float dots = font.Measure("…", scale, markup: false).X;
        int length = caption.Length;
        while (length > 0 && font.Measure(caption.AsSpan(0, length), scale, markup: false).X > room - dots)
            length--;
        if (length > 0 && char.IsHighSurrogate(caption[length - 1]))
            length--;
        return fittedCaption = length > 0 ? caption[..length].TrimEnd() + "…" : string.Empty;
    }
    private (string Text, UIFont? Font, float Scale, float Room) titleFitFor;
    private string fittedTitle = string.Empty;

    private string FitTitle(string title, UIFont font, float scale, float room)
    {
        var key = (title, font, scale, room);
        if (titleFitFor == key)
            return fittedTitle;
        titleFitFor = key;
        if (font.Measure(title, scale, markup: false).X <= room)
            return fittedTitle = title;
        float dots = font.Measure("…", scale, markup: false).X;
        if (room <= dots)
            return fittedTitle = string.Empty;
        int length = title.Length;
        while (length > 0 && font.Measure(title.AsSpan(0, length), scale, markup: false).X > room - dots)
            length--;
        if (length > 0 && char.IsHighSurrogate(title[length - 1]))
            length--;
        return fittedTitle = title[..length] + "…";
    }

    /// <summary>One of the icons every skin has (<see cref="UISymbols"/>) at the left end, empty for none.</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>An image used instead of the symbol, tinted with <see cref="IconColor"/> or the skin's accent.</summary>
    public string IconFile { get; set; } = string.Empty;

    /// <summary>The colour of the icon, null for the skin's accent.</summary>
    public Vector4? IconColor { get; set; }

    /// <summary>Whether it has the window's minimise, maximise and close at the right end.</summary>
    public bool WindowButtons { get; set; } = true;

    // Only where there's a window to minimise and maximise, a phone's bar has no business pretending, and not where the
    // system draws them itself (a Mac's traffic lights). Without an engine (a test laying the bar out) it's what it was told
    private bool ShowsWindowButtons => WindowButtons &&
        (GameEngine.Instance?.WindowManager is not { } window || ((window.Features & Horizon.Platform.PlatformFeatures.Window) != 0 && !window.NativeChrome));

    /// <summary>
    /// How much room to leave empty at the left end, in the window's pixels, on top of what the system's own frame controls
    /// cover (a Mac's traffic lights, see <see cref="Horizon.Core.WindowManager.FrameInsets"/>, which is 0 in fullscreen).
    /// </summary>
    public float LeadingInset { get; set; }

    // The room at the left end that the window's frame controls and LeadingInset take, in the UI's units
    private float LeadingRoom
    {
        get
        {
            float pixels = LeadingInset + (GameEngine.Instance?.WindowManager?.FrameInsets.Leading ?? 0.0f);
            return LeadingRoomFor(pixels, Module?.Compositor.UIScale ?? 1.0f);
        }
    }

    /// <summary>The room at the left end the window's pixels take, in the UI's units, where a unit is <paramref name="uiScale"/> pixels.</summary>
    public static float LeadingRoomFor(float pixels, float uiScale) => pixels > 0.0f && uiScale > 0.0f ? pixels / uiScale : 0.0f;

    private float reportedBar = -1.0f;

    /// <summary>How tall it is when nothing in it is taller, in the window's pixels (see <see cref="PixelSized"/>).</summary>
    public float Height { get; set; } = 32.0f;

    /// <summary>
    /// Whether its height, its title and its buttons are in the window's pixels rather than the UI's units, so a UI that
    /// is drawn bigger (a bigger screen, <see cref="UICompositor.Scale"/>) doesn't have a title bar that grows with it, the
    /// system's never does. On a screen the system scales (<see cref="UICompositor.DisplayScale"/>) they're that much
    /// bigger, the same as the system's own title bars there. What is put in it is the program's to size.
    /// <para>
    /// The one thing of a UI that stays out of its interface size, which is what a game's player expects of a window's
    /// frame. A program whose title bar is its own menus (an editor's) switches it off, or its menus are the only words
    /// that don't grow when the person asks for everything bigger.
    /// </para>
    /// </summary>
    public bool PixelSized { get; set; } = true;

    // A window's pixel in the UI's units, times the scale of the screen it's on when the window follows that
    private float Unit
    {
        get
        {
            if (!PixelSized || Module is not { } module)
                return 1.0f;

            UICompositor compositor = module.Compositor;
            float uiScale = module.UnitScale > 0.0f ? module.UnitScale : compositor.UIScale;
            float displayScale = compositor.DesignSize is null && module.DesignSize is null
                ? compositor.DisplayScale
                : GameEngine.Instance?.WindowManager is { ScalesToDisplay: true } scaled
                    ? scaled.DisplayScale
                    : 1.0f;
            return UnitFor(true, uiScale, displayScale);
        }
    }

    /// <summary>
    /// Helper method to compute how many UI layout units equal one display-scaled window pixel for a title bar.
    /// Accounts for HiDPI <c>ScaleToDisplay</c>, <see cref="UICompositor.DesignSize"/>, and <see cref="UICompositor.Scale"/>.
    /// </summary>
    internal static float UnitFor(bool pixelSized, float uiScale, float displayScale)
    {
        if (!pixelSized || uiScale <= 0.0f)
            return 1.0f;

        float density = displayScale > 0.0f ? displayScale : 1.0f;
        return density / uiScale;
    }

    /// <summary>
    /// How round the window's top right corner is, in the window's pixels, for the close button in it to be the same shape
    /// under the pointer. Windows 11 rounds a window by 8 pixels, nothing else rounds one the UI hasn't drawn round.
    /// </summary>
    public float WindowCorner { get; set; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? 8.0f : 0.0f;

    // At least as tall as the system's own title bar where it draws the window's frame controls, they are centred in it
    // (exactly as tall where the system says so, a Mac's full screen, whose own bar slides down over this one)
    private float BarHeight
    {
        get
        {
            var insets = GameEngine.Instance?.WindowManager?.FrameInsets ?? default;
            float system = LeadingRoomFor(insets.Height, Module?.Compositor.UIScale ?? 1.0f);
            return insets.Exact && system > 0.0f ? system : MathF.Max(Height * Unit, system);
        }
    }
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
    private float IconRoom => (ShowsIcon ? BarHeight : 0.0f) + LeadingRoom;

    // Not in the system's full screen on a Mac, where the traffic lights come down over the bar's left end
    private bool ShowsIcon => (Icon.Length > 0 || IconFile.Length > 0) &&
        !(GameEngine.Instance?.WindowManager is { NativeChrome: true } window && window.FrameInsets.Height <= 0.0f);
    private float ButtonsRoom => (ShowsWindowButtons ? ButtonWidth * 3.0f : 0.0f) + TrailingRoom;

    // The room the window's rounded corner takes at the right end, where the system draws the frame controls at the left
    private float TrailingRoom => LeadingRoomFor(GameEngine.Instance?.WindowManager?.FrameInsets.Trailing ?? 0.0f, Module?.Compositor.UIScale ?? 1.0f);

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

        // The menus have what's left before those, the ones that don't fit go into the bar's last menu. They were laid
        // out as wide as all of them and ran under the buttons at the right end at 200%
        if (Module?.Compositor.Skin is not { } skin)
            return;

        for (int i = 0; i < ChildSpan.Length; i++)
        {
            if (ChildSpan[i] is not MenuBar { Visible: true } bar || bar.Anchor.ToVector().X > 0)
                continue;

            UIRect at = bar.Bounds;
            bar.Room = MathF.Max(1.0f, MathF.Floor(right - at.Min.X));
            float width = MathF.Min(bar.ShownWidth(skin), bar.Room);
            if (MathF.Abs(width - at.Width) > 0.5f)
                bar.ArrangeTree(new UIRect(at.Min, new Vector2(at.Min.X + width, at.Max.Y)));
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

        if (ShowsWindowButtons)
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

        // The system's frame controls are centred in the bar, which they are told the height of (in the window's pixels)
        if (Visible && GameEngine.Instance?.WindowManager is { NativeChrome: true } native && Module is { } own)
        {
            float pixels = Bounds.Height * own.Compositor.UIScale;
            if (MathF.Abs(pixels - reportedBar) > 0.5f)
            {
                reportedBar = pixels;
                native.SetTitleBarHeight(pixels);
            }
        }

        list.BeginPart(-1);
        list.Rect(Bounds, Color ?? skin.PanelColor with { W = 1.0f });

        if (ShowsIcon)
        {
            float side = MathF.Round(BarHeight * 0.5f);
            Vector2 middle = Vector2.Round(new Vector2(Bounds.Min.X + LeadingRoom + BarHeight * 0.5f, Bounds.Center.Y));
            var area = new UIRect(middle - new Vector2(side * 0.5f), middle + new Vector2(side * 0.5f));
            Vector4 tint = IconColor ?? skin.AccentColor;
            if (IconFile.Length > 0)
            {
                // A title-bar image is small even on a high-DPI screen; keep large source art out of the UI atlas.
                if (skin.TryGetImage(IconFile, 128, out var image)) list.NineSlice(image, area, tint);
            }
            else list.Symbol(Icon, area, tint);
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
                        titleEnd = MathF.Min(titleEnd, child.Bounds.Min.X - 16.0f * Unit);
                    else
                        contentEnd = MathF.Max(contentEnd, child.Bounds.Max.X);
                }
            }

            float start = contentEnd + 16.0f * Unit;
            float room = MathF.Max(0, titleEnd - start);

            // The caption after it, when the two fit whole. The pair is centred as one, so the title sits a little left
            // of the middle and reads as the first of two things and not as a title with something stuck to it
            string caption = Caption.Length > 0 ? Resolve(Caption) : string.Empty;
            float captionScale = scale * CAPTION_SCALE, gap = CAPTION_GAP * Unit;
            Vector2 captionSize = caption.Length > 0 ? skin.Font.Measure(caption, captionScale, markup: false) : Vector2.Zero;

            // The title has the room first. The caption gets what's left and is cut short with dots when that isn't
            // all of it (a long note after the program's name), and goes altogether when there's hardly any
            string shown = FitTitle(title, skin.Font, scale, room);
            size = skin.Font.Measure(shown, scale, markup: false);
            float captionRoom = room - size.X - gap;
            if (caption.Length > 0 && captionSize.X > captionRoom)
            {
                caption = captionRoom >= CAPTION_LEAST * Unit ? FitCaption(caption, skin.Font, captionScale, captionRoom) : string.Empty;
                captionSize = caption.Length > 0 ? skin.Font.Measure(caption, captionScale, markup: false) : Vector2.Zero;
            }

            bool captioned = caption.Length > 0;
            float whole = size.X + (captioned ? gap + captionSize.X : 0.0f);
            float left = MathF.Max(start, MathF.Min(Bounds.Center.X - whole * 0.5f, titleEnd - whole));
            if (shown.Length > 0)
                list.Text(shown, new UIRect(new Vector2(left, Bounds.Min.Y), new Vector2(left + size.X, Bounds.Max.Y)), Origin.Left, scale, skin.TextColor * new Vector4(1, 1, 1, 0.8f), markup: false);
            if (captioned)
            {
                float at = left + size.X + gap;
                list.Text(caption, new UIRect(new Vector2(at, Bounds.Min.Y), new Vector2(at + captionSize.X, Bounds.Max.Y)), Origin.Left, captionScale,
                    skin.TextColor * new Vector4(1, 1, 1, CAPTION_ALPHA), markup: false);
            }
        }

        if (ShowsWindowButtons)
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
            window.MaximizeButton = ShowsWindowButtons ? InWindow(module, ButtonBounds(1)) : default;
            if ((window.Features & Horizon.Platform.PlatformFeatures.DragArea) != 0)
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
        if (ShowsWindowButtons)
        {
            for (int i = 0; i < 3; i++)
                holes.Add(InWindow(module, ButtonBounds(i)));
        }

        foreach (UIComponent child in ChildSpan)
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
        Expose("caption", () => Caption, value => Caption = value);
        Expose("icon", () => Icon, value => Icon = value);
        Expose("icon_file", () => IconFile, value => IconFile = value);
        Expose("icon_color", () => IconColor ?? Vector4.Zero, value => IconColor = value == Vector4.Zero ? null : value);
        Expose("window_buttons", () => WindowButtons, value => WindowButtons = value);
        Expose("height", () => Height, value => Height = MathF.Max(16.0f, value));
        Expose("pixel_sized", () => PixelSized, value => PixelSized = value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
    }
}
