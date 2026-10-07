using System.Numerics;

using Horizon.Content;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

using Shader = Horizon.OpenGL.Assets.Shader;
using Texture = Horizon.OpenGL.Assets.Texture;

namespace Horizon.OpenGL;

/// <summary>
/// A shader and the uniforms that go with it. Bind it, set what it needs by name and draw.
/// <code>
/// var technique = Technique.Load("shaders/water", "ripple");
/// ...
/// technique.Bind();
/// technique.SetUniform("uTime", time);
/// </code>
/// Where a uniform lives in the shader is looked up the first time it is set and remembered after.
/// A class of its own that descends from this can set what it always needs in <see cref="SetUniforms"/>, which every bind calls.
/// </summary>
public class Technique
{
    private Shader shader;
    private TechniqueUniformManager uniformManager;
    private TechniqueResourceIndexManager resourceManager;
    private TechniqueUniformBlockManager blockManager;

    public Technique()
    { }

    public Technique(Shader shader)
    {
        SetShader(shader);
    }

    public Technique(AssetCreationResult<Shader> asset)
        : this(asset.Asset) { }

    /// <summary>
    /// A technique out of every shader file of a name in a folder, see <see cref="Shader.Load(string, string)"/>.
    /// </summary>
    public static Technique Load(string directory, string name) => new(Shader.Load(directory, name));

    /// <summary>
    /// Whether there is a shader behind this that compiled. Drawing with one that didn't shows nothing.
    /// </summary>
    public bool IsValid => shader is { IsValid: true };

    /// <summary>
    /// Sets the internal IGLObject Shader (if null), useful for derived classes.
    /// </summary>
    protected void SetShader(Shader inShader)
    {
        shader ??= inShader;
        uniformManager ??= new(shader);
        resourceManager ??= new(shader);
        blockManager ??= new(shader);
    }

    /// <summary>
    /// Helper method for derived classes to take their shader out of every file of a name in a folder, see <see cref="Shader.Load(string, string)"/>.
    /// </summary>
    protected void LoadShader(string directory, string name) => SetShader(Shader.Load(directory, name));

    public void UniformBlockBinding(string name, uint index) => blockManager.UniformBlockBinding(name, index);

    public void BindBuffer(string name, BufferObject bufferObject, BufferTargetARB target = BufferTargetARB.ShaderStorageBuffer)
    {
        if (target == BufferTargetARB.ShaderStorageBuffer)
            ObjectManager.GL.BindBufferBase(target, resourceManager.GetLocation(name), bufferObject.Handle);
        else if (target == BufferTargetARB.UniformBuffer)
            ObjectManager.GL.BindBufferBase(target, uniformManager.GetLocation(name), bufferObject.Handle);
    }

    public void BindBuffer(uint index, BufferObject bufferObject, BufferTargetARB target = BufferTargetARB.ShaderStorageBuffer) =>
        ObjectManager.GL.BindBufferBase(target, index, bufferObject.Handle);

    /// <summary>
    /// Binds a texture to a slot and tells the sampler of that name to read from it.
    /// </summary>
    public void SetTexture(string name, Texture texture, uint slot)
    {
        texture.Bind(slot);
        ObjectManager.GL.Uniform1(Location(name), (int)slot);
    }

    public void SetUniform(string name, int value) => ObjectManager.GL.Uniform1(Location(name), value);

    public void SetUniform(string name, float value) => ObjectManager.GL.Uniform1(Location(name), value);

    public void SetUniform(string name, uint value) => ObjectManager.GL.Uniform1(Location(name), value);

    public void SetUniform(string name, bool value) => ObjectManager.GL.Uniform1(Location(name), value ? 1 : 0);

    public void SetUniform(string name, ulong value) => ObjectManager.GL.Uniform1(Location(name), value);

    public void SetUniform(string name, in Vector2 value) => ObjectManager.GL.Uniform2(Location(name), value.X, value.Y);

    public void SetUniform(string name, in Vector3 value) => ObjectManager.GL.Uniform3(Location(name), value.X, value.Y, value.Z);

    public void SetUniform(string name, in Vector4 value) => ObjectManager.GL.Uniform4(Location(name), value.X, value.Y, value.Z, value.W);

    public void SetUniform(string name, in Matrix4x4 value) => ObjectManager.GL.UniformMatrix4(Location(name), 1, false, in value.M11);

    private int Location(string name) => (int)uniformManager.GetLocation(name);

    /// <summary>
    /// Called after the shader is bound.
    /// </summary>
    protected virtual void SetUniforms()
    { }

    public void Bind()
    {
        ObjectManager.GL.UseProgram(shader.Handle);
        SetUniforms();
    }

    public void Unbind() => ObjectManager.GL.UseProgram(Shader.Invalid.Handle);
}
