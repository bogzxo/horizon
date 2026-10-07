using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering.UIX.Components;

using Silk.NET.Input;

namespace Horizon.Rendering.UIX;

/// <summary>
/// Drives a module without a pointer: a gamepad or the keyboard walking from one component to the next, with one
/// of them selected at a time (lit the way the pointer lights it, see <see cref="UIComponent.IsSelected"/>) and the
/// confirm button doing what a click would. Every module has one, see <see cref="UIModule.Navigation"/>:
/// <code>
/// var nav = layout.Module.Navigation;
/// nav.SelectFirst();
/// ...
/// if (pad.WasPressed(GamepadInput.DPadDown)) nav.Move(0, 1);
/// if (pad.WasPressed(GamepadInput.A)) nav.Activate();
/// </code>
/// Moving goes by where things are on screen, so a grid of buttons works as well as a list does. Left and right ask
/// the selected component first (a selector steps, a slider slides) and only move on if it has no use for them, and
/// so does <see cref="Move(int, int)"/> for a component that walks its own rows (a list box).
/// The arrow keys, enter and space do the same by themselves once something is selected (see <see cref="Keys"/>),
/// unless a text box has the keyboard.
/// </summary>
public sealed class UINavigator
{
    // How much worse a candidate off to the side is than one straight ahead at the same distance. Enough that the cell
    // under this one beats the one over from it, not so much that the selector of the next row (whose label pushes it
    // off to the side) loses to a button three rows down
    private const float ASIDE_PENALTY = 1.0f;

    // Coming round past the edge is about the row furthest back, so there being off to the side counts for more
    private const float WRAP_ASIDE_PENALTY = 2.5f;

    private readonly UIModule module;
    private readonly List<UIComponent> candidates = [];

    /// <summary>The component that is selected, null for none.</summary>
    public UIComponent? Current { get; private set; }

    /// <summary>
    /// The part of the module the navigator keeps to, null for the whole of it. A dialog sets this to itself while
    /// it is up, so nothing behind it can be walked to.
    /// </summary>
    public UIComponent? Scope { get; set; }

    /// <summary>Whether moving past the last component comes round to the first, and the other way. On unless switched off.</summary>
    public bool Wrap { get; set; } = true;

    /// <summary>
    /// Whether the arrow keys move the selection and enter or space activate it, once something is selected. On
    /// unless switched off. A game that reads the arrows itself and has nothing selected loses nothing to this.
    /// </summary>
    public bool Keys { get; set; } = true;

    /// <summary>Called with the newly selected component (or null) whenever the selection changes.</summary>
    public Action<UIComponent?>? Changed { get; set; }

    internal UINavigator(UIModule module)
    {
        this.module = module;
    }

    /// <summary>
    /// Selects a component, lighting it up and scrolling it into view if it is inside something that scrolls. Null
    /// selects nothing.
    /// </summary>
    /// <returns>Whether that changed anything.</returns>
    public bool Select(UIComponent? component)
    {
        if (component == Current)
            return false;

        if (Current is { } before)
            before.IsSelected = false;

        Current = component;

        if (component is not null)
        {
            component.IsSelected = true;

            // Everything it is inside of that scrolls brings it into sight
            for (UIComponent? at = component.Parent; at is not null; at = at.Parent)
            {
                if (at is ScrollPanel scroll)
                    scroll.ScrollIntoView(component);
            }
        }

        Changed?.Invoke(component);
        return true;
    }

    /// <summary>Selects the first component there is to select, in the order of the layout. False if there is none.</summary>
    public bool SelectFirst()
    {
        Collect();
        return candidates.Count > 0 && (Select(candidates[0]) || Current == candidates[0]);
    }

    /// <summary>Selects the next component in the order of the layout, the first after the last (if the navigator wraps).</summary>
    public bool Next() => Step(1);

    /// <summary>Selects the component before this one in the order of the layout, the last before the first.</summary>
    public bool Previous() => Step(-1);

    private bool Step(int by)
    {
        Collect();
        if (candidates.Count == 0)
            return false;

        int index = Current is null ? -1 : candidates.IndexOf(Current);
        if (index < 0)
            return Select(by > 0 ? candidates[0] : candidates[^1]);

        int next = index + by;
        if (next < 0 || next >= candidates.Count)
        {
            if (!Wrap)
                return false;

            next = (next + candidates.Count) % candidates.Count;
        }

        return Select(candidates[next]);
    }

    /// <summary>
    /// Moves the selection to the nearest component in a direction, by where things are on screen. Positive
    /// <paramref name="right"/> goes right and positive <paramref name="down"/> goes down, the way lists count.
    /// Nothing selected yet, it selects the first thing there is.
    /// </summary>
    /// <returns>Whether the selection moved.</returns>
    public bool Move(int right, int down)
    {
        // Something that has a use for the direction itself (a list box going down its rows) gets it first
        if (Current is { } current && IsSelectable(current) && current.OnNavigate(Math.Sign(right), Math.Sign(down)))
            return true;

        return Move(new Vector2(right, -down));
    }

