using System.Numerics;
using Horizon.Engine;
using Horizon.UI.Drawing;
using Silk.NET.Input;

namespace Horizon.UI.Components;

public partial class CodeEditor
{
    private readonly MiddleScrollGesture middleScroll = new();
    /// <summary>Whether middle-mouse autoscroll is active.</summary>
    public bool AutoScrolling => middleScroll.Active;

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
        float movement = middleScroll.Step(module.ToLocal(pointer.Position), pointer.MiddleDown,
            IsHovered && !AllInSight, cancel, dt);
        if (!wasActive && middleScroll.Active) Focus();
        if (!middleScroll.Active) return;
        // This is displacement-driven motion, independent of the wheel's lines-per-notch setting.
        if (paintedSkin is { } skin && movement != 0)
        {
            scrollGoal = null;
            scrollY += movement;
            ClampScroll(skin, paintedScale);
            // Autoscroll stops at the document edge; it must not keep adding motion beyond an easing target.
            if (scrollGoal is { } limit) scrollY = MathF.Min(scrollY, limit);
            scrollGoal = null;
        }
        GameEngine.Instance?.WindowManager.RequestFrames(0.1);
    }

    private void PaintMiddleScroll(UIDrawList list)
    {
        if (!middleScroll.Active) return;
        list.BeginPart(-20);
        var box = UIRect.FromCenter(middleScroll.Origin, new Vector2(26));
        list.RoundRect(box, 13, list.Skin.Palette.Raised);
        list.RoundOutline(box, 13, 1, list.Skin.AccentColor);
        list.Symbol("arrow-up", UIRect.FromCenter(middleScroll.Origin + new Vector2(0, 6), new Vector2(10)), list.Skin.TextColor);
        list.Symbol("arrow-down", UIRect.FromCenter(middleScroll.Origin - new Vector2(0, 6), new Vector2(10)), list.Skin.TextColor);
    }

    internal sealed class MiddleScrollGesture
    {
        internal bool Active { get; private set; }
        internal Vector2 Origin { get; private set; }
        private bool wasDown, latched, moved;
        private float held;
        internal void Cancel() => Active = latched = false;

        internal float Step(Vector2 point, bool down, bool mayStart, bool cancel, float dt)
        {
            bool pressed = down && !wasDown, released = !down && wasDown;
            wasDown = down;
            if (cancel) { Cancel(); return 0; }
            if (pressed)
            {
                if (Active) { Cancel(); return 0; }
                if (!mayStart) return 0;
                Active = true;
                Origin = point;
                held = 0;
                latched = moved = false;
            }
            if (!Active) return 0;
            if (down) held += dt;
            float distance = Origin.Y - point.Y;
            moved |= MathF.Abs(distance) > 8;
            if (released)
            {
                latched = held < 0.25f && !moved;
                if (!latched) { Cancel(); return 0; }
            }
            if (!down && !latched) return 0;
            float outside = MathF.Max(0, MathF.Abs(distance) - 8);
            float speed = MathF.Min(2400, outside * 5 + outside * outside * 0.045f);
            return MathF.Sign(distance) * speed * MathF.Min(dt, 0.05f);
        }
    }
}
