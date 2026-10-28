using System.Numerics;

namespace Horizon.UI.Components;

// One bounded search bar above the panes, shared by embedded and standalone terminals.
public partial class TerminalPanel
{
    private StackPanel? findBar;
    private TextBox? findText;
    private Label? findCount;
    private TerminalView? findTarget;
    private float findWait;
    private bool findPending;
    private (int Count, int At, bool Running, bool Limited) foundShown = (-1, -1, false, false);
    private const float FIND_HEIGHT = 34.0f;

    public bool Finding => findBar?.Visible == true;

    /// <summary>Search the current pane without a modal dialog. False when no on-screen pane is available.</summary>
    public bool BeginFind()
    {
        if (Module is null || FocusedView is not { } view)
            return false;
        if (findBar is null)
        {
            findBar = Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 4.0f, Padding = new UIEdges(6.0f, 2.0f), Visible = false });
            findText = findBar.Add(new TextBox { Placeholder = "Find in terminal", LeadIcon = "search", TextScale = 0.14f, MaxLength = 400 });
            findCount = findBar.Add(new Label { Size = new Vector2(72, 28), TextScale = 0.13f });
            findBar.Add(new Button { Icon = "chevron-up", Size = new Vector2(28), Style = "button_flat", Animated = false, Tooltip = "Previous match (Up)", OnPressed = () => StepFind(-1) });
            findBar.Add(new Button { Icon = "chevron-down", Size = new Vector2(28), Style = "button_flat", Animated = false, Tooltip = "Next match (Enter or Down)", OnPressed = () => StepFind(1) });
            findBar.Add(new Button { Icon = "close", Size = new Vector2(28), Style = "button_flat", Animated = false, Tooltip = "Close find (Escape)", OnPressed = CloseFind });
            findText.OnChanged = _ => { findPending = true; findWait = 0.12f; };
            findText.OnSubmitted = _ => StepFind(1);
            findText.OnKey = key => { if (key == UIKeyboard.UP) StepFind(-1); else if (key == UIKeyboard.DOWN) StepFind(1); };
            findText.TakeKey = key =>
            {
                if (key != UIKeyboard.ESCAPE) return false;
                CloseFind();
                return true;
            };
        }
        if (findBar.Parent is null)
            Add(findBar);
        findTarget = view;
        findText!.Text = view.Finding;
        findBar.Visible = true;
        foundShown = (-1, -1, false, false);
        findText.Focus();
        RequestFrame();
        return true;
    }

    /// <summary>Dismisses search, clears its highlights, and returns input to the pane.</summary>
    public void CloseFind()
    {
        TerminalView? target = findTarget;
        HideFind();
        if (target == FocusedView)
            target?.Focus();
    }

    private void HideFind()
    {
        if (findBar is not null)
        {
            if (Module?.Compositor is { } ui && ui.Focus is { } focused && focused.Parent == findBar)
                ui.Focus = null;
            findBar.Visible = false;
            Remove(findBar);
        }
        findTarget?.ClearFind();
        findTarget = null;
        findPending = false;
        RequestFrame();
    }

    private void RunFind()
    {
        if (!findPending || findTarget is null || findText is null)
            return;
        findPending = false;
        findTarget.StartFind(findText.Text);
    }

    private void StepFind(int direction)
    {
        RunFind();
        findTarget?.FindNext(direction);
    }

    private void UpdateFind(float dt)
    {
        if (!Finding)
            return;
        if (findTarget != FocusedView)
        {
            bool hadFocus = findText?.IsFocused == true;
            HideFind();
            if (hadFocus) FocusedView?.Focus();
            return;
        }
        if (findPending && (findWait -= dt) <= 0.0f)
            RunFind();
        if (!findPending)
            findTarget!.AdvanceFind(256, 100_000);
        var position = findTarget!.Found;
        var found = (position.Count, position.At, Running: findTarget.FindRunning, Limited: findTarget.FindLimited);
        if (found != foundShown)
        {
            foundShown = found;
            findCount!.Text = found.Running ? "Searching..." : found.Limited ? "100k+" : found.Count == 0 ? "0 / 0" : $"{found.At + 1} / {found.Count}";
            RequestFrame();
        }
    }

    private void ArrangeFind(UIRect content, float top)
    {
        if (!Finding)
            return;
        findCount!.Visible = content.Width >= 300.0f;
        findText!.Size = new Vector2(MathF.Max(24.0f, content.Width - (findCount.Visible ? 196.0f : 120.0f)), 28.0f);
        findBar!.ArrangeTree(new UIRect(new Vector2(content.Min.X, top - FIND_HEIGHT), new Vector2(content.Max.X, top)));
    }
}
