using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>One tab of a <see cref="TabStrip"/>, a document that is open.</summary>
public sealed class StripTab
{
    /// <summary>What it says, the name of the file.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>One of the icons every skin has (<see cref="UISymbols"/>) in front of the title. Empty for none.</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>The colour of the icon, null for the colour of the title.</summary>
    public Vector4? IconColor { get; set; }

    /// <summary>
    /// The colour of the title, null for the skin's (bright on the open one, dim on the rest). For saying something about
    /// the file at a glance, changed since the last commit say. The one that isn't open is drawn a little fainter.
    /// </summary>
    public Vector4? TitleColor { get; set; }

    /// <summary>What comes up when the pointer rests on it, the whole path of the file say.</summary>
    public string Tooltip { get; set; } = string.Empty;

    /// <summary>Whether it has changes that aren't saved, a dot where its x is until the pointer comes for the x.</summary>
    public bool Modified { get; set; }

    /// <summary>Whatever the program wants to know the tab by.</summary>
    public object? Tag { get; set; }
}

/// <summary>
/// The row of tabs over an editor, a tab a document, VS Code's. The one that is open is lit with a line of the accent
/// along its top, every tab has its icon and its title, an x to close it on the one that is open and the one under
/// the pointer, and a dot instead of the x while it has changes that aren't saved. The middle button closes one too.
/// More than fit scroll along with the wheel, and the one that is open is always brought into sight.
/// <code>
/// strip.Tabs.Add(new StripTab { Title = "menu.hor", Icon = "layout", Tag = document });
/// strip.OnSelected = index => Show(strip.Tabs[index].Tag);
/// strip.OnClose = index => Close(strip.Tabs[index].Tag);
/// strip.OnContextMenu = point => ShowMenuFor(strip.TabAt(point));
/// </code>
/// </summary>
public class TabStrip : UIComponent
{
    private const float TAB_PADDING = 10.0f;
    private const float GAP = 6.0f;
    private const float WHEEL_STEP = 60.0f;

    private float scroll;
    private int pressedTab = -1;
    private bool pressedClose;

    // The tab the middle button went down on, closed if it comes up on the same one, the way a browser's tabs go
    private int middleTab = -1;
    private bool middleWas;

    /// <summary>The tabs, in their order. Change it as documents open and close.</summary>
    public List<StripTab> Tabs { get; } = [];

    /// <summary>Which tab is open, -1 for none.</summary>
    public int Selected { get; set; } = -1;

    /// <summary>Called with the tab that was clicked, to open it.</summary>
    public Action<int>? OnSelected { get; set; }

    /// <summary>Called with the tab whose x was clicked, to close it (or ask first).</summary>
    public Action<int>? OnClose { get; set; }

    /// <summary>
    /// A tab dragged to where another was, with where it was and where it is now, the strip's own list already moved.
    /// Tabs aren't dragged unless this is set.
    /// </summary>
    public Action<int, int>? OnMoved { get; set; }

    /// <summary>Moves a tab, the selected one going along with it, and says so (<see cref="OnMoved"/>).</summary>
    public void Move(int from, int to)
    {
        if (from < 0 || from >= Tabs.Count || to < 0 || to >= Tabs.Count || from == to)
            return;

        StripTab moving = Tabs[from];
        Tabs.RemoveAt(from);
        Tabs.Insert(to, moving);
        if (Selected == from)
            Selected = to;
        else if (Selected > from && Selected <= to)
            Selected--;
        else if (Selected < from && Selected >= to)
            Selected++;
        OnMoved?.Invoke(from, to);
    }

    private bool dragged;

    /// <summary>The scale of the text, or zero for a little smaller than the skin's.</summary>
    public float TextScale { get; set; }

    protected override bool HitTestVisible => true;

    private float ScaleOf(UISkin skin) => TextScale > 0.0f ? TextScale : skin.TextScale;

    private float Line(UISkin skin) => skin.Font.LineHeight * ScaleOf(skin);

    protected override Vector2 Measure(UISkin skin) => new(0.0f, MathF.Round(Line(skin) + TAB_PADDING * 1.6f));

    private float WidthOf(UISkin skin, StripTab tab)
    {
        float line = Line(skin);
        float icon = tab.Icon.Length > 0 ? line + GAP : 0.0f;
        return MathF.Round(TAB_PADDING * 2.0f + icon + skin.Font.Measure(tab.Title, ScaleOf(skin), markup: false).X + GAP + line);
    }

    /// <summary>Where a tab is, in the module's units, scrolled along. Partly or wholly out of sight past the ends of a strip that is too short.</summary>
    public UIRect TabBounds(int index)
    {
        if (Module?.Compositor.Skin is not { } skin || index < 0 || index >= Tabs.Count)
            return default;

        float x = Bounds.Min.X - scroll;
        for (int i = 0; i < index; i++)
            x += WidthOf(skin, Tabs[i]);

        return new UIRect(new Vector2(x, Bounds.Min.Y), new Vector2(x + WidthOf(skin, Tabs[index]), Bounds.Max.Y));
    }

    /// <summary>Where the x that closes a tab is.</summary>
    public UIRect CloseBounds(int index)
    {
        UIRect tab = TabBounds(index);
        if (tab.IsEmpty || Module?.Compositor.Skin is not { } skin)
            return default;

        float side = MathF.Round(Line(skin));
        float right = tab.Max.X - TAB_PADDING + side * 0.25f;
        return new UIRect(new Vector2(right - side, tab.Center.Y - side * 0.5f), new Vector2(right, tab.Center.Y + side * 0.5f));
    }

