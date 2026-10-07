using System.Numerics;

namespace Horizon.Rendering.UIX;

/// <summary>
/// The state of whatever points at the UI: the mouse by default, but a compositor can be given any
/// <see cref="UICompositor.PointerSource"/>.
/// </summary>
/// <param name="Position">Where it points, in the world space of the compositor's camera.</param>
/// <param name="Down">Whether its primary button is held.</param>
public readonly record struct UIPointer(Vector2 Position, bool Down);
