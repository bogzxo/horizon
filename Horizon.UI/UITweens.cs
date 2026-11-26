using System.Numerics;

using Horizon.Core.Tweening;
using Horizon.UI.Components;

namespace Horizon.UI;

/// <summary>
/// Tweens for UI components. Every one of them starts playing straight away on the component's own
/// <see cref="UIComponent.Tweens"/> and hands the handle back for setting up further.
/// <code>
/// panel.PopIn();
/// button.SlideIn(new Vector2(-300, 0), delay: 0.1f);
/// label.TweenOpacity(0.0f, 0.5f).SetLoops(-1, LoopMode.PingPong);
/// </code>
/// What moves is how the component is drawn (<see cref="UIComponent.VisualOffset"/>,
/// <see cref="UIComponent.VisualScale"/> and <see cref="UIComponent.Opacity"/>), which leaves the layout alone.
/// A button that pops in doesn't push its neighbours around while it does. <see cref="TweenPosition"/> and
/// <see cref="TweenSize"/> are there for when the layout is what should move.
/// Starting a tween stops the one that was moving the same thing, so they can be fired off without checking.
/// </summary>
public static class UITweens
{
    // Value-type channels so starting or replacing a tween never boxes an enum.
    private static readonly TweenChannel ChannelOffset = new(1);
    private static readonly TweenChannel ChannelScale = new(2);
    private static readonly TweenChannel ChannelOpacity = new(3);
    private static readonly TweenChannel ChannelPosition = new(4);
    private static readonly TweenChannel ChannelSize = new(5);

    // Cached static getter/setter delegates so every call allocates 0 bytes and creates no closure.
    private static readonly Func<object, Vector2> GetOffset = static target => ((UIComponent)target).VisualOffset;
    private static readonly Action<object, Vector2> SetOffset = static (target, value) => ((UIComponent)target).VisualOffset = value;

    private static readonly Func<object, Vector2> GetScale = static target => ((UIComponent)target).VisualScale;
    private static readonly Action<object, Vector2> SetScale = static (target, value) => ((UIComponent)target).VisualScale = value;

    private static readonly Func<object, float> GetOpacity = static target => ((UIComponent)target).Opacity;
    private static readonly Action<object, float> SetOpacity = static (target, value) => ((UIComponent)target).Opacity = value;

    private static readonly Func<object, Vector2> GetPosition = static target => ((UIComponent)target).Position;
    private static readonly Action<object, Vector2> SetPosition = static (target, value) => ((UIComponent)target).Position = value;

    private static readonly Func<object, Vector2> GetSize = static target => ((UIComponent)target).Size;
    private static readonly Action<object, Vector2> SetSize = static (target, value) => ((UIComponent)target).Size = value;

    /* One property at a time */

    /// <summary>Moves where the component is drawn, relative to where the layout put it.</summary>
    public static TweenHandle TweenOffset(this UIComponent component, Vector2 to, float duration) =>
        component.Tweens.PlayVector2(component, GetOffset, SetOffset, to, duration, ChannelOffset);

    /// <summary>Changes how big the component is drawn, around its middle.</summary>
    public static TweenHandle TweenScale(this UIComponent component, Vector2 to, float duration) =>
        component.Tweens.PlayVector2(component, GetScale, SetScale, to, duration, ChannelScale);

    /// <inheritdoc cref="TweenScale(UIComponent, Vector2, float)"/>
    public static TweenHandle TweenScale(this UIComponent component, float to, float duration) =>
        component.TweenScale(new Vector2(to), duration);

    /// <summary>Changes how see-through the component and everything in it is, 0 being invisible.</summary>
    public static TweenHandle TweenOpacity(this UIComponent component, float to, float duration) =>
        component.Tweens.PlayFloat(component, GetOpacity, SetOpacity, to, duration, ChannelOpacity);

    /// <summary>Moves the component in the layout, whatever is laid out around it follows.</summary>
    public static TweenHandle TweenPosition(this UIComponent component, Vector2 to, float duration) =>
        component.Tweens.PlayVector2(component, GetPosition, SetPosition, to, duration, ChannelPosition);

