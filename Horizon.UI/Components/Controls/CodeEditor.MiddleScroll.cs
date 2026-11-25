using System.Numerics;
using Horizon.Engine;
using Horizon.UI.Drawing;
using Horizon.Input;

namespace Horizon.UI.Components;

public partial class CodeEditor
{
    private readonly MiddleScrollGesture middleScroll = new();
    /// <summary>Whether middle-mouse autoscroll is active.</summary>
    public bool AutoScrolling => middleScroll.Active;
    internal bool HasInternalAutoScroll => !AllInSight;

    private void UpdateMiddleScroll(float dt)
    {
        if (Module is not { } module) return;
        var pointer = module.Compositor.Pointer;
        bool cancel = AllInSight || !EnabledInHierarchy || !IsShownInHierarchy
            || (module.Compositor.PointerSource is null && GameEngine.Instance?.WindowManager is { IsFocused: false })
            || (middleScroll.Active && !IsFocused)
            || pointer.Down || pointer.SecondaryDown
            || GameEngine.Instance?.Input.Keyboard.WasPressed(Key.Escape) == true;
        bool wasActive = middleScroll.Active;
        Vector2 movement = middleScroll.Step(module.ToLocal(pointer.Position), pointer.MiddleDown,
            IsHovered && !AllInSight, cancel, dt, module.Compositor.MiddleScrollSpeed);
        if (!wasActive && middleScroll.Active) Focus();
        if (!middleScroll.Active) return;
        // This is displacement-driven motion, independent of the wheel's lines-per-notch setting.
        if (paintedSkin is { } skin)
        {
            if (movement.Y != 0.0f)
            {
                scrollGoal = null;
                scrollY += movement.Y;
                ClampScroll(skin, paintedScale);
                // Autoscroll stops at the document edge; it must not keep adding motion beyond an easing target.
                if (scrollGoal is { } limit) scrollY = MathF.Min(scrollY, limit);
                scrollGoal = null;
            }
            if (movement.X != 0.0f)
            {
                scrollX = wordWrap ? 0.0f : Math.Clamp(scrollX + movement.X, 0.0f, MaxScrollX(skin, paintedScale));
            }
        }
        // Keep full tick and frame rate while middle-mouse autoscroll is active (including inside the deadzone
        // or during purely horizontal motion) so stationary-mouse scrolling never drops into calm pacing or sleep.
        if (GameEngine.Instance?.WindowManager is { } window)
        {
            window.RequestSmooth();
            window.NoteChange();
        }
    }

    private void PaintMiddleScroll(UIDrawList list)
    {
        if (!middleScroll.Active) return;
        list.BeginPart(-20);
        Vector2 origin = middleScroll.Origin;
        var box = UIRect.FromCenter(origin, new Vector2(28));
        list.RoundRect(box, 14, list.Skin.Palette.Raised);
        list.RoundOutline(box, 14, 1, list.Skin.AccentColor);
        list.Symbol("arrow-small-up", UIRect.FromCenter(origin + new Vector2(0, 7), new Vector2(10)), list.Skin.TextColor);
        list.Symbol("arrow-small-down", UIRect.FromCenter(origin - new Vector2(0, 7), new Vector2(10)), list.Skin.TextColor);
        list.Symbol("arrow-small-left", UIRect.FromCenter(origin - new Vector2(7, 0), new Vector2(10)), list.Skin.TextColor);
        list.Symbol("arrow-small-right", UIRect.FromCenter(origin + new Vector2(7, 0), new Vector2(10)), list.Skin.TextColor);
    }

    internal sealed class MiddleScrollGesture
    {
        internal bool Active { get; private set; }
        internal Vector2 Origin { get; private set; }
        private bool wasDown, latched, moved;
        private float held;
        internal void Cancel() => Active = latched = false;

        internal Vector2 Step(Vector2 point, bool down, bool mayStart, bool cancel, float dt, float multiplier = 1)
        {
            bool pressed = down && !wasDown, released = !down && wasDown;
            wasDown = down;
            if (cancel) { Cancel(); return Vector2.Zero; }
            if (pressed)
            {
                if (Active) { Cancel(); return Vector2.Zero; }
                if (!mayStart) return Vector2.Zero;
                Active = true;
                Origin = point;
                held = 0;
                latched = moved = false;
            }
            if (!Active) return Vector2.Zero;
            if (down) held += dt;
            float dx = point.X - Origin.X;
            float dy = Origin.Y - point.Y;
            moved |= MathF.Abs(dx) > 8.0f || MathF.Abs(dy) > 8.0f;
            if (released)
            {
                latched = held < 0.25f && !moved;
                if (!latched) { Cancel(); return Vector2.Zero; }
            }
            if (!down && !latched) return Vector2.Zero;
            multiplier = float.IsFinite(multiplier) ? Math.Clamp(multiplier, 0.25f, 5.0f) : 1.0f;
            float step = multiplier * MathF.Min(dt, 0.05f);
            return new Vector2(AxisSpeed(dx) * step, AxisSpeed(dy) * step);
        }

        private static float AxisSpeed(float distance)
        {
            float outside = MathF.Max(0.0f, MathF.Abs(distance) - 8.0f);
            if (outside <= 0.0f) return 0.0f;
            float speed = MathF.Min(2400.0f, outside * 5.0f + outside * outside * 0.045f);
            return MathF.Sign(distance) * speed;
        }
    }
}
