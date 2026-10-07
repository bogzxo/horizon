using Horizon.Core.Primitives;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

namespace Horizon.OpenGL.Assets;

public class Shader : IGLObject
{
    public uint Handle { get; init; }

    public bool IsValid => Handle != 0;

    public static Shader Invalid { get; } = new Shader { Handle = 0 };

    /// <summary>
    /// Loads and compiles a shader out of every file of a name in a folder, so "shaders/post" and "blur" is blur.vert and blur.frag in there.
    /// It is only compiled the first time, everybody who asks after gets the same one.
    /// </summary>
    /// <returns>The shader, or <see cref="Invalid"/> if it didn't compile (and what the compiler had to say about that is in the log).</returns>
    public static Shader Load(string directory, string name) =>
        Load($"{directory}/{name}", ShaderDescription.FromPath(directory, name));

    /// <summary>
    /// Compiles a shader out of whatever a description says, and keeps it under a name for everybody who asks after.
    /// </summary>
    public static Shader Load(string name, in ShaderDescription description) =>
        ObjectManager.Instance.Shaders.CreateOrGet(name, description) ?? Invalid;
}
