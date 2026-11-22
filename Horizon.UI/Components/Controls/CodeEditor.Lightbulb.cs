using System.Numerics;

namespace Horizon.UI.Components;

// The lightbulb in the gutter, on a line the program knows has something to fix, VS Code's and Visual Studio's. The
// program says which line and what a press on it does (the fixes' menu), the editor only draws it in the column of marks
// in place of the line's error or warning, and takes a press on it ahead of the breakpoint the column is otherwise for.
public partial class CodeEditor
{
    /// <summary>The line a lightbulb is drawn on in the gutter, -1 for none. The program keeps it, the caret's line as a rule.</summary>
    public int ActionLine { get; set; } = -1;

    /// <summary>Whether the lightbulb's fix is the one the server would pick itself, drawn with its spark.</summary>
    public bool ActionPreferred { get; set; }

    /// <summary>A press on the lightbulb, the menu of what can be done there opened. Null and nothing is drawn.</summary>
    public Action? OnActionPressed { get; set; }

    /// <summary>Where the lightbulb was last painted, in the editor's module, null when it wasn't (out of sight, none).</summary>
    public UIRect? ActionBounds { get; private set; }

    // Whether the lightbulb is on a line, for the gutter's paint
    private bool ShowsAction(int line) => line == ActionLine && OnActionPressed is not null;

    /// <summary>Whether a point is on the lightbulb in the gutter, its column of marks on the line it's drawn on.</summary>
    public bool ActionAt(Vector2 point)
    {
        if (ActionLine < 0 || OnActionPressed is null || paintedSkin is not { } painted || point.X >= BreakpointStripRight(painted) || point.X < Bounds.Min.X)
            return false;
        if (OverLensRow(point))
            return false;
        return PositionAt(point).Line == ActionLine;
    }

    // A press on the lightbulb opens what it has, ahead of a breakpoint put down there
    private bool PressAction(Vector2 point)
    {
        if (!ActionAt(point))
            return false;
        OnActionPressed!();
        return true;
    }
}