    /// <summary>
    /// Moves the selection to the nearest component in a direction of the layout (Y up), see <see cref="Move(int, int)"/>.
    /// </summary>
    public bool Move(Vector2 direction)
    {
        if (direction == Vector2.Zero)
            return false;

        if (Current is null || !IsSelectable(Current))
            return SelectFirst();

        Collect();
        direction = Vector2.Normalize(direction);

        UIRect from = Current.Bounds;
        UIComponent? best = null, around = null;
        float bestScore = float.MaxValue, aroundScore = float.MaxValue;

        foreach (UIComponent candidate in candidates)
        {
            if (candidate == Current)
                continue;

            // How far along the way and how far off to the side the candidate is, measured from edge to edge along the way
            Vector2 offset = candidate.Bounds.Center - from.Center;
            float forward = Vector2.Dot(offset, direction);
            float along = forward - Extent(from, direction) - Extent(candidate.Bounds, direction);
            float aside = (offset - forward * direction).Length();

            // Ahead is in front and within a cone of the way, widened by how far the two reach across it: a grid cell
            // under this one is, the next column over at the same height isn't
            Vector2 across = new(-direction.Y, direction.X);
            float reach = Extent(from, across) + Extent(candidate.Bounds, across);
            bool ahead = forward > 0.0f && aside - reach <= forward;
            float score = MathF.Abs(along) + aside * ASIDE_PENALTY;

            if (ahead && score < bestScore)
            {
                best = candidate;
                bestScore = score;
            }
            else if (!ahead)
            {
                // The one furthest back (and least off to the side) is where going past the edge comes round to
                float back = forward + aside * WRAP_ASIDE_PENALTY;
                if (back < aroundScore)
                {
                    around = candidate;
                    aroundScore = back;
                }
            }
        }

        if (best is null && Wrap)
            best = around;

        return best is not null && Select(best);
    }

    /// <summary>How far a rectangle reaches from its middle along a direction.</summary>
    private static float Extent(UIRect rect, Vector2 direction) =>
        MathF.Abs(direction.X) * rect.Width * 0.5f + MathF.Abs(direction.Y) * rect.Height * 0.5f;

    /// <summary>Does to the selected component what a click would: presses a button, flips a toggle, opens a dropdown.</summary>
    public void Activate()
    {
        if (Current is { } current && IsSelectable(current))
            current.OnActivate();
    }

    /// <summary>
    /// Hands left or right to the selected component (a selector steps, a slider slides). False if it had no use for
    /// it, in which case whoever called this usually moves instead.
    /// </summary>
    /// <param name="step">-1 for left, 1 for right.</param>
    public bool Adjust(int step) => step != 0 && Current is { } current && IsSelectable(current) && current.OnAdjust(step);

    /// <summary>
    /// Keeps the selection honest and reads the keys. Once an update, by the module.
    /// </summary>
    internal void Update()
    {
        // Whatever was selected has gone, been hidden or switched off
        if (Current is { } current && !IsSelectable(current))
        {
            current.IsSelected = false;
            Current = null;
            Changed?.Invoke(null);
        }

        if (!Keys || Current is null || !module.Interactive || module.Compositor.Focus is not null)
            return;

        var keyboard = GameEngine.Instance.Input.Keyboard;

        if (keyboard.WasPressed(Key.Enter) || keyboard.WasPressed(Key.KeypadEnter) || keyboard.WasPressed(Key.Space))
            Activate();

        if (keyboard.WasPressed(Key.Left) && !Adjust(-1))
            Move(-1, 0);
        if (keyboard.WasPressed(Key.Right) && !Adjust(1))
            Move(1, 0);
        if (keyboard.WasPressed(Key.Up))
            Move(0, -1);
        if (keyboard.WasPressed(Key.Down))
            Move(0, 1);
    }

    /// <summary>Whether a component is there to be selected right now: in the scope, shown, and switched on.</summary>
    private bool IsSelectable(UIComponent component)
    {
        if (!component.Navigable || !component.EnabledInHierarchy || component.Module != module)
            return false;

        UIComponent top = Scope ?? module.Root;
        bool inside = false;

        for (UIComponent? at = component; at is not null; at = at.Parent)
        {
            if (!at.Visible || at.IsOnHiddenLayer || (at.Parent is { } parent && !parent.ShowsChild(at)))
                return false;

            if (at == top)
            {
                inside = true;
                break;
            }
        }

        return inside || top == module.Root;
    }

    /// <summary>Helper method to list everything there is to select, in the order of the layout.</summary>
    private void Collect()
    {
        candidates.Clear();
        Collect(Scope ?? module.Root);
    }

    private void Collect(UIComponent component)
    {
        if (!component.Visible || component.IsOnHiddenLayer || !component.Enabled)
            return;

        if (component.Navigable)
            candidates.Add(component);

        foreach (UIComponent child in component.Children)
        {
            if (component.ShowsChild(child))
                Collect(child);
        }
    }
}
