using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Horizon.Rendering.UIX.Drawing;

/// <summary>
/// What a quad takes its colour from. Must match the constants in shaders/ui/ui.frag.
/// </summary>
internal enum UISource : uint
{
    /// <summary>The vertex colour alone.</summary>
    Solid = 0,

    /// <summary>The skin texture, tinted.</summary>
    Skin = 1,

    /// <summary>The vertex colour, with the font atlas as coverage.</summary>
    Font = 2,

    /// <summary>The batch's own texture, tinted.</summary>
    Image = 3
}

// 24 bytes. Must match the attributes UIRenderer sets up and the inputs of shaders/ui/ui.vert.
[StructLayout(LayoutKind.Sequential)]
internal struct UIVertex
{
    public Vector2 Position;
    public Vector2 TexCoords;

    /// <summary>RGBA, 8 bits each with red in the low byte, which is what unpackUnorm4x8 expects.</summary>
    public uint Color;
    public UISource Source;

    public static readonly uint SizeInBytes = (uint)Unsafe.SizeOf<UIVertex>();
}
