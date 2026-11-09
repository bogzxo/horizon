using System.Numerics;

using Horizon.Input;

namespace Horizon.UI;

/// <summary>
/// The state of whatever points at the UI, the mouse by default, but a compositor can be given any
/// <see cref="UICompositor.PointerSource"/>.
/// </summary>
/// <param name="Position">Where it points, in the world space of the compositor's camera.</param>
/// <param name="Down">Whether its primary button is held, or the finger is on the screen.</param>
/// <param name="SecondaryDown">Whether the other button (the right one of a mouse) is held, which is what opens context menus.</param>
/// <param name="MiddleDown">Whether the middle mouse button is held.</param>
/// <param name="Kind">
/// A mouse, a finger or a pen. A finger only hovers while it's down, waits a moment before a drag turns into a scroll
/// of whatever it's in, and holds still for a context menu instead of a right click, see <see cref="UICompositor.TouchSlop"/>.
/// </param>
/// <param name="Id">Which contact it is, for a finger. The compositor follows one at a time, the others are a game's.</param>
/// <param name="Cancelled">The system took the contact away. Whatever it was pressing is let go of without a click.</param>
public readonly record struct UIPointer(Vector2 Position, bool Down, bool SecondaryDown = false, bool MiddleDown = false,
    PointerKind Kind = PointerKind.Mouse, int Id = 0, bool Cancelled = false)
{
    /// <summary>Whether it's a finger or a pen rather than a mouse.</summary>
    public bool IsTouch => Kind != PointerKind.Mouse;
}
