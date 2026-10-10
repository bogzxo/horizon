using System.Numerics;

using Horizon.Core.Tweening;

namespace Horizon.Rendering.Spriting;

/// <summary>
/// Tweens for sprites. Every one of them starts playing straight away on the sprite's own
/// <see cref="Sprite.Tweens"/> and hands the tween back for setting up further.
/// <code>
/// sprite.TweenPosition(new Vector2(200, 0), 0.5f).SetEasing(Easing.OutCubic);
/// sprite.PopIn();
/// sprite.TweenRotation(360, 2.0f).SetLoops(-1);
/// </code>
/// Starting a tween stops the one that was moving the same thing, so they can be fired off without checking.
/// </summary>
public static class SpriteTweens
{
    // What a tween is about. Only one tween at a time gets to move each of these on a sprite.
    private enum Channel
    {
        Position,
        Size,
        Rotation,
        Tint,
        Flash
    }

    public static Tween TweenPosition(this Sprite sprite, Vector2 to, float duration) =>
        sprite.Tweens.Play(
            Tween.To(() => sprite.Transform.Position, value => sprite.Transform.Position = value, to, duration),
            Channel.Position);

    /// <summary>Resizes the sprite. A negative width is a flipped sprite, see <see cref="Sprite.Flipped"/>.</summary>
    public static Tween TweenSize(this Sprite sprite, Vector2 to, float duration) =>
        sprite.Tweens.Play(
            Tween.To(() => sprite.Transform.Size, value => sprite.Transform.Size = value, to, duration),
            Channel.Size);

    /// <summary>Turns the sprite, in degrees.</summary>
    public static Tween TweenRotation(this Sprite sprite, float to, float duration) =>
        sprite.Tweens.Play(
            Tween.To(() => sprite.Transform.Rotation, value => sprite.Transform.Rotation = value, to, duration),
            Channel.Rotation);

    /// <summary>
    /// Lights the sprite up in a colour and lets it fade back to how it was, for something that just got hit.
    /// A tint can't do this (it only ever darkens), see <see cref="Sprite.FlashColor"/>.
    /// </summary>
    /// <param name="duration">How long the fade takes, in seconds.</param>
    /// <param name="amount">How much of the colour there is to begin with, 1 being nothing but the colour.</param>
    public static Tween Flash(this Sprite sprite, Vector4 color, float duration, float amount = 1.0f)
    {
        sprite.FlashColor = color;
        sprite.FlashAmount = amount;

        return sprite.Tweens.Play(
            Tween.To(() => sprite.FlashAmount, value => sprite.FlashAmount = value, 0.0f, duration).SetEasing(Easing.OutQuad),
            Channel.Flash);
    }

    /// <summary>Changes the colour the sprite is multiplied with, alpha included.</summary>
    public static Tween TweenTint(this Sprite sprite, Vector4 to, float duration) =>
        sprite.Tweens.Play(
            Tween.To(() => sprite.Tint, value => sprite.Tint = value, to, duration),
            Channel.Tint);

    /// <summary>Changes how see-through the sprite is and leaves its colour alone, 0 being invisible.</summary>
    public static Tween TweenOpacity(this Sprite sprite, float to, float duration) =>
        sprite.Tweens.Play(
            Tween.To(() => sprite.Tint.W, value => sprite.Tint = sprite.Tint with { W = value }, to, duration),
            Channel.Tint);

    /// <summary>
    /// Makes the sprite appear by growing out of nothing to the size it has now, a little too far and back.
    /// It is hidden from the moment this is called, also while it waits out its delay.
    /// </summary>
    public static Tween PopIn(this Sprite sprite, float duration = 0.35f, float delay = 0.0f)
    {
        Vector2 size = sprite.Transform.Size;
        sprite.Transform.Size = Vector2.Zero;

        return sprite.TweenSize(size, duration).SetEasing(Easing.OutBack).SetDelay(delay);
    }

    /// <summary>Makes the sprite disappear by shrinking to nothing.</summary>
    public static Tween PopOut(this Sprite sprite, float duration = 0.25f, float delay = 0.0f) =>
        sprite.TweenSize(Vector2.Zero, duration).SetEasing(Easing.InBack).SetDelay(delay);

    /// <summary>
    /// A quick bump in size that settles back to the size the sprite has now.
    /// </summary>
    /// <param name="amount">How much bigger it gets at most, 0.1 being a tenth. Negative squeezes it instead.</param>
    public static Tween Punch(this Sprite sprite, float amount = 0.15f, float duration = 0.25f)
    {
        // Whatever was resizing it is cut short, so the size it settles back to is the one it has right now.
        Vector2 size = sprite.Transform.Size;

        Vector2 Get() => sprite.Transform.Size;
        void Set(Vector2 value) => sprite.Transform.Size = value;

        return sprite.Tweens.Play(
            Tween.Sequence()
                .Append(Tween.To(Get, Set, size * (1.0f + amount), duration * 0.3f).SetEasing(Easing.OutQuad))
                .Append(Tween.To(Get, Set, size, duration * 0.7f).SetEasing(Easing.OutBack))
                .Build(),
            Channel.Size);
    }

    /// <summary>Rattles the sprite about where it is now, dying down until it is back there.</summary>
    /// <param name="strength">How far it gets thrown at most, in the units of the world.</param>
    public static Tween Shake(this Sprite sprite, float strength = 10.0f, float duration = 0.35f)
    {
        Vector2 home = sprite.Transform.Position;
        float left = 1.0f;

        return sprite.Tweens.Play(
            Tween.To(
                () => left,
                value =>
                {
                    left = value;
                    sprite.Transform.Position = home + new Vector2(
                        Random.Shared.NextSingle() * 2.0f - 1.0f,
                        Random.Shared.NextSingle() * 2.0f - 1.0f) * strength * left;
                },
                0.0f,
                duration),
            Channel.Position);
    }
}
