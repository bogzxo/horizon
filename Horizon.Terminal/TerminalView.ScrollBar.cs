using System.Numerics;

using Horizon.Engine;
using Horizon.UI.Drawing;

namespace Horizon.UI.Components;

// The scroll bar down the right edge of a terminal, its thumb as long against the track as the rows in sight are against
// the scrollback and the screen together. Thin and faint as it sits, it widens and brightens under the pointer and while
// it's held, the way ScrollPanel's and the code editor's light up. The thumb is dragged, a press on the track pages
// towards the pointer and goes on paging while it's held, and with shift the thumb jumps to the pointer and is dragged
// from there. Its room is kept whatever the screen so a program going to the alternate screen isn't resized for it, a
// program with the whole screen has no scrollback and gets no bar, nothing there takes a press.
public partial class TerminalView
{
    // How wide its room is, the thumb thin and lit, the least the thumb is, and its part of the paint
    private const float BAR_WIDTH = 10.0f, BAR_THIN = 4.0f, BAR_THICK = 8.0f, MIN_THUMB = 20.0f;
    private const int PART_BAR = -7;

    // How long a press on the track waits before it pages again, and how often it does after that
    private const float PAGE_DELAY = 0.35f, PAGE_REPEAT = 0.05f;

    // How quickly the thumb widens and fades, a share of the way each second
    private const float BAR_EASE = 18.0f;

    // Its room as the paint worked it out, and its track as it was drawn, nothing while there's no bar
    private UIRect barRoom, barTrack;

    // The thumb held, and where, down from its top. The track held, and which way it pages (1 up, -1 down)
    private bool draggingBar;
    private float barGrab;
    private int paging;
    private float pageWait;

    // How lit it is, 0 thin and faint, 1 wide and bright
    private float barLit;

    /// <summary>The track of the scroll bar as it was last drawn, in the view's units, nothing while there's no bar.</summary>
    public UIRect ScrollBarTrack => barTrack;

    /// <summary>Its thumb as things are now, nothing while there's no bar.</summary>
    public UIRect ScrollBarThumb
    {
        get
        {
            if (barTrack.Width <= 0.0f)
                return default;
            var (length, top) = Thumb(barTrack);
            return new UIRect(new Vector2(barTrack.Min.X, top - length), new Vector2(barTrack.Max.X, top));
        }
    }

    /// <summary>How many rows up from the bottom the view is, 0 for following what's written.</summary>
    public int ScrolledUp => scrolled;

    // A bar only where there's a scrollback to go through
    private bool HasScrollBar => !screen.AlternateScreen && screen.Scrollback.Count > 0;

    // How far in from the pane's right edge its room starts, clear of a round corner
    private float SideInset => MathF.Max(2.0f, Radius * 0.35f);

    // What's kept down the right of the cells, the minimap and the bar
    private float SideWidth => SideInset + BAR_WIDTH + MinimapWidth;

    /// <summary>Helper method to lay the bar and the minimap out down the right of the cells, as tall as they are.</summary>
    private void PlaceSide()
    {
        float right = Bounds.Max.X - SideInset;
        barRoom = new UIRect(new Vector2(right - BAR_WIDTH, paintedArea.Min.Y), new Vector2(right, paintedArea.Max.Y));
        minimapArea = Minimap ? new UIRect(new Vector2(barRoom.Min.X - MINIMAP_GAP - MINIMAP_WIDTH, paintedArea.Min.Y), new Vector2(barRoom.Min.X - MINIMAP_GAP, paintedArea.Max.Y)) : default;
    }

    /// <summary>Helper method for the thumb on a track, how long it is and where its top is.</summary>
    private (float Length, float Top) Thumb(UIRect track)
    {
        int most = screen.Scrollback.Count;
        float length = MathF.Min(track.Height, MathF.Max(MIN_THUMB, track.Height * screen.Rows / MathF.Max(1.0f, screen.TotalRows)));
        float share = most > 0 ? (float)RowOffset() / most : 1.0f;
        return (length, track.Max.Y - (track.Height - length) * share);
    }

