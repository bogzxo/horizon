using System;
using System.Collections.Generic;
using System.Text;

namespace Horizon.Rendering;

/// <summary>
/// Does this even need docs? its a byte ig...
/// </summary>
public enum Origin : byte
{
    Center,

    TopLeft, TopRight,
    BottomLeft, BottomRight,
    
    Top, Bottom,
    Left, Right,
}