    /// <summary>Resizes the component in the layout, whatever is laid out around it makes room.</summary>
    public static TweenHandle TweenSize(this UIComponent component, Vector2 to, float duration) =>
        component.Tweens.PlayVector2(component, GetSize, SetSize, to, duration, ChannelSize);

    /* Things a UI does all the time */

    /// <summary>
    /// Makes the component appear by growing out of nothing, a little too far and back.
    /// It is hidden from the moment this is called, also while it waits out its delay.
    /// </summary>
    public static TweenHandle PopIn(this UIComponent component, float duration = 0.35f, float delay = 0.0f, Easing easing = Easing.OutBack)
    {
        component.VisualScale = Vector2.Zero;
        component.Opacity = 0.0f;

        component.TweenOpacity(1.0f, duration * 0.5f).SetEasing(Easing.OutCubic).SetDelay(delay);
        return component.TweenScale(1.0f, duration).SetEasing(easing).SetDelay(delay);
    }

    /// <summary>Makes the component disappear by shrinking to nothing.</summary>
    public static TweenHandle PopOut(this UIComponent component, float duration = 0.25f, float delay = 0.0f)
    {
        component.TweenOpacity(0.0f, duration).SetEasing(Easing.InCubic).SetDelay(delay);
        return component.TweenScale(0.0f, duration).SetEasing(Easing.InBack).SetDelay(delay);
    }

    /// <summary>
    /// Makes the component appear by sliding into its place from somewhere else.
    /// </summary>
    /// <param name="from">Where it comes from, relative to where it belongs, (-300, 0) comes in from the left.</param>
    public static TweenHandle SlideIn(this UIComponent component, Vector2 from, float duration = 0.4f, float delay = 0.0f, Easing easing = Easing.OutCubic)
    {
        component.VisualOffset = from;
        component.Opacity = 0.0f;

        component.TweenOpacity(1.0f, duration * 0.6f).SetEasing(Easing.OutCubic).SetDelay(delay);
        return component.TweenOffset(Vector2.Zero, duration).SetEasing(easing).SetDelay(delay);
    }

    /// <summary>Makes the component disappear by sliding away.</summary>
    /// <param name="to">Where it goes, relative to where it belongs.</param>
    public static TweenHandle SlideOut(this UIComponent component, Vector2 to, float duration = 0.3f, float delay = 0.0f)
    {
        component.TweenOpacity(0.0f, duration).SetEasing(Easing.InCubic).SetDelay(delay);
        return component.TweenOffset(to, duration).SetEasing(Easing.InCubic).SetDelay(delay);
    }

    /// <summary>Makes the component appear out of nothing. Eased, a straight fade looks like it stops dead at the end.</summary>
    public static TweenHandle FadeIn(this UIComponent component, float duration = 0.3f, float delay = 0.0f, Easing easing = Easing.OutCubic)
    {
        component.Opacity = 0.0f;
        return component.TweenOpacity(1.0f, duration).SetEasing(easing).SetDelay(delay);
    }

    public static TweenHandle FadeOut(this UIComponent component, float duration = 0.3f, float delay = 0.0f, Easing easing = Easing.InCubic) =>
        component.TweenOpacity(0.0f, duration).SetEasing(easing).SetDelay(delay);

    /// <summary>
    /// A quick bump in size that settles back to normal, for saying "this one" or "got it".
    /// </summary>
    /// <param name="amount">How much bigger it gets at most, 0.1 being a tenth. Negative squeezes it instead.</param>
    public static TweenHandle Punch(this UIComponent component, float amount = 0.12f, float duration = 0.25f) =>
        component.Tweens.PlayTwoStepVector2(
            component,
            GetScale,
            SetScale,
            new Vector2(1.0f + amount),
            duration * 0.3f,
            Easing.OutQuad,
            Vector2.One,
            duration * 0.7f,
            Easing.OutBack,
            ChannelScale);

    /// <summary>Rattles the component about where it is, dying down until it is still again.</summary>
    /// <param name="strength">How far it gets thrown at most, in the units of the layout.</param>
    public static TweenHandle Shake(this UIComponent component, float strength = 10.0f, float duration = 0.35f) =>
        component.Tweens.PlayShakeVector2(component, SetOffset, Vector2.Zero, strength, duration, ChannelOffset);
}
