using System.Numerics;

using Horizon.UI.Drawing;

namespace Horizon.UI.Components;

// Little flat buttons along the title bar, left of the x, what the window does besides being looked at (the settings
// opening their file, VS Code's buttons in the top right of its settings). A window without any is the same as ever
public partial class WindowPanel
{
    private const float ACTION_WIDTH = 28.0f;

    // How wide each of them is, as wide as the title bar is tall once that's taller than usual
    private float ActionWidth => MathF.Max(ACTION_WIDTH, MathF.Round(TitleHeight * 1.05f));

    private readonly List<(string Icon, string Tooltip, Action Pressed)> titleActions = [];

    // Which one the pointer went down on, while it's held
    private int pressedAction = -1;

    // The tooltip the window put up for the button under the pointer, taken off again as the pointer leaves it
    private string? actionTooltip;

    /// <summary>How many buttons its title bar has besides the x.</summary>
    public int TitleActions => titleActions.Count;

    /// <summary>
    /// Puts a flat button on the title bar, left of the x and of the ones put there before it (they read left to right in
    /// the order they're added), one of the icons every skin has and what it says when the pointer rests on it.
    /// </summary>
    public void AddTitleAction(string icon, string tooltip, Action pressed) => titleActions.Add((icon, tooltip, pressed));

    /// <summary>What the button at an index says when the pointer rests on it, for a test.</summary>
    public string TitleActionTooltip(int index) => titleActions[index].Tooltip;

    /// <summary>The icon of the button at an index, for a test.</summary>
    public string TitleActionIcon(int index) => titleActions[index].Icon;

    // How much of the title bar the buttons take, right of the title
    private float ActionsWidth => titleActions.Count * ActionWidth;

    /// <summary>Where a button of the title bar is, in the module's units, empty for an index it hasn't got.</summary>
    public UIRect TitleActionBounds(int index)
    {
        if (index < 0 || index >= titleActions.Count)
            return default;

        float right = Bounds.Max.X - (Closable ? CloseWidth : 0.0f) - (titleActions.Count - 1 - index) * ActionWidth;
        return new UIRect(new Vector2(right - ActionWidth, Bounds.Max.Y - TitleHeight), new Vector2(right, Bounds.Max.Y));
    }

    // The button a point is on, -1 for none
    private int ActionAt(Vector2 point)
    {
        for (int i = 0; i < titleActions.Count; i++)
        {
            if (TitleActionBounds(i).Contains(point))
                return i;
        }

        return -1;
    }

    private void PaintTitleActions(UIDrawList list, Vector2 pointer)
    {
        for (int i = 0; i < titleActions.Count; i++)
        {
            UIRect area = TitleActionBounds(i);
            bool over = IsHovered && area.Contains(pointer);
            UIWindowButtons.Paint(list, area, titleActions[i].Icon, over, over && IsPressed && pressedAction == i, false,
                over ? list.Skin.TextColor : list.Skin.DimTextColor);
        }
    }

    protected override void Update(float dt)
    {
        base.Update(dt);
        KeepScrim();
        if (titleActions.Count == 0)
            return;

        // The tooltip of the button under the pointer is the window's own while it's there, the compositor shows whatever
        // the component under the pointer says
        int over = IsHovered ? ActionAt(Pointer) : -1;
        string? wanted = over >= 0 ? Resolve(titleActions[over].Tooltip) : null;
        if (wanted is not null && Tooltip != wanted)
        {
            Tooltip = wanted;
            actionTooltip = wanted;
        }
        else if (wanted is null && actionTooltip is not null)
        {
            if (Tooltip == actionTooltip)
                Tooltip = string.Empty;
            actionTooltip = null;
        }
    }
}
