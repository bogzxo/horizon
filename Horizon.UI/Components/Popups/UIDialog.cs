using Horizon.Rendering;
using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// One of the things a <see cref="UIDialog"/> offers to do, a button along its bottom.
/// </summary>
/// <param name="Label">What the button says.</param>
/// <param name="Picked">What picking it does, after the dialog has closed. Null for a button that only closes it.</param>
public readonly record struct DialogChoice(string Label, Action? Picked = null);

/// <summary>
/// A question in a box over everything else in its module, a title, a line or two about it and a button for every
/// answer. Nothing behind it can be clicked or walked to until it is answered, and escape is the last answer (the one
/// that changes nothing, by convention). For "save your changes?", "quit?" and the like.
/// <code>
/// UIDialog.Show(module, "Unsaved changes", "The layout has changes that aren't in its file.",
///     new DialogChoice("Save", Save), new DialogChoice("Don't save", Discard), new DialogChoice("Cancel"));
/// </code>
/// One per module at a time, showing another closes the one that is up. It isn't part of any layout file.
/// </summary>
public sealed class UIDialog : Panel
{
    private const float WIDTH = 460.0f;
    // The heading a size up and the line under it what the skin writes, it was seven tenths of that and the hardest
    // thing in a dialog to read
    private const float TITLE_SCALE = 1.25f;
    private const float TEXT_SCALE = 0.95f;

    private static readonly Vector4 Shade = new(0.0f, 0.0f, 0.0f, 0.55f);

    // A palette's shade is a hint of one, it's a quick thing and what's behind it is still what's being worked on, and its
    // card hangs this far under the top of the module rather than in the middle
    private static readonly Vector4 PaletteShade = new(0.02f, 0.03f, 0.05f, 0.22f);
    private const float PALETTE_DROP = 88.0f;

    // Whether it's shown as a palette, see ShowPalette
    private bool palette;

    private readonly StackPanel card;
    private readonly Label title, message;
    private readonly StackPanel buttons;
    private readonly DialogChoice[] choices;

    // What the navigator of the module was on before the dialog took it over, to go back to
    private UIComponent? scopeBefore, selectedBefore;

    /// <summary>Called once the dialog has closed, however it was closed.</summary>
    public Action? Closed { get; set; }

    /// <summary>Whether the dialog is up, and not on its way out.</summary>
    public bool IsOpen => Parent is not null && !closing;

    // Fading out after it was closed, there still and taking nothing
    private bool closing;

    // How long it takes to go, a little quicker than it came
    private const float CLOSE_TIME = 0.12f;

    protected override bool HitTestVisible => !closing && base.HitTestVisible;

    /// <summary>The buttons, one per choice in the order they were given. For tests that want to click one.</summary>
    public IReadOnlyList<Button> Buttons { get; }

    /// <summary>
    /// Between the text and the buttons, empty until somebody puts something in it. This is what makes a dialog
    /// of settings out of a question, rows of whatever there is to set, added after <see cref="Show"/>.
    /// <code>
    /// var dialog = UIDialog.Show(module, "Settings", string.Empty, new DialogChoice("Done"));
    /// dialog.Body.Add(new ToggleButton("Snap to the grid") { OnChanged = on => settings.Snap = on });
    /// </code>
    /// </summary>
    public StackPanel Body { get; }

    private UIDialog(string heading, string text, DialogChoice[] choices)
    {
        this.choices = choices;

        // The shade over everything, which also stops the pointer getting through, eased in with the card rather than
        // slammed on under it
        Fill = UIFill.Both;
        Color = Shade;
        Intro = UIIntro.Fade;
        IntroTime = 0.16f;

        card = Add(new StackPanel
        {
            Background = "panel",
            Padding = new UIEdges(32.0f, 28.0f),
            Spacing = 18.0f,
            Intro = UIIntro.Pop,
            IntroTime = 0.22f
        });

        // The heading in bold, a size up from what's under it is a heading only when it's heavier as well
        title = card.Add(new Label($"[b]{heading}[/b]") { Anchor = Origin.Left, Align = Origin.Left });
        message = card.Add(new Label(text) { Anchor = Origin.Left, Align = Origin.TopLeft, Wrap = true, Size = new Vector2(WIDTH, 0.0f) });

        // A dialog that is only its controls has no line of text taking up room over them
        message.Visible = text.Length > 0;
        Body = card.Add(new StackPanel { Anchor = Origin.Left, Spacing = 10.0f });
        buttons = card.Add(new StackPanel { Direction = UIDirection.Horizontal, Anchor = Origin.Right, Spacing = 12.0f });

        var made = new Button[choices.Length];
        for (int i = 0; i < choices.Length; i++)
        {
            int index = i;
            made[i] = buttons.Add(new Button(choices[i].Label) { OnPressed = () => Pick(index) });
        }

        // The first is the one to press when there's a choice, the last is what Escape picks
        if (made.Length > 1)
            made[0].Primary = true;

        Buttons = made;
    }