    /// <summary>Its light eased towards what the pointer says, and the track paged again while it's held.</summary>
    private void UpdateScrollBar(float dt)
    {
        bool over = false;
        if (IsHovered && barTrack.Width > 0.0f && Module is { } module)
        {
            Vector2 pointer = module.ToLocal(module.Compositor.Pointer.Position);
            over = pointer.X >= barTrack.Min.X - 2.0f && pointer.X <= Bounds.Max.X && pointer.Y >= barTrack.Min.Y && pointer.Y <= barTrack.Max.Y;
        }

        float wanted = over || draggingBar || paging != 0 ? 1.0f : 0.0f;
        if (barLit != wanted)
        {
            barLit = Animated ? barLit + (wanted - barLit) * (1.0f - MathF.Exp(-BAR_EASE * dt)) : wanted;
            if (MathF.Abs(barLit - wanted) < 0.01f)
                barLit = wanted;
            RequestFrame();
        }

        if (paging != 0 && (pageWait -= dt) <= 0.0f)
        {
            pageWait = PAGE_REPEAT;
            PageToward(lastPointer);
        }
    }

    private void PaintScrollBar(UIDrawList list, TerminalPalette palette)
    {
        barTrack = HasScrollBar ? barRoom : default;
        if (barTrack.Width <= 0.0f || barTrack.Height <= 0.0f)
            return;

        var (length, top) = Thumb(barTrack);
        float width = BAR_THIN + (BAR_THICK - BAR_THIN) * barLit;
        float middle = barTrack.Center.X;

        // A pill for a skin with round corners, the track there all the time and only seen lit so the quads stay put
        float round = list.Skin.CornerRadius > 0.0f ? width * 0.5f : 0.0f;
        Vector4 text = palette.Foreground;
        list.BeginPart(PART_BAR);
        list.RoundRect(new UIRect(new Vector2(middle - BAR_THICK * 0.5f, barTrack.Min.Y), new Vector2(middle + BAR_THICK * 0.5f, barTrack.Max.Y)), BAR_THICK * 0.5f,
            text with { W = 0.05f * barLit });
        list.RoundRect(new UIRect(new Vector2(middle - width * 0.5f, top - length), new Vector2(middle + width * 0.5f, top)), round,
            text with { W = draggingBar ? 0.5f : 0.2f + 0.12f * barLit });
    }

    /// <summary>A press on the bar's room, the thumb taken hold of or the track paged. False off it.</summary>
    private bool PressScrollBar(Vector2 point)
    {
        if (barRoom.Width <= 0.0f || point.X < barRoom.Min.X - 2.0f || point.X > Bounds.Max.X || point.Y < barRoom.Min.Y || point.Y > barRoom.Max.Y)
            return false;

        if (!HasScrollBar || barTrack.Width <= 0.0f)
            return true;

        var (length, top) = Thumb(barTrack);
        if (point.Y <= top && point.Y >= top - length)
        {
            barGrab = top - point.Y;
            draggingBar = true;
            return true;
        }

        // With shift the thumb's middle goes to the pointer and it's dragged from there
        if (GameEngine.Instance?.Input.Keyboard.Shift == true)
        {
            barGrab = length * 0.5f;
            draggingBar = true;
            DragScrollBar(point);
            return true;
        }

        paging = point.Y > top ? 1 : -1;
        pageWait = PAGE_DELAY;
        PageToward(point);
        return true;
    }

    /// <summary>A page towards a point on the track, nothing once the thumb has got to it.</summary>
    private void PageToward(Vector2 point)
    {
        if (barTrack.Width <= 0.0f)
            return;

        var (length, top) = Thumb(barTrack);
        if (paging > 0 ? point.Y <= top : point.Y >= top - length)
            return;

        ScrollBy(paging * Math.Max(1, screen.Rows - 1));
    }

    /// <summary>The thumb dragged, held where it was taken, the view scrolled to where it is along the track.</summary>
    private void DragScrollBar(Vector2 point)
    {
        int most = screen.Scrollback.Count;
        if (barTrack.Width <= 0.0f || most == 0)
            return;

        var (length, _) = Thumb(barTrack);
        float travel = barTrack.Height - length;
        if (travel <= 0.0f)
            return;

        float share = Math.Clamp((barTrack.Max.Y - (point.Y + barGrab)) / travel, 0.0f, 1.0f);
        ScrollToRow((int)MathF.Round(share * most));
    }

    /// <summary>The bar or the minimap let go of, true when either was held.</summary>
    private bool ReleaseSide()
    {
        bool held = draggingBar || paging != 0 || draggingMinimap;
        draggingBar = draggingMinimap = false;
        paging = 0;
        if (held)
            RequestFrame();
        return held;
    }
}
