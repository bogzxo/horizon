using System.Numerics;

namespace Horizon.UI.Components;

/// <summary>
/// The soft dim behind whatever has to be answered before anything else is done (a <see cref="UIDialog"/>, a modal
/// <see cref="WindowPanel"/>), over the whole of its module and taking the pointer, so nothing under it is clicked, hovered
/// or given a tooltip. One shade for all of them, a dialog's was nearly black and a window's was none at all.
/// </summary>
public sealed class UIScrim : Panel
{
    /// <summary>The shade, a third of the way to black, what's behind still readable under it.</summary>
    public static readonly Vector4 Shade = new(0.0f, 0.0f, 0.0f, 0.32f);

    // The window it's behind, gone with it
    private readonly WindowPanel? owner;

    // What had the keyboard in the window, given back to it when the dim is clicked (a click on nothing let go of it)
    private UIComponent? keyboard;

    internal UIScrim(WindowPanel? owner = null)
    {
        this.owner = owner;
        Fill = UIFill.Both;
        Color = Shade;
        Intro = UIIntro.Fade;
        IntroTime = 0.16f;
    }

    protected override bool HitTestVisible => true;

    // The wheel goes nowhere, what's behind doesn't scroll under a question
    protected internal override bool OnScroll(float delta) => true;

    protected override void Update(float dt)
    {
        base.Update(dt);
        if (owner is null)
            return;

        // Gone with the window, closed, hidden or taken out of where it was
        if (owner.Parent != Parent || !owner.Visible || !owner.Modal)
        {
            Parent?.Remove(this);
            return;
        }

        if (Module?.Compositor.Focus is { } focus && Inside(focus, owner))
            keyboard = focus;
    }

    protected internal override void OnPointerDown(Vector2 point)
    {
        if (owner is null)
            return;

        // The keyboard stays in the window, Escape still closes it
        if (keyboard is { Module: not null } had && Inside(had, owner))
            had.Focus();
        else
            owner.Focus();
    }

    private static bool Inside(UIComponent component, UIComponent of)
    {
        for (UIComponent? at = component; at is not null; at = at.Parent)
        {
            if (at == of)
                return true;
        }

        return false;
    }
}