    /// <summary>Which tab is at a point, in the module's units, -1 for none. For a context menu.</summary>
    public int TabAt(Vector2 point)
    {
        if (!Bounds.Contains(point))
            return -1;

        for (int i = 0; i < Tabs.Count; i++)
        {
            if (TabBounds(i).Contains(point))
                return i;
        }

        return -1;
    }

    private Vector2 Pointer => Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : default;

    protected override void Update(float dt)
    {
        int over = IsHovered ? TabAt(Pointer) : -1;
        Tooltip = over >= 0 ? Tabs[over].Tooltip : string.Empty;

        var mouse = Horizon.Engine.GameEngine.Instance?.Input.Mouse;
        bool middle = mouse is not null && mouse.IsDown(Silk.NET.Input.MouseButton.Middle);
        if (middle && !middleWas)
            middleTab = over;
        else if (!middle && middleWas && middleTab >= 0 && middleTab == over)
            OnClose?.Invoke(middleTab);
        if (!middle)
            middleTab = -1;
        middleWas = middle;

        // The one that is open is brought into sight, whichever way it is off. One wider than the strip is shown from its
        // start, it was shown its end one tick and its start the next and the title shook like a shitting dog
        if (Selected >= 0 && Selected < Tabs.Count && !IsPressed)
        {
            UIRect open = TabBounds(Selected);
            if (open.Min.X < Bounds.Min.X || open.Width > Bounds.Width)
                scroll -= Bounds.Min.X - open.Min.X;
            else if (open.Max.X > Bounds.Max.X)
                scroll += open.Max.X - Bounds.Max.X;
        }

        ClampScroll();
    }

    private void ClampScroll()
    {
        if (Module?.Compositor.Skin is not { } skin)
            return;

        float total = 0.0f;
        foreach (StripTab tab in Tabs)
            total += WidthOf(skin, tab);

        scroll = Math.Clamp(scroll, 0.0f, MathF.Max(0.0f, total - Bounds.Width));
    }

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        float scale = ScaleOf(skin);
        float line = Line(skin);
        Vector2 pointer = Pointer;
        Vector4 dim = skin.DimTextColor;

        list.PushClip(Bounds);

        for (int i = 0; i < Tabs.Count; i++)
        {
            list.BeginPart(i);
            StripTab tab = Tabs[i];
            UIRect area = TabBounds(i);
            bool open = i == Selected;
            bool over = IsHovered && area.Contains(pointer);

            // A pill under the open one, the one pick there is in lists and menus, and a fainter one under the pointer. It
            // was a square of the panel with a stripe of the accent along its top, sharp corners in a window of round ones.
            // Always painted, see-through when not lit, so the quads after it don't shift
            list.Box(area.Shrink(new UIEdges(2.0f, 3.0f)), open ? skin.TextColor with { W = ListBox.PICKED } : over ? skin.HoverColor : skin.HoverColor with { W = 0.0f });

            Vector4 text = tab.TitleColor is { } own ? (open ? own : own with { W = own.W * 0.75f }) : open ? skin.TextColor : dim;
            float x = area.Min.X + TAB_PADDING;
            float side = MathF.Round(line);

            if (tab.Icon.Length > 0)
            {
                list.Symbol(tab.Icon, new UIRect(new Vector2(x, area.Center.Y - side * 0.5f), new Vector2(x + side, area.Center.Y + side * 0.5f)), tab.IconColor ?? text);
                x += side + GAP;
            }

            list.Text(tab.Title, new UIRect(new Vector2(x, area.Min.Y), new Vector2(area.Max.X, area.Max.Y)), Origin.Left, scale, text, markup: false);

            // The x on the one that is open and the one under the pointer, a dot on one with changes otherwise
            UIRect close = CloseBounds(i);
            bool onClose = over && close.Contains(pointer);
            if (onClose)
                list.Box(close, skin.HoverColor);

            if (open || over)
                list.Symbol(tab.Modified && !onClose ? "circle-filled" : "close", close.Shrink(new UIEdges(side * 0.12f)), text);
            else if (tab.Modified)
                list.Symbol("circle-filled", close.Shrink(new UIEdges(side * 0.12f)), text);
        }

        list.EndParts();
        list.PopClip();
    }

    protected internal override void OnPointerDown(Vector2 point)
    {
        pressedTab = TabAt(point);
        pressedClose = pressedTab >= 0 && CloseBounds(pressedTab).Contains(point);
        dragged = false;
    }

    protected internal override void OnPointerDrag(Vector2 point)
    {
        if (OnMoved is null || pressedTab < 0 || pressedClose)
            return;

        int over = TabAt(point);
        if (over >= 0 && over != pressedTab)
        {
            Move(pressedTab, over);
            pressedTab = over;
            dragged = true;
        }
    }

    protected internal override void OnClick()
    {
        int tab = pressedTab;
        bool close = pressedClose && !dragged;
        pressedTab = -1;
        dragged = false;

        if (tab < 0 || tab >= Tabs.Count || TabAt(Pointer) != tab)
            return;

        if (close && CloseBounds(tab).Contains(Pointer))
            OnClose?.Invoke(tab);
        else
            OnSelected?.Invoke(tab);
    }

    protected internal override bool OnScroll(float delta)
    {
        scroll -= delta * WHEEL_STEP;
        ClampScroll();
        return true;
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("text_scale", () => TextScale, value => TextScale = value);
    }
}