    /// <summary>
    /// Shows a dialog over a module. The last choice is what escape picks.
    /// </summary>
    public static UIDialog Show(UIModule module, string title, string message, params DialogChoice[] choices)
    {
        if (choices.Length == 0)
            choices = [new DialogChoice("OK")];

        // Only one at a time
        foreach (var other in module.Root.Children)
        {
            if (other is UIDialog open)
            {
                open.Close();
                break;
            }
        }

        // Whatever was popped up in the module goes, a dialog is the top of it (a code editor's completions drew over a palette)
        module.Popup = null;

        var dialog = new UIDialog(title, message, choices);
        module.AddComponent(dialog);

        // Walking the UI stays inside the dialog while it is up, and starts on its first button
        var navigation = module.Navigation;
        dialog.scopeBefore = navigation.Scope;
        dialog.selectedBefore = navigation.Current;
        navigation.Scope = dialog;
        navigation.SelectFirst();

        dialog.PlayIntro();
        return dialog;
    }

    /// <summary>
    /// Shows a dialog as a palette, Spotlight's and VS Code's, a card hanging under the top of the module with nothing on it
    /// but what is put in its <see cref="Body"/> (a box to type in and a list), no heading, no line and no buttons, over a
    /// shade that hardly darkens what's behind. The choices are still there for whatever presses them, and escape is the
    /// last of them as ever.
    /// </summary>
    public static UIDialog ShowPalette(UIModule module, string title, params DialogChoice[] choices)
    {
        UIDialog dialog = Show(module, title, string.Empty, choices);
        dialog.palette = true;
        dialog.Color = PaletteShade;
        dialog.title.Visible = false;
        dialog.buttons.Visible = false;
        dialog.card.Padding = new UIEdges(10.0f, 10.0f, 10.0f, 10.0f);
        dialog.card.Spacing = 0.0f;
        dialog.card.Anchor = Origin.Top;
        dialog.card.Pivot = Origin.Top;
        dialog.card.Position = new Vector2(0.0f, -PALETTE_DROP);
        dialog.card.IntroTime = 0.16f;
        return dialog;
    }

    /// <summary>Whether it's shown as a palette (<see cref="ShowPalette"/>).</summary>
    public bool IsPalette => palette;

    /// <summary>Closes the dialog as if its last choice had been picked, which is what escape does.</summary>
    public void Cancel() => Pick(choices.Length - 1);

    /// <summary>Takes the dialog down without picking anything.</summary>
    public void Close()
    {
        if (Parent is not { } owner || closing)
            return;

        if (Module is { } module)
        {
            var navigation = module.Navigation;
            if (navigation.Scope == this)
            {
                navigation.Scope = scopeBefore;
                navigation.Select(selectedBefore);
            }
        }

        // The keyboard let go of at once, a box of it typed into while it fades was typing into nothing
        if (Module?.Compositor is { Focus: { } focused } compositor && Contains(focused))
            compositor.Focus = null;

        closing = true;
        Closed?.Invoke();

        // Faded out and shrunk a touch the way it came, then gone, or gone at once with less motion
        if (UIDrawList.Motion && Module is not null)
        {
            card.TweenScale(0.97f, CLOSE_TIME);
            this.FadeOut(CLOSE_TIME, easing: Horizon.Core.Tweening.Easing.OutCubic).OnComplete(() => Parent?.Remove(this));
        }
        else
        {
            owner.Remove(this);
        }
    }

    // Whether a component is inside the dialog
    private bool Contains(UIComponent component)
    {
        for (UIComponent? at = component; at is not null; at = at.Parent)
        {
            if (at == this)
                return true;
        }

        return false;
    }

    private void Pick(int index)
    {
        if (!IsOpen)
            return;

        Close();
        choices[index].Picked?.Invoke();
    }

    protected override Vector2 Measure(UISkin skin)
    {
        // The text sizes are the skin's, scaled for a heading and a line of explanation
        title.TextScale = skin.TextScale * TITLE_SCALE;
        message.TextScale = skin.TextScale * TEXT_SCALE;

        return base.Measure(skin);
    }

    protected override void Paint(UIDrawList list)
    {
        // A skin without art for a panel gets a plain box instead, in whatever colour the skin has for one right
        // now, the skin can change under a dialog that is up (the one its theme is picked in, say)
        card.Color = list.Skin.TryGetRegion(card.Background, out _) ? null : list.Skin.Palette.Raised;

        // On what every popup sits on, as round, with the hairline and the shadow that lift it off the shade
        card.Radius = list.Skin.PopupRadius;
        card.Outline = list.Skin.BorderColor;
        card.Lift = 28.0f;

        // The card is what sits on glass, not the shade over everything else
        card.Glass = Glass ?? Module?.Compositor.PopupGlass;

        base.Paint(list);
    }
}
