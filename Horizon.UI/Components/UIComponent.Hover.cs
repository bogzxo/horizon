using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Scripting;

namespace Horizon.UI.Components;

/// <summary>
/// What a component does while the pointer is over it (or a navigator is on it), see <see cref="UIComponent.Hover"/>.
/// In a layout file that is <c>hover: "grow"</c>, with <c>hover_amount</c> and <c>hover_time</c> to taste.
/// </summary>
public enum UIHover
{
    /// <summary>Nothing, it sits there.</summary>
    None,

    /// <summary>Gets a little bigger, around its middle.</summary>
    Grow,

    /// <summary>Gets a little smaller, as if it was being pushed in.</summary>
    Shrink,

    /// <summary>Rises up off whatever it is on.</summary>
    Lift,

    /// <summary>Steps along to the right, the way the rows of a menu do.</summary>
    Nudge,

    /// <summary>Breathes in and out for as long as the pointer stays.</summary>
    Pulse,

    /// <summary>Lights up, the skin's hover colour laid over it.</summary>
    Glow
}

// How a component answers the pointer resting on it. None of it moves the layout, it is how the thing is drawn, the
// same as the tweens, and the two add up, a button that pops in and grows under the pointer does both.
public abstract partial class UIComponent
{
    // How far into its hover the component is, 0 to 1 and eased when it is used, and how long it has been lit for the pulse
    private float hoverness;
    private float hoverClock;

    // How fast a pulse breathes, in and out this many times a second
    private const float PULSE_RATE = 1.4f;

    /// <summary>
    /// What the component does while the pointer is over it or anything inside it, or a navigator is on it. Nothing,
    /// unless it says so. A card (a panel) with a hover lights up whichever of its buttons is under the pointer.
    /// </summary>
    public UIHover Hover { get; set; }

    /// <summary>
    /// How much of it, 0 for what suits the kind of hover. A fraction of its size for <see cref="UIHover.Grow"/>,
    /// <see cref="UIHover.Shrink"/> and <see cref="UIHover.Pulse"/>, the units of the layout for
    /// <see cref="UIHover.Lift"/> and <see cref="UIHover.Nudge"/>, and times the skin's hover colour for <see cref="UIHover.Glow"/>.
    /// </summary>
    public float HoverAmount { get; set; }

    /// <summary>How many seconds the hover takes to come on, and to go off again.</summary>
    public float HoverTime { get; set; } = DEFAULT_HOVER_TIME;

    public const float DEFAULT_HOVER_TIME = 0.12f;

    /// <summary>
    /// Keeps the hover on whatever the pointer is doing, for an editor showing what it looks like (Hex does, on what is
    /// under the pointer in its canvas, which is a layout being edited and not handed the pointer) or a tutorial
    /// pointing something out.
    /// </summary>
    public bool HoverHeld { get; set; }

    /// <summary>
    /// Whether the component is lit as the chosen one of its group, which lights its hover the way the pointer does. A
    /// navigator standing on it, and for a button whatever set <see cref="Button.Selected"/>, a game walking its own
    /// menu with a pad (Fighter2D's do) has the hover come on with it.
    /// </summary>
    protected virtual bool ShowsAsChosen => IsSelected;

    /// <summary>How far into its hover the component is right now, 0 not at all and 1 all the way.</summary>
    public float HoverProgress => hoverness;

    /// <summary>The amount of a kind of hover when the component doesn't say, see <see cref="HoverAmount"/>.</summary>
    public static float DefaultHoverAmount(UIHover hover) => hover switch
    {
        UIHover.Grow => 0.06f,
        UIHover.Shrink => 0.05f,
        UIHover.Lift => 4.0f,
        UIHover.Nudge => 6.0f,
        UIHover.Pulse => 0.04f,
        UIHover.Glow => 1.0f,
        _ => 0.0f
    };

    private float HoverStrength => HoverAmount != 0.0f ? HoverAmount : DefaultHoverAmount(Hover);

    /// <summary>
    /// Helper method to move the hover along a tick. It comes on at a steady rate and goes off at the same one, so
    /// a pointer sweeping across a row of buttons leaves a little wake of them settling back down.
    /// </summary>
    private void StepHover(float dt)
    {
        bool lit = Hover != UIHover.None && EnabledInHierarchy && (HoverHeld || ShowsAsChosen || PointerWithin());
        float time = MotionTime(HoverTime);
        float step = time > 0.0f ? dt / time : 1.0f;

        hoverness = lit ? MathF.Min(1.0f, hoverness + step) : MathF.Max(0.0f, hoverness - step);

        // The pulse starts from its rest every time, so it swells as the pointer arrives and doesn't jump
        hoverClock = hoverness > 0.0f ? hoverClock + dt : 0.0f;
    }

    /// <summary>
    /// Helper method to say whether the pointer is over this component or anything inside it. The compositor only
    /// marks the topmost thing under it, a button on a card, and the card wants to know as well.
    /// </summary>
    private bool PointerWithin()
    {
        if (IsHovered)
            return true;

        for (UIComponent? at = Module?.Compositor.HoveredComponent; at is not null; at = at.Parent)
        {
            if (at == this)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Helper method to put the hover on top of what the tweens have done to how the component is drawn. Left out of
    /// where the pointer finds it on purpose, a button that lifts away from under the pointer at its bottom edge would
    /// drop back on it and lift again every other tick.
    /// </summary>
    private void ApplyHover(ref Vector2 offset, ref Vector2 scale)
    {
        // Out cubic, quick off the mark and settling gently
        float rest = 1.0f - hoverness;
        float eased = 1.0f - rest * rest * rest;
        float amount = HoverStrength;

        switch (Hover)
        {
            case UIHover.Grow:
                scale *= 1.0f + amount * eased;
                break;

            case UIHover.Shrink:
                scale *= 1.0f - amount * eased;
                break;

            case UIHover.Lift:
                offset.Y += amount * eased;
                break;

            case UIHover.Nudge:
                offset.X += amount * eased;
                break;

            case UIHover.Pulse:
                float swell = 0.5f - 0.5f * MathF.Cos(hoverClock * PULSE_RATE * MathF.Tau);
                scale *= 1.0f + amount * eased * swell;
                break;
        }
    }

    /// <summary>Helper method to lay the glow of a hover over the component, after it painted itself and under its children.</summary>
    private void PaintHoverGlow(UIDrawList list)
    {
        Vector4 glow = list.Skin.HoverColor;
        list.Box(Bounds, glow with { W = MathF.Min(1.0f, glow.W * HoverStrength * hoverness) });
    }

    private void DefineHoverScript()
    {
        Expose(
            "hover",
            () => UIScript.FromEnum(Hover),
            value => Hover = UIScript.ToEnum<UIHover>(value, "hover"));
        Expose("hover_amount", () => HoverAmount, value => HoverAmount = value);
        Expose("hover_time", () => HoverTime, value => HoverTime = MathF.Max(0.0f, value));
    }
}
