using System.Numerics;

using Horizon.Core.Tweening;
using Horizon.UI.Scripting;

namespace Horizon.UI.Components;

/// <summary>
/// Which way a component goes when it is put away with <see cref="UIComponent.Show"/>, see <see cref="UIComponent.Slide"/>.
/// In a layout file that is <c>slide: "left"</c>.
/// </summary>
public enum UISlide
{
    /// <summary>It is there or it isn't, Show is Visible.</summary>
    None,

    /// <summary>Off to the left, a side bar on the left of the screen.</summary>
    Left,

    /// <summary>Off to the right, a side bar on the right.</summary>
    Right,

    /// <summary>Up and away, a bar along the top.</summary>
    Up,

    /// <summary>Down and away, a panel along the bottom.</summary>
    Down
}

// A component slid in and out like a drawer. Not the tweens, which move how it is drawn and leave the layout alone, this
// is the room it takes. While it slides the size it asks its parent for goes from all of it to nothing (or back), eased,
// so whatever is next to it in a stack makes room or takes it as it goes. What is in it keeps its whole size, laid out
// where it would be with the part that's gone hanging off the edge it's going out of, and is cut off at the component's
// bounds. A side bar shut to the left has its contents slide left with it, a tab panel shut downwards isn't squashed.
public abstract partial class UIComponent
{
    public const float DEFAULT_SLIDE_TIME = 0.16f;

    // How far out it is, 0 put away and 1 all of it, at a steady rate (eased where it's used), and where it's going while
    // it moves, null when it isn't moving and Visible is all there is to it
    private float slideProgress = 1.0f;
    private bool? slideWanted;

    // How big it is all the way out, measured as it would be if it weren't sliding
    private Vector2 slideFull;

    /// <summary>Which way it goes when <see cref="Show"/> puts it away, and comes from when it brings it back. None, unless it says.</summary>
    public UISlide Slide { get; set; }

    /// <summary>How many seconds a slide takes, in or out. Quick, a drawer that takes its time is one somebody waits for.</summary>
    public float SlideTime { get; set; } = DEFAULT_SLIDE_TIME;

    /// <summary>
    /// Whether it is wanted on screen. True on its way in as well, false on its way out, while <see cref="Visible"/> stays
    /// on until it has slid all the way out.
    /// </summary>
    public bool Shown => slideWanted ?? Visible;

    /// <summary>Whether it is sliding right now.</summary>
    public bool Sliding => slideWanted is not null;

    /// <summary>How much of it is out, eased, 0 put away and 1 all of it. For a program sizing things round it by hand.</summary>
    public float SlideAmount => slideWanted is null ? (Visible ? 1.0f : 0.0f) : Ease.Apply(Easing.OutCubic, slideProgress);

    /// <summary>
    /// Shows it or puts it away, sliding the way <see cref="Slide"/> says, or at once when it says none, when it's told to
    /// be instant (a program putting itself back the way it was left) or its <see cref="SlideTime"/> is 0.
    /// </summary>
    public void Show(bool shown, bool instantly = false)
    {
        if (Slide == UISlide.None || SlideTime <= 0.0f || instantly || (!Visible && !shown))
        {
            slideWanted = null;
            slideProgress = shown ? 1.0f : 0.0f;
            Visible = shown;
            return;
        }

        // Hidden and asked back it comes in from nothing, wherever it was left
        if (!Visible)
            slideProgress = 0.0f;

        Visible = true;
        slideWanted = shown && slideProgress >= 1.0f ? null : shown;
    }

    /// <summary>Shows it if it's put away, puts it away if it's shown, see <see cref="Show"/>.</summary>
    public void ToggleShown() => Show(!Shown);

    private void StepSlide(float dt)
    {
        bool wanted = slideWanted!.Value;
        float step = SlideTime > 0.0f ? dt / SlideTime : 1.0f;
        slideProgress = wanted ? MathF.Min(1.0f, slideProgress + step) : MathF.Max(0.0f, slideProgress - step);

        if (slideProgress is > 0.0f and < 1.0f)
            return;

        slideWanted = null;
        Visible = wanted;
    }

    /// <summary>Helper method to cut what it measured down to how far out it is, along the way it slides.</summary>
    private Vector2 SlidSize(Vector2 measured)
    {
        slideFull = measured;
        float amount = Ease.Apply(Easing.OutCubic, slideProgress);
        return Slide is UISlide.Left or UISlide.Right ? measured with { X = measured.X * amount } : measured with { Y = measured.Y * amount };
    }

    /// <summary>
    /// Helper method for where what's in it is laid out while it slides, its whole size with the part that's gone hanging
    /// off the edge it goes out of. Up is more, so a panel going down shows the top of itself last.
    /// </summary>
    private UIRect SlidContent(UIRect bounds) => Slide switch
    {
        UISlide.Left => new UIRect(new Vector2(bounds.Max.X - slideFull.X, bounds.Min.Y), bounds.Max),
        UISlide.Right => new UIRect(bounds.Min, new Vector2(bounds.Min.X + slideFull.X, bounds.Max.Y)),
        UISlide.Down => new UIRect(new Vector2(bounds.Min.X, bounds.Max.Y - slideFull.Y), bounds.Max),
        UISlide.Up => new UIRect(bounds.Min, new Vector2(bounds.Max.X, bounds.Min.Y + slideFull.Y)),
        _ => bounds
    };

    private void DefineSlideScript()
    {
        Expose(
            "slide",
            () => UIScript.FromEnum(Slide),
            value => Slide = UIScript.ToEnum<UISlide>(value, "slide"));
        Expose("slide_time", () => SlideTime, value => SlideTime = MathF.Max(0.0f, value));
        Expose("shown", () => Shown, value => Show(value));
    }
}
