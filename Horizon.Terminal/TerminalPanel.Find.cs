using System.Numerics;

using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// The find bar, a card over the top right of the pane it searches, the way Dawn's is over its code. It was a bar the
// width of the panel above the panes, and opening it took its height off every pane, so every shell was told it had
// three rows fewer and drew its screen again, and the output under the reader moved. Now nothing under it changes size.
public partial class TerminalPanel
{
    private StackPanel? findBar;
    private TextBox? findText;
    private Label? findCount;
    private TerminalView? findTarget;
    private float findWait;
    private bool findPending;
    private (int Count, int At, bool Running, bool Limited, bool Empty) foundShown = (-1, -1, false, false, false);
    private UISkin? findSkin;

    // The bar's height and how far in from the pane's corner, Dawn's (find_bar.hor) the same
    private const float FIND_ROW = 26.0f, FIND_PADDING = 5.0f, FIND_IN = 8.0f, FIND_QUERY = 240.0f, FIND_COUNT = 86.0f;

    /// <summary>What the find bar says, its placeholder, its count's words and its buttons' tooltips. English as it comes.</summary>
    public TerminalFindWords FindWords { get; set; } = new();

    public bool Finding => findBar?.Visible == true;

    /// <summary>Where the find bar is while it's up, for a test.</summary>
    public UIRect FindBounds => Finding ? findBar!.Bounds : default;

    /// <summary>Search the current pane without a modal dialog. False when no on-screen pane is available.</summary>
    public bool BeginFind()
    {
        if (Module is null || FocusedView is not { } view)
            return false;
        if (findBar is null)
        {
            findBar = Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 2.0f, Padding = new UIEdges(FIND_PADDING), Visible = false, ZOffset = 20, Lift = 16.0f });
            findText = findBar.Add(new TextBox { Placeholder = FindWords.Placeholder, LeadIcon = "search", TextScale = 0.13f, MaxLength = 400, Size = new Vector2(FIND_QUERY, FIND_ROW) });
            findCount = findBar.Add(new Label { Size = new Vector2(FIND_COUNT, FIND_ROW), TextScale = 0.125f, Dim = true });
            findBar.Add(new Button { Icon = "arrow-up", Size = new Vector2(FIND_ROW), Style = "button_flat", Animated = false, Tooltip = FindWords.Previous, OnPressed = () => StepFind(-1) });
            findBar.Add(new Button { Icon = "arrow-down", Size = new Vector2(FIND_ROW), Style = "button_flat", Animated = false, Tooltip = FindWords.Next, OnPressed = () => StepFind(1) });
            findBar.Add(new Button { Icon = "close", Size = new Vector2(FIND_ROW), Style = "button_flat", Animated = false, Tooltip = FindWords.Close, OnPressed = CloseFind });
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
        foundShown = (-1, -1, false, false, false);
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

        // A card of the skin's, its panel's colour with the hairline every popup has, taken again when the skin changes
        if (Module?.Compositor.Skin is { } skin && skin != findSkin)
        {
            findSkin = skin;
            findBar!.Color = skin.PanelColor with { W = 1.0f };
            findBar.Outline = skin.BorderColor;
            findBar.Radius = MathF.Max(0.0f, skin.CornerRadius) + FIND_PADDING;
        }

        if (findPending && (findWait -= dt) <= 0.0f)
            RunFind();
        if (!findPending)
            findTarget!.AdvanceFind(256, 100_000);
        var position = findTarget!.Found;
        var found = (position.Count, position.At, Running: findTarget.FindRunning, Limited: findTarget.FindLimited, Empty: findTarget.Finding.Length == 0);
        if (found != foundShown)
        {
            foundShown = found;
            TerminalFindWords words = FindWords;
            findCount!.Text = found.Empty ? string.Empty
                : found.Running ? words.Searching
                : found.Limited ? words.Many
                : found.Count == 0 ? words.None
                : words.Of(found.At + 1, found.Count);
            RequestFrame();
        }
    }

    /// <summary>The bar over the top right of the pane it searches, as much of its query as the pane leaves room for.</summary>
    private void ArrangeFind()
    {
        if (!Finding || findTarget is not { } view)
            return;

        UIRect pane = view.Bounds;
        float buttons = FIND_ROW * 3.0f + findBar!.Spacing * 4.0f + FIND_PADDING * 2.0f;
        float room = pane.Width - FIND_IN * 2.0f - buttons;
        findCount!.Visible = room >= FIND_COUNT + 120.0f;
        float query = Math.Clamp(room - (findCount.Visible ? FIND_COUNT : 0.0f), 24.0f, FIND_QUERY);
        findText!.Size = new Vector2(query, FIND_ROW);
        float width = query + (findCount.Visible ? FIND_COUNT : 0.0f) + buttons, height = FIND_ROW + FIND_PADDING * 2.0f;
        float right = pane.Max.X - FIND_IN, top = pane.Max.Y - FIND_IN;
        findBar.ArrangeTree(new UIRect(new Vector2(MathF.Max(pane.Min.X, right - width), top - height), new Vector2(right, top)));
    }
}

/// <summary>What the terminal's find bar says, for a program to put in its own words and its own language.</summary>
public sealed class TerminalFindWords
{
    public string Placeholder { get; init; } = "Find in terminal";
    public string Previous { get; init; } = "Previous match (Up)";
    public string Next { get; init; } = "Next match (Enter or Down)";
    public string Close { get; init; } = "Close (Escape)";
    public string Searching { get; init; } = "Searching…";
    public string Many { get; init; } = "100,000+";
    public string None { get; init; } = "No results";

    /// <summary>Which of how many, "3 of 12".</summary>
    public Func<int, int, string> Of { get; init; } = (at, count) => $"{at} of {count}";
}
