using System.Numerics;
using System.Runtime.CompilerServices;

using Horizon.Content;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

using Shader = Horizon.OpenGL.Assets.Shader;

namespace Horizon.OpenGL;

public class Technique
{
    private Shader shader;
    private TechniqueUniformManager uniformManager;
    private TechniqueResourceIndexManager resourceManager;
    private TechniqueUniformBlockManager blockManager;

    public Technique()
    { }

    public Technique(in Shader shader)
    {
        this.shader = shader;
        uniformManager = new(shader);
        resourceManager = new(shader);
        blockManager = new(shader);
    }

    /// <summary>
    /// Sets the internal IGLObject Shader (if null), useful for derived classes.
    /// </summary>
    protected void SetShader(in Shader inShader)
    {
        shader ??= inShader;
        uniformManager ??= new(shader);
        resourceManager ??= new(shader);
        blockManager ??= new(shader);
    }

    public Technique(AssetCreationResult<Shader> asset)
        : this(asset.Asset) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UniformBlockBinding(in string name, in uint index) => blockManager.UniformBlockBinding(name, index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BindBuffer(in string name, in BufferObject bufferObject, BufferTargetARB target = BufferTargetARB.ShaderStorageBuffer)
    {
        if (target == BufferTargetARB.ShaderStorageBuffer)
        {
            ObjectManager
               .GL
               .BindBufferBase(
                   target,
                   resourceManager.GetLocation(name),
                   bufferObject.Handle
               );
        }
        else if (target == BufferTargetARB.UniformBuffer)
        {
            ObjectManager
               .GL
               .BindBufferBase(
                   target,
                   uniformManager.GetLocation(name),
                   bufferObject.Handle
               );
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BindBuffer(in uint index, in BufferObject bufferObject, BufferTargetARB target = BufferTargetARB.ShaderStorageBuffer)
    {
        ObjectManager
               .GL
               .BindBufferBase(
                   target,
                   index,
                   bufferObject.Handle
               );
    }

    /// <summary>
    /// Sets the specified uniform to a specified value, the uniform index is guaranteed to be cached.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetUniform(in string name, in int value)
    {
        int location = (int)uniformManager.GetLocation(name);
        ObjectManager.GL.Uniform1(location, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetUniform(in string name, in float value)
    {
        int location = (int)uniformManager.GetLocation(name);
        ObjectManager.GL.Uniform1(location, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetUniform(in string name, in uint value)
    {
        int location = (int)uniformManager.GetLocation(name);
        ObjectManager.GL.Uniform1(location, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetUniform(in string name, ref readonly Vector2 value)
    {
        int location = (int)uniformManager.GetLocation(name);
        ObjectManager.GL.Uniform2(location, value.X, value.Y);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetUniform(in string name, ref readonly Vector3 value)
    {
        int location = (int)uniformManager.GetLocation(name);
        ObjectManager.GL.Uniform3(location, value.X, value.Y, value.Z);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetUniform(in string name, ref readonly Vector4 value)
    {
        int location = (int)uniformManager.GetLocation(name);
        ObjectManager.GL.Uniform4(location, value.X, value.Y, value.Z, value.W);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetUniform(in string name, ref readonly Matrix4x4 value)
    {
        int location = (int)uniformManager.GetLocation(name);
        ObjectManager.GL.UniformMatrix4(location, 1, false, in value.M11);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetUniform(in string name, in bool value)
    {
        int location = (int)uniformManager.GetLocation(name);
        ObjectManager.GL.Uniform1(location, value ? 1 : 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetUniform(in string name, in ulong value)
    {
        int location = (int)uniformManager.GetLocation(name);
        ObjectManager.GL.Uniform1(location, value);
    }


    /// <summary>
    /// Called after the shader is bound.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected virtual void SetUniforms()
    { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Bind()
    {
        ObjectManager.GL.UseProgram(shader.Handle);
        SetUniforms();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Unbind() => ObjectManager.GL.UseProgram(Shader.Invalid.Handle);
}