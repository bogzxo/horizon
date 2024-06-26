﻿using Silk.NET.OpenGL;

namespace Horizon.Core.Data;

/// <summary>
/// Attach this to properties and call Buffer.SetLayout() to automatically configure a buffer layout.
/// </summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public class VertexLayout(uint index, VertexAttribPointerType type, bool instanced = false) : Attribute
{
    public uint Index { get; } = index;
    public VertexAttribPointerType Type { get; } = type;
    public bool Instanced { get; } = instanced;
}
