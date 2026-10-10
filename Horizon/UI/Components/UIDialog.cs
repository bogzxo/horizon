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
    private const float TITLE_SCALE = 1.1f;
    private const float TEXT_SCALE = 0.7f;

    private static readonly Vector4 Shade = new(0.0f, 0.0f, 0.0f, 0.55f);

    private readonly StackPanel card;
    private readonly Label title, message;
    private readonly StackPanel buttons;
    private readonly DialogChoice[] choices;

    // What the navigator of the module was on before the dialog took it over, to go back to
    private UIComponent? scopeBefore, selectedBefore;

    /// <summary>Called once the dialog has closed, however it was closed.</summary>
    public Action? Closed { get; set; }

    /// <summary>Whether the dialog is up.</summary>
    public bool IsOpen => Parent is not null;

    /// <summary>The buttons, one per choice in the order they were given. For tests that want to click one.</summary>
    public IReadOnlyList<Button> Buttons { get; }

    private UIDialog(string heading, string text, DialogChoice[] choices)
    {
        this.choices = choices;

        // The shade over everything, which also stops the pointer getting through
        Fill = UIFill.Both;
        Color = Shade;

        card = Add(new StackPanel
        {
            Background = "panel",
            Padding = new UIEdges(32.0f, 28.0f),
            Spacing = 18.0f,
            Intro = UIIntro.Pop,
            IntroTime = 0.22f
        });

        title = card.Add(new Label(heading) { Anchor = Origin.Left, Align = Origin.Left });
        message = card.Add(new Label(text) { Anchor = Origin.Left, Align = Origin.TopLeft, Wrap = true, Size = new Vector2(WIDTH, 0.0f) });
        buttons = card.Add(new StackPanel { Direction = UIDirection.Horizontal, Anchor = Origin.Right, Spacing = 12.0f });

        var made = new Button[choices.Length];
        for (int i = 0; i < choices.Length; i++)
        {
            int index = i;
            made[i] = buttons.Add(new Button(choices[i].Label) { OnPressed = () => Pick(index) });
        }

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

    /// <summary>Closes the dialog as if its last choice had been picked, which is what escape does.</summary>
    public void Cancel() => Pick(choices.Length - 1);

    /// <summary>Takes the dialog down without picking anything.</summary>
    public void Close()
    {
        if (Parent is not { } owner)
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

        owner.Remove(this);
        Closed?.Invoke();
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
        // A skin without art for a panel gets a plain box instead
        if (!list.Skin.TryGetRegion(card.Background, out _))
            card.Color ??= list.Skin.PanelColor with { W = 1.0f };

        base.Paint(list);
    }
}
