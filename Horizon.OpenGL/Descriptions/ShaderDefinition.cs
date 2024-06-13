using Silk.NET.OpenGL;

namespace Horizon.OpenGL.Descriptions;

public readonly record struct ShaderDefinition(ShaderType Type, string File, string Source);
