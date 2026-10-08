using System.Numerics;
using System.Runtime.CompilerServices;

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
/// <para>
/// What every shader needs of the camera doesn't go through here at all: it is in the uniform block every shader
/// includes (shaders/common/camera.glsl), set once a frame for all of them, see <c>Horizon.Rendering.CameraBlock</c>.
/// Samplers and blocks say where they are bound in the shader itself (<c>layout(binding = N)</c>), so nothing
/// has to be set for those either: bind the texture to the unit and draw.
/// </para>
/// </summary>
public class Technique
{
    private Shader shader = Shader.Invalid;
    private TechniqueUniformManager uniformManager = null!;
    private TechniqueResourceIndexManager resourceManager = null!;
    private TechniqueUniformBlockManager blockManager = null!;

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

    /// <summary>The program behind the technique.</summary>
    public Shader Shader => shader;

    /// <summary>
    /// Sets the internal IGLObject Shader (if null), useful for derived classes.
    /// </summary>
    protected void SetShader(Shader inShader)
    {
        if (shader.IsValid) return;

        shader = inShader;
        uniformManager = new(shader);
        resourceManager = new(shader);
        blockManager = new(shader);
    }

    /// <summary>
    /// Helper method for derived classes to take their shader out of every file of a name in a folder, see <see cref="Shader.Load(string, string)"/>.
    /// </summary>
    protected void LoadShader(string directory, string name) => SetShader(Shader.Load(directory, name));

    public void UniformBlockBinding(string name, uint index) => blockManager.UniformBlockBinding(name, index);

    /// <summary>
    /// Binds a buffer to a block by the name the shader gives it. For a block that says its binding in the shader
    /// (<c>layout(binding = N)</c>) bind by the number instead, it saves the lookup.
    /// </summary>
    public void BindBuffer(string name, BufferObject bufferObject, BufferTargetARB target = BufferTargetARB.ShaderStorageBuffer)
    {
        if (target == BufferTargetARB.ShaderStorageBuffer)
            bufferObject.BindBase(target, resourceManager.GetLocation(name));
        else if (target == BufferTargetARB.UniformBuffer)
            bufferObject.BindBase(target, uniformManager.GetLocation(name));
    }

    public void BindBuffer(uint index, BufferObject bufferObject, BufferTargetARB target = BufferTargetARB.ShaderStorageBuffer) =>
        bufferObject.BindBase(target, index);

    /// <summary>
    /// Binds a texture to a slot and tells the sampler of that name to read from it. A sampler that says its unit in
    /// the shader (<c>layout(binding = N) uniform sampler2D</c>) only needs the texture bound, see <see cref="Texture.Bind"/>.
    /// </summary>
    public void SetTexture(string name, Texture texture, uint slot)
    {
        texture.Bind(slot);
        ObjectManager.GL.Uniform1(Location(name), (int)slot);
    }

    /// <summary>
    /// Binds textures to a run of units in one call, the first to <paramref name="first"/> and so on. A handle of 0
    /// leaves nothing bound to its unit.
    /// </summary>
    public static void BindTextures(ReadOnlySpan<uint> handles, uint first = 0) =>
        Horizon.Graphics.GraphicsDevice.Current.BindTextures(handles, first);

    /// <summary>
    /// Binds sampler objects to a run of units in one call. A handle of 0 has the unit go by the texture's own settings.
    /// </summary>
    public static void BindSamplers(ReadOnlySpan<uint> samplers, uint first = 0) =>
        Horizon.Graphics.GraphicsDevice.Current.BindSamplers(samplers, first);

    public void SetUniform(string name, int value) => ObjectManager.GL.Uniform1(Location(name), value);

    public void SetUniform(string name, float value) => ObjectManager.GL.Uniform1(Location(name), value);

    public void SetUniform(string name, uint value) => ObjectManager.GL.Uniform1(Location(name), value);

    public void SetUniform(string name, bool value) => ObjectManager.GL.Uniform1(Location(name), value ? 1 : 0);

    public void SetUniform(string name, ulong value) => ObjectManager.GL.Uniform1(Location(name), value);

    public void SetUniform(string name, in Vector2 value) => ObjectManager.GL.Uniform2(Location(name), value.X, value.Y);

    public void SetUniform(string name, in Vector3 value) => ObjectManager.GL.Uniform3(Location(name), value.X, value.Y, value.Z);

    public void SetUniform(string name, in Vector4 value) => ObjectManager.GL.Uniform4(Location(name), value.X, value.Y, value.Z, value.W);

    public void SetUniform(string name, in Matrix4x4 value) => ObjectManager.GL.UniformMatrix4(Location(name), 1, false, in value.M11);

    /// <summary>Sets a whole array of vectors in one call, <c>uniform vec2 uName[N]</c>.</summary>
    public unsafe void SetUniform(string name, ReadOnlySpan<Vector2> values)
    {
        fixed (Vector2* pointer = values)
            ObjectManager.GL.Uniform2(Location(name), (uint)values.Length, (float*)pointer);
    }

    /// <summary>Sets a whole array of vectors in one call, <c>uniform vec4 uName[N]</c>.</summary>
    public unsafe void SetUniform(string name, ReadOnlySpan<Vector4> values)
    {
        fixed (Vector4* pointer = values)
            ObjectManager.GL.Uniform4(Location(name), (uint)values.Length, (float*)pointer);
    }

    /// <summary>Sets a whole array of floats in one call, <c>uniform float uName[N]</c>.</summary>
    public unsafe void SetUniform(string name, ReadOnlySpan<float> values)
    {
        fixed (float* pointer = values)
            ObjectManager.GL.Uniform1(Location(name), (uint)values.Length, pointer);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
