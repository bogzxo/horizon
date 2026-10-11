using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Horizon.Content;
using Horizon.Logging;

namespace Horizon.Graphics;

/// <summary>
/// A shader and the uniforms that go with it. Bind it, set what it needs by name and draw.
/// <code>
/// var technique = Technique.Load("shaders/water", "ripple");
/// ...
/// technique.Bind();
/// technique.SetUniform("uTime", time);
/// </code>
/// The uniforms are the members of the shader's <c>Params</c> block (a <c>ConstantBuffer&lt;Params&gt;</c> on binding 1
/// in Slang, there is no GLSL any more and nobody misses it). Setting one writes it into a copy of the block
/// here, and the block goes to the GPU the moment something is drawn with the technique. However many uniforms a draw
/// sets, it is one small write. A name the block hasn't got is said once in the log and then ignored.
/// A class of its own that descends from this can set what it always needs in <see cref="SetUniforms"/>, which every bind calls.
/// <para>
/// What every shader needs of the camera doesn't go through here at all, it is in the camera block every shader
/// includes, set once a frame for all of them, see <c>Horizon.Rendering.CameraBlock</c>. Samplers and buffers say
/// where they are bound in the shader itself (<c>BIND_TEXTURE(n)</c>, <c>BIND_BUFFER(n)</c>), so nothing has to be set
/// for those either, bind the texture to the unit and draw.
/// </para>
/// </summary>
public class Technique
{
    private static readonly HashSet<(uint, string)> warned = [];

    private Shader shader = Shader.Invalid;
    private byte[] block = [];
    private bool dirty = true;

    public Technique()
    { }

    public Technique(Shader shader)
    {
        SetShader(shader);
    }

    public Technique(AssetCreationResult<Shader> asset)
        : this(asset.Asset) { }

    /// <summary>A technique out of every shader file of a name in a folder, see <see cref="Shader.Load(string, string)"/>.</summary>
    public static Technique Load(string directory, string name) => new(Shader.Load(directory, name));

    /// <summary>Whether there is a shader behind this that compiled. Drawing with one that didn't shows nothing.</summary>
    public bool IsValid => shader is { IsValid: true };

    /// <summary>The program behind the technique.</summary>
    public Shader Shader => shader;

    /// <summary>Sets the shader, for derived classes. Only the first time, a technique keeps its shader.</summary>
    protected void SetShader(Shader inShader)
    {
        if (shader.IsValid) return;

        shader = inShader;
        block = new byte[shader.Params.Size];
        dirty = true;
    }

    /// <summary>Helper method for derived classes to take their shader out of every file of a name in a folder, see <see cref="Shader.Load(string, string)"/>.</summary>
    protected void LoadShader(string directory, string name) => SetShader(Shader.Load(directory, name));

    /// <summary>Makes a buffer what the shader reads at a storage binding (2 and up), see <see cref="GraphicsDevice.BindStorageBuffer"/>.</summary>
    public void BindBuffer(uint index, GpuBuffer buffer) => GraphicsDevice.Current.BindStorageBuffer(index, buffer);

    /// <summary>Binds a texture to a unit. The name is for the reader, the shader says where its samplers are.</summary>
    public void SetTexture(string name, Texture texture, uint slot) => texture.Bind(slot);

    /// <summary>Binds textures to a run of units in one call, the first to <paramref name="first"/> and so on.</summary>
    public static void BindTextures(ReadOnlySpan<Texture?> textures, uint first = 0) => GraphicsDevice.Current.BindTextures(textures, first);

    /// <summary>Binds sampler objects to a run of units in one call. A handle of 0 has the unit go by the texture's own settings.</summary>
    public static void BindSamplers(ReadOnlySpan<uint> samplers, uint first = 0) => GraphicsDevice.Current.BindSamplers(samplers, first);

    /* Uniforms */

    public void SetUniform(string name, int value) => Write(name, value);

    public void SetUniform(string name, float value) => Write(name, value);

    public void SetUniform(string name, uint value) => Write(name, value);

    public void SetUniform(string name, bool value) => Write(name, value ? 1 : 0);

    public void SetUniform(string name, in Vector2 value) => Write(name, value);

    public void SetUniform(string name, in Vector3 value) => Write(name, value);

    public void SetUniform(string name, in Vector4 value) => Write(name, value);

    public void SetUniform(string name, in Matrix4x4 value) => Write(name, value);

    /// <summary>Sets a whole array of vectors in one call, <c>float2 uName[N]</c>.</summary>
    public void SetUniform(string name, ReadOnlySpan<Vector2> values) => WriteArray(name, values);

    /// <summary>Sets a whole array of vectors in one call, <c>float4 uName[N]</c>.</summary>
    public void SetUniform(string name, ReadOnlySpan<Vector4> values) => WriteArray(name, values);

    /// <summary>Sets a whole array of floats in one call, <c>float uName[N]</c>.</summary>
    public void SetUniform(string name, ReadOnlySpan<float> values) => WriteArray(name, values);

    /// <summary>Sets one element of an array, <c>uName[i]</c>.</summary>
    public void SetUniform<T>(string name, int index, in T value) where T : unmanaged
    {
        if (!Find(name, out var member) || index < 0 || index >= Math.Max(member.ArrayLength, 1)) return;

        int at = member.Offset + index * (member.ArrayLength > 0 ? member.ArrayStride : member.Size);
        MemoryMarshal.Write(block.AsSpan(at), in value);
        dirty = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Write<T>(string name, in T value) where T : unmanaged
    {
        if (!Find(name, out var member)) return;

        int bytes = Math.Min(Unsafe.SizeOf<T>(), member.Size);
        MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value))[..bytes].CopyTo(block.AsSpan(member.Offset));
        dirty = true;
    }

    private void WriteArray<T>(string name, ReadOnlySpan<T> values) where T : unmanaged
    {
        if (!Find(name, out var member)) return;

        int count = Math.Min(values.Length, Math.Max(member.ArrayLength, 1));
        int stride = member.ArrayLength > 0 ? member.ArrayStride : member.Size;
        int bytes = Math.Min(Unsafe.SizeOf<T>(), member.Size);

        for (int i = 0; i < count; i++)
            MemoryMarshal.AsBytes(values.Slice(i, 1))[..bytes].CopyTo(block.AsSpan(member.Offset + i * stride));

        dirty = true;
    }

    private bool Find(string name, out UniformBlockLayout.Member member)
    {
        if (shader.Params.TryGet(name, out member)) return true;

        lock (warned)
        {
            if (warned.Add((shader.Handle, name)))
                Log.Warning($"[Technique] {shader.Name ?? "a shader"} has no uniform called '{name}' in its Params, it is being ignored.");
        }

        return false;
    }

    /// <summary>Called after the shader is bound.</summary>
    protected virtual void SetUniforms()
    { }

    public void Bind()
    {
        var device = GraphicsDevice.Current;
        device.BindShader(shader);
        device.BoundTechnique = this;

        // Whatever the block holds goes to the GPU at the next draw, another technique may have been bound since
        dirty = true;
        SetUniforms();
    }

    public void Unbind()
    {
        var device = GraphicsDevice.Current;
        if (ReferenceEquals(device.BoundTechnique, this)) device.BoundTechnique = null;
        device.BindShader(null);
    }

    /// <summary>Hands the block to the device if anything in it changed. The device calls this before every draw.</summary>
    internal void Flush(GraphicsDevice device)
    {
        if (!dirty || block.Length == 0) return;

        device.SetParams(block);
        dirty = false;
    }
}
