using System.Numerics;
using Horizon.Engine;
using Horizon.Input;
using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Horizon.UI;

public partial class UICompositor
{
    private readonly CodeEditor.MiddleScrollGesture middleScroll = new();
    private UIComponent? middleScrollTarget;
    private bool middleWasDown;
    private bool middleConsumedEscape;

    /// <summary>The component captured by middle-button autoscroll, if any.</summary>
    public UIComponent? AutoScrollTarget => middleScroll.Active ? middleScrollTarget : null;

    private void PaintMiddleScroll(UIDrawList list, UIModule module)
    {
        if (AutoScrollTarget is not { } target || target.Module != module) return;
        list.PushOwner(target);
        list.BeginPart(-7005);
        Vector2 origin = middleScroll.Origin;
        var box = UIRect.FromCenter(origin, new Vector2(28));
        list.RoundRect(box, 14, list.Skin.Palette.Raised);
        list.RoundOutline(box, 14, 1, list.Skin.AccentColor);
        list.Symbol("arrow-small-up", UIRect.FromCenter(origin + new Vector2(0, 7), new Vector2(10)), list.Skin.TextColor);
        list.Symbol("arrow-small-down", UIRect.FromCenter(origin - new Vector2(0, 7), new Vector2(10)), list.Skin.TextColor);
        list.Symbol("arrow-small-left", UIRect.FromCenter(origin - new Vector2(7, 0), new Vector2(10)), list.Skin.TextColor);
        list.Symbol("arrow-small-right", UIRect.FromCenter(origin + new Vector2(7, 0), new Vector2(10)), list.Skin.TextColor);
        list.EndParts();
        list.PopOwner();
    }

    private void RouteMiddleScroll(UIModule[] snapshot, UIPointer pointer, float dt)
    {
        bool pressed = pointer.MiddleDown && !middleWasDown;
        middleWasDown = pointer.MiddleDown;
        bool escape = InputOf?.Keyboard.WasPressed(Key.Escape) == true;
        middleConsumedEscape = middleScroll.Active && escape;
        bool cancel = pointer.Cancelled || pointer.Kind != PointerKind.Mouse || pointer.Down || pointer.SecondaryDown || escape
            || (PointerSource is null && GameEngine.Instance?.WindowManager is { IsFocused: false });
        if (pressed && !middleScroll.Active && !cancel)
        {
            middleScrollTarget = null;
            for (int i = snapshot.Length - 1; i >= 0; i--)
            {
                var module = snapshot[i];
                if (!module.Enabled || !module.Interactive || module.HitTest(pointer.Position) is not { } over) continue;
                for (UIComponent? at = over; at is not null; at = at.Parent)
                {
                    // Editors retain their existing marker, focus and document-edge handling.
                    if (at is CodeEditor editor)
                    {
                        if (!editor.HasInternalAutoScroll) continue; // Notebook cells scroll with their containing panel.
                        break;
                    }
                    if (at is TabStrip or DrawingPanel) break;
                    if (at.EnabledInHierarchy && at.UsesScrollSpeed) { middleScrollTarget = at; break; }
                    if (at == module.Popup) break;
                }
                break; // Never scroll a window behind the one that was clicked.
            }
        }
        var target = middleScrollTarget;
        cancel |= target is null || target.Module is not { Enabled: true, Interactive: true }
            || !target.EnabledInHierarchy || !target.IsShownInHierarchy;
        Vector2 point = target?.Module?.ToLocal(pointer.Position) ?? pointer.Position;
        Vector2 movement = middleScroll.Step(point, pointer.MiddleDown, target is not null, cancel, dt, MiddleScrollSpeed);
        if (!middleScroll.Active) { middleScrollTarget = null; return; }
        // OnPan uses finger-displacement signs: +Y scrolls down, -X scrolls right.
        if (movement != Vector2.Zero) target!.OnPan(new Vector2(-movement.X, movement.Y));
        // Keep full tick and frame rate while middle-mouse autoscroll is active so a stationary pointer
        // never drops into calm pacing (24/12 Hz) or WaitForEvents sleep.
        if (GameEngine.Instance?.WindowManager is { } window)
        {
            window.RequestSmooth();
            window.NoteChange();
        }
    }
}
