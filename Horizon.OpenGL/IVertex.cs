using System;
using System.Collections.Generic;
using System.Text;

using Silk.NET.OpenGL;


namespace Horizon.OpenGL;

public readonly struct VertexLayoutDescription
{
    public uint Index { get; init; }
    public int Size { get; init; }
    public int Count { get; init; }
    public int Offset { get; init; }
    public bool Instanced { get; init; }
    public VertexAttribPointerType Type { get; init; }
}

public interface IVertex
{
    // C# 11 feature: Interfaces can require static methods!
    static abstract ReadOnlySpan<VertexLayoutDescription> GetLayout();
}