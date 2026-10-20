using System.Numerics;

using Horizon.Graphics;

namespace Horizon.UI;

/// <summary>
/// A pointer a UI draws itself, where the mouse is at the very moment a frame is drawn.
/// <code>
/// compositor.Cursor = new UICursor { Texture = Texture.Load("Assets/ui/cursor.png"), Hotspot = new Vector2(2, 2) };
/// </code>
/// It is not a component and is not laid out or painted with the rest. Everything else in a UI is painted once a
/// tick and drawn a moment in the past, between the last two ticks, which is what makes things that move go
/// evenly and is a tick or two of delay nobody notices on a menu sliding in. On a cursor it is all anybody
/// notices, the thing trails the hand. So this one is placed on the render thread, every frame, from the mouse
/// as the window last heard of it, and is as far behind the hand as a frame takes to get to the screen and no
/// further. It is drawn over the rest of its UI.
/// </summary>
public sealed class UICursor
{
    /// <summary>What it looks like. Without one it is a small square in <see cref="Tint"/>, which is enough to find it by.</summary>
    public Texture? Texture { get; set; }

    /// <summary>How big it is drawn, in the units of the layout. The size of the texture if this is left at zero.</summary>
    public Vector2 Size { get; set; }

    /// <summary>Which point of it is the one that points, from its top left corner in the same units. The tip of an arrow.</summary>
    public Vector2 Hotspot { get; set; }

    public Vector4 Tint { get; set; } = Vector4.One;

    public bool Visible { get; set; } = true;

    /// <summary>
    /// Where it is, in the window from its top left corner the way <see cref="Input.Mouse.Position"/> has it, asked
    /// on the render thread for every frame. Left alone it is the mouse. For a cursor a gamepad moves, hand out
    /// where that has got to from here, something that can be read from another thread.
    /// </summary>
    public Func<Vector2>? Position { get; set; }
}
