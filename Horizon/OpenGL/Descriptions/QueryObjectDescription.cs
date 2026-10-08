using Horizon.Content.Descriptions;

using Silk.NET.OpenGL;

namespace Horizon.OpenGL.Descriptions;

public readonly record struct QueryObjectDescription : IAssetDescription
{
    public readonly QueryTarget Target { get; init; }
    public static readonly QueryObjectDescription Default = new() { Target = QueryTarget.TimeElapsed };
}