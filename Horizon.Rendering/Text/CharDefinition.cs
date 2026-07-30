using System.Numerics;

namespace Horizon.Rendering.Text;

public readonly struct CharDefinition
{
    public readonly char Id { get; init; }
    public readonly Vector2 Position { get; init; }
    public readonly Vector2 Size { get; init; }
    public readonly Vector2 Offset { get; init; }
    public readonly int XAdvance { get; init; }
}
