using System.Numerics;

using Horizon.Core.Tweening;

namespace Horizon.UI.Components;

// A window pops in when it's put up and sinks away when it's closed, a little bigger out of a touch smaller and faded
// in, then the other way round and quicker. Small enough that it reads as the window landing rather than as an
// animation somebody has to sit through, and as quick as the compositor's AnimationSpeed says, none at all at 0.
// Closing keeps it painted (and taking nothing) until it has gone, then hides it and calls OnClosed the way it always
// did, so whoever takes it out of the UI on OnClosed doesn't cut it off halfway.
public partial class WindowPanel
{
    /// <summary>How long a window takes to come in, and to go, in seconds before the compositor's speed.</summary>
    public const float ENTER_TIME = 0.2f, LEAVE_TIME = 0.12f;

    // How much smaller it starts or ends, and how far below where it belongs (Y points up)
    private const float ENTER_SCALE = 0.965f, LEAVE_SCALE = 0.975f, ENTER_DROP = -10.0f, LEAVE_DROP = -6.0f;

    // Whether it's on its way out, painted still but gone as far as anything else is concerned
    private bool leaving;

    // Whether it was up the last update, to come in again when it's shown after being hidden
    private bool wasUp;

    /// <summary>Whether it's on its way out after <see cref="Close"/>, still painted but taking nothing.</summary>
    public bool Leaving => leaving;

    /// <summary>Whether it comes and goes with a little motion, true as it comes.</summary>
    public bool Animated { get; set; } = true;

    protected override void OnParentChanged()
    {
        base.OnParentChanged();
        if (Parent is not null && Visible)
            Enter();
        else if (Parent is null)
            StopLeaving();
        wasUp = Parent is not null && Visible;
    }

    // Shown again after being hidden, it comes in again too
    private void KeepMotion()
    {
        bool up = Parent is not null && Visible;
        if (up && !wasUp && !leaving)
            Enter();
        wasUp = up;
    }

    /// <summary>Helper method to start its entrance, from a touch small, low and see-through.</summary>
    private void Enter()
    {
        bool wasLeaving = leaving;
        leaving = false;
        float time = Animated ? MotionTime(ENTER_TIME) : 0.0f;
        if (time <= 0.0f)
        {
            if (wasLeaving)
                Settle();
            return;
        }

        VisualScale = new Vector2(ENTER_SCALE);
        VisualOffset = new Vector2(0.0f, ENTER_DROP);
        Opacity = 0.0f;
        this.TweenOpacity(1.0f, time * 0.7f).SetEasing(Easing.OutCubic);
        this.TweenOffset(Vector2.Zero, time).SetEasing(Easing.OutCubic);
        this.TweenScale(1.0f, time).SetEasing(Easing.OutBack);
    }

    /// <summary>
    /// Helper method to start it on its way out, true when it's going and <see cref="Close"/> is finished by
    /// <see cref="Gone"/> later, false when it's to go at once.
    /// </summary>
    private bool Leave()
    {
        float time = Animated ? MotionTime(LEAVE_TIME) : 0.0f;
        if (time <= 0.0f || Parent is null || !Visible)
            return false;

        leaving = true;
        this.TweenScale(LEAVE_SCALE, time).SetEasing(Easing.InCubic);
        this.TweenOffset(new Vector2(0.0f, LEAVE_DROP), time).SetEasing(Easing.InCubic);
        this.TweenOpacity(0.0f, time).SetEasing(Easing.InQuad).OnComplete(Gone);
        return true;
    }

    // All the way out, hidden for real and said so
    private void Gone()
    {
        if (!leaving)
            return;

        leaving = false;
        Visible = false;
        wasUp = false;
        Settle();
        OnClosed?.Invoke();
    }

    /// <summary>Helper method to stop it going, back as it was. Brought to the front while going, it comes back.</summary>
    private void StopLeaving()
    {
        if (!leaving)
            return;

        if (Parent is not null)
        {
            Enter();
            return;
        }

        leaving = false;
        Settle();
    }

    // Drawn the way the layout has it, no tween left on it
    private void Settle()
    {
        Tweens.KillAll();
        VisualScale = Vector2.One;
        VisualOffset = Vector2.Zero;
        Opacity = 1.0f;
    }
}
