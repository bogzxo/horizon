using System.Numerics;

using Horizon.Core.Tweening;

namespace Horizon.Rendering.Spriting;

/// <summary>
/// Tweens for sprites. Every one of them starts playing straight away on the sprite's own
/// <see cref="Sprite.Tweens"/> and hands the handle back for setting up further.
/// <code>
/// sprite.TweenPosition(new Vector2(200, 0), 0.5f).SetEasing(Easing.OutCubic);
/// sprite.PopIn();
/// sprite.TweenRotation(360, 2.0f).SetLoops(-1);
/// </code>
/// Starting a tween stops the one that was moving the same thing, so they can be fired off without checking.
/// </summary>
public static class SpriteTweens
{
    // Value-type channels so starting or replacing a tween never boxes an enum.
    private static readonly TweenChannel ChannelPosition = new(1);
    private static readonly TweenChannel ChannelSize = new(2);
    private static readonly TweenChannel ChannelRotation = new(3);
    private static readonly TweenChannel ChannelTint = new(4);
    private static readonly TweenChannel ChannelFlash = new(5);

    // Cached static getter/setter delegates so every call allocates 0 bytes and creates no closure.
    private static readonly Func<object, Vector2> GetPosition = static target => ((Sprite)target).Transform.Position;
    private static readonly Action<object, Vector2> SetPosition = static (target, value) => ((Sprite)target).Transform.Position = value;

    private static readonly Func<object, Vector2> GetSize = static target => ((Sprite)target).Transform.Size;
    private static readonly Action<object, Vector2> SetSize = static (target, value) => ((Sprite)target).Transform.Size = value;

    private static readonly Func<object, float> GetRotation = static target => ((Sprite)target).Transform.Rotation;
    private static readonly Action<object, float> SetRotation = static (target, value) => ((Sprite)target).Transform.Rotation = value;

    private static readonly Func<object, float> GetFlashAmount = static target => ((Sprite)target).FlashAmount;
    private static readonly Action<object, float> SetFlashAmount = static (target, value) => ((Sprite)target).FlashAmount = value;

    private static readonly Func<object, Vector4> GetTint = static target => ((Sprite)target).Tint;
    private static readonly Action<object, Vector4> SetTint = static (target, value) => ((Sprite)target).Tint = value;

    private static readonly Func<object, float> GetOpacity = static target => ((Sprite)target).Tint.W;
    private static readonly Action<object, float> SetOpacity = static (target, value) =>
    {
        var sprite = (Sprite)target;
        sprite.Tint = sprite.Tint with { W = value };
    };

    public static TweenHandle TweenPosition(this Sprite sprite, Vector2 to, float duration) =>
        sprite.Tweens.PlayVector2(sprite, GetPosition, SetPosition, to, duration, ChannelPosition);

    /// <summary>Resizes the sprite. A negative width is a flipped sprite, see <see cref="Sprite.Flipped"/>.</summary>
    public static TweenHandle TweenSize(this Sprite sprite, Vector2 to, float duration) =>
        sprite.Tweens.PlayVector2(sprite, GetSize, SetSize, to, duration, ChannelSize);

    /// <summary>Turns the sprite, in degrees.</summary>
    public static TweenHandle TweenRotation(this Sprite sprite, float to, float duration) =>
        sprite.Tweens.PlayFloat(sprite, GetRotation, SetRotation, to, duration, ChannelRotation);

    /// <summary>
    /// Lights the sprite up in a colour and lets it fade back to how it was, for something that just got hit.
    /// A tint can't do this (it only ever darkens), see <see cref="Sprite.FlashColor"/>.
    /// </summary>
    /// <param name="duration">How long the fade takes, in seconds.</param>
    /// <param name="amount">How much of the colour there is to begin with, 1 being nothing but the colour.</param>
    public static TweenHandle Flash(this Sprite sprite, Vector4 color, float duration, float amount = 1.0f)
    {
        sprite.FlashColor = color;
        sprite.FlashAmount = amount;

        return sprite.Tweens.PlayFloat(sprite, GetFlashAmount, SetFlashAmount, 0.0f, duration, ChannelFlash, Easing.OutQuad);
    }

    /// <summary>Changes the colour the sprite is multiplied with, alpha included.</summary>
    public static TweenHandle TweenTint(this Sprite sprite, Vector4 to, float duration) =>
        sprite.Tweens.PlayVector4(sprite, GetTint, SetTint, to, duration, ChannelTint);

    /// <summary>Changes how see-through the sprite is and leaves its colour alone, 0 being invisible.</summary>
    public static TweenHandle TweenOpacity(this Sprite sprite, float to, float duration) =>
        sprite.Tweens.PlayFloat(sprite, GetOpacity, SetOpacity, to, duration, ChannelTint);

    /// <summary>
    /// Makes the sprite appear by growing out of nothing to the size it has now, a little too far and back.
    /// It is hidden from the moment this is called, also while it waits out its delay.
    /// </summary>
    public static TweenHandle PopIn(this Sprite sprite, float duration = 0.35f, float delay = 0.0f)
    {
        Vector2 size = sprite.Transform.Size;
        sprite.Transform.Size = Vector2.Zero;

        return sprite.TweenSize(size, duration).SetEasing(Easing.OutBack).SetDelay(delay);
    }

    /// <summary>Makes the sprite disappear by shrinking to nothing.</summary>
    public static TweenHandle PopOut(this Sprite sprite, float duration = 0.25f, float delay = 0.0f) =>
        sprite.TweenSize(Vector2.Zero, duration).SetEasing(Easing.InBack).SetDelay(delay);

    /// <summary>
    /// A quick bump in size that settles back to the size the sprite has now.
    /// </summary>
    /// <param name="amount">How much bigger it gets at most, 0.1 being a tenth. Negative squeezes it instead.</param>
    public static TweenHandle Punch(this Sprite sprite, float amount = 0.15f, float duration = 0.25f)
    {
        // Whatever was resizing it is cut short, so the size it settles back to is the one it has right now.
        Vector2 size = sprite.Transform.Size;

        return sprite.Tweens.PlayTwoStepVector2(
            sprite,
            GetSize,
            SetSize,
            size * (1.0f + amount),
            duration * 0.3f,
            Easing.OutQuad,
            size,
            duration * 0.7f,
            Easing.OutBack,
            ChannelSize);
    }

    /// <summary>Rattles the sprite about where it is now, dying down until it is back there.</summary>
    /// <param name="strength">How far it gets thrown at most, in the units of the world.</param>
    public static TweenHandle Shake(this Sprite sprite, float strength = 10.0f, float duration = 0.35f) =>
        sprite.Tweens.PlayShakeVector2(sprite, SetPosition, sprite.Transform.Position, strength, duration, ChannelPosition);
}
