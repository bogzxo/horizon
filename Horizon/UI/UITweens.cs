using System.Numerics;

using Horizon.Core.Tweening;
using Horizon.UI.Components;

namespace Horizon.UI;

/// <summary>
/// Tweens for UI components. Every one of them starts playing straight away on the component's own
/// <see cref="UIComponent.Tweens"/> and hands the tween back for setting up further:
/// <code>
/// panel.PopIn();
/// button.SlideIn(new Vector2(-300, 0), delay: 0.1f);
/// label.TweenOpacity(0.0f, 0.5f).SetLoops(-1, LoopMode.PingPong);
/// </code>
/// What moves is how the component is drawn (<see cref="UIComponent.VisualOffset"/>,
/// <see cref="UIComponent.VisualScale"/> and <see cref="UIComponent.Opacity"/>), which leaves the layout alone:
/// a button that pops in doesn't push its neighbours around while it does. <see cref="TweenPosition"/> and
/// <see cref="TweenSize"/> are there for when the layout is what should move.
/// Starting a tween stops the one that was moving the same thing, so they can be fired off without checking.
/// </summary>
public static class UITweens
{
    // What a tween is about. Only one tween at a time gets to move each of these on a component.
    private enum Channel
    {
        Offset,
        Scale,
        Opacity,
        Position,
        Size
    }

    /* One property at a time */

    /// <summary>Moves where the component is drawn, relative to where the layout put it.</summary>
    public static Tween TweenOffset(this UIComponent component, Vector2 to, float duration) =>
        component.Tweens.Play(
            Tween.To(() => component.VisualOffset, value => component.VisualOffset = value, to, duration),
            Channel.Offset);

    /// <summary>Changes how big the component is drawn, around its middle.</summary>
    public static Tween TweenScale(this UIComponent component, Vector2 to, float duration) =>
        component.Tweens.Play(
            Tween.To(() => component.VisualScale, value => component.VisualScale = value, to, duration),
            Channel.Scale);

    /// <inheritdoc cref="TweenScale(UIComponent, Vector2, float)"/>
    public static Tween TweenScale(this UIComponent component, float to, float duration) =>
        component.TweenScale(new Vector2(to), duration);

    /// <summary>Changes how see-through the component and everything in it is, 0 being invisible.</summary>
    public static Tween TweenOpacity(this UIComponent component, float to, float duration) =>
        component.Tweens.Play(
            Tween.To(() => component.Opacity, value => component.Opacity = value, to, duration),
            Channel.Opacity);

    /// <summary>Moves the component in the layout, whatever is laid out around it follows.</summary>
    public static Tween TweenPosition(this UIComponent component, Vector2 to, float duration) =>
        component.Tweens.Play(
            Tween.To(() => component.Position, value => component.Position = value, to, duration),
            Channel.Position);

    /// <summary>Resizes the component in the layout, whatever is laid out around it makes room.</summary>
    public static Tween TweenSize(this UIComponent component, Vector2 to, float duration) =>
        component.Tweens.Play(
            Tween.To(() => component.Size, value => component.Size = value, to, duration),
            Channel.Size);

    /* Things a UI does all the time */

    /// <summary>
    /// Makes the component appear by growing out of nothing, a little too far and back.
    /// It is hidden from the moment this is called, also while it waits out its delay.
    /// </summary>
    public static Tween PopIn(this UIComponent component, float duration = 0.35f, float delay = 0.0f)
    {
        component.VisualScale = Vector2.Zero;
        component.Opacity = 0.0f;

        component.TweenOpacity(1.0f, duration * 0.5f).SetDelay(delay);
        return component.TweenScale(1.0f, duration).SetEasing(Easing.OutBack).SetDelay(delay);
    }

    /// <summary>Makes the component disappear by shrinking to nothing.</summary>
    public static Tween PopOut(this UIComponent component, float duration = 0.25f, float delay = 0.0f)
    {
        component.TweenOpacity(0.0f, duration).SetEasing(Easing.InCubic).SetDelay(delay);
        return component.TweenScale(0.0f, duration).SetEasing(Easing.InBack).SetDelay(delay);
    }

    /// <summary>
    /// Makes the component appear by sliding into its place from somewhere else.
    /// </summary>
    /// <param name="from">Where it comes from, relative to where it belongs: (-300, 0) comes in from the left.</param>
    public static Tween SlideIn(this UIComponent component, Vector2 from, float duration = 0.4f, float delay = 0.0f)
    {
        component.VisualOffset = from;
        component.Opacity = 0.0f;

        component.TweenOpacity(1.0f, duration * 0.6f).SetDelay(delay);
        return component.TweenOffset(Vector2.Zero, duration).SetEasing(Easing.OutCubic).SetDelay(delay);
    }

    /// <summary>Makes the component disappear by sliding away.</summary>
    /// <param name="to">Where it goes, relative to where it belongs.</param>
    public static Tween SlideOut(this UIComponent component, Vector2 to, float duration = 0.3f, float delay = 0.0f)
    {
        component.TweenOpacity(0.0f, duration).SetEasing(Easing.InCubic).SetDelay(delay);
        return component.TweenOffset(to, duration).SetEasing(Easing.InCubic).SetDelay(delay);
    }

    public static Tween FadeIn(this UIComponent component, float duration = 0.3f, float delay = 0.0f)
    {
        component.Opacity = 0.0f;
        return component.TweenOpacity(1.0f, duration).SetDelay(delay);
    }

    public static Tween FadeOut(this UIComponent component, float duration = 0.3f, float delay = 0.0f) =>
        component.TweenOpacity(0.0f, duration).SetDelay(delay);

    /// <summary>
    /// A quick bump in size that settles back to normal, for saying "this one" or "got it".
    /// </summary>
    /// <param name="amount">How much bigger it gets at most, 0.1 being a tenth. Negative squeezes it instead.</param>
    public static Tween Punch(this UIComponent component, float amount = 0.12f, float duration = 0.25f)
    {
        Vector2 Get() => component.VisualScale;
        void Set(Vector2 value) => component.VisualScale = value;

        return component.Tweens.Play(
            Tween.Sequence()
                .Append(Tween.To(Get, Set, new Vector2(1.0f + amount), duration * 0.3f).SetEasing(Easing.OutQuad))
                .Append(Tween.To(Get, Set, Vector2.One, duration * 0.7f).SetEasing(Easing.OutBack))
                .Build(),
            Channel.Scale);
    }

    /// <summary>Rattles the component about where it is, dying down until it is still again.</summary>
    /// <param name="strength">How far it gets thrown at most, in the units of the layout.</param>
    public static Tween Shake(this UIComponent component, float strength = 10.0f, float duration = 0.35f)
    {
        float left = 1.0f;

        return component.Tweens.Play(
            Tween.To(
                () => left,
                value =>
                {
                    left = value;
                    component.VisualOffset = new Vector2(
                        Random.Shared.NextSingle() * 2.0f - 1.0f,
                        Random.Shared.NextSingle() * 2.0f - 1.0f) * strength * left;
                },
                0.0f,
                duration),
            Channel.Offset);
    }
}
