using Horizon.Content;

using Silk.NET.Vulkan;

namespace Horizon.Graphics;

/// <summary>
/// A compiled program, its stages as the GPU has them and the layout of its <c>Params</c> block. Loading one is a
/// line, and it is only compiled the first time anybody asks for it (and kept on disk after, see <see cref="ShaderCompiler"/>).
/// <code>
/// Shader shader = Shader.Load("shaders/spritebatch", "sprites");
/// </code>
/// Most things don't hold one of these, they hold a <see cref="Technique"/>, which is the shader and its uniforms.
/// </summary>
public sealed class Shader : GpuResource
{
    /// <summary>One stage of the program, with the module it is in and the entry point to start at.</summary>
    internal readonly record struct Stage(ShaderStage Kind, ShaderModule Module, string EntryPoint);

    private readonly GraphicsDevice? device;

    internal readonly List<Stage> Modules = [];

    /// <summary>Whether this is a compute program, which is dispatched rather than drawn with.</summary>
    public bool IsCompute { get; }

    /// <summary>Where every member of the Params block sits, for <see cref="Technique"/>.</summary>
    public UniformBlockLayout Params { get; } = UniformBlockLayout.Empty;

    /// <summary>How big a work group of a compute program is, as the shader declares it. Zeros for a program that isn't compute.</summary>
    public uint[] WorkGroupSize { get; } = [0, 0, 0];

    public override bool IsValid => base.IsValid && Modules.Count > 0;

    public static Shader Invalid { get; } = new();

    private Shader() : base(invalid: true)
    { }

    private Shader(GraphicsDevice device, CompiledShader compiled, string name)
    {
        this.device = device;
        Name = name;
        Params = compiled.Params;

        // The stages of one Slang file share a module, so each distinct SPIR-V is made once
        var modules = new Dictionary<byte[], ShaderModule>(ReferenceEqualityComparer.Instance);
        foreach (var stage in compiled.Stages)
        {
            if (!modules.TryGetValue(stage.Spirv, out ShaderModule module))
                modules[stage.Spirv] = module = device.CreateShaderModule(stage.Spirv);

            Modules.Add(new Stage(stage.Stage, module, stage.EntryPoint));

            if (stage.Stage == ShaderStage.Compute)
            {
                IsCompute = true;
                if (stage.ThreadGroupSize.Length == 3) WorkGroupSize = stage.ThreadGroupSize;
            }
        }
    }

    /// <summary>
    /// Loads and compiles a program out of every file of a name in a folder, so "shaders/post" and "blur" is blur.slang
    /// (or blur.vert and blur.frag) in there. Only compiled the first time, everybody who asks after gets the same one.
    /// </summary>
    /// <returns>The shader, or <see cref="Invalid"/> if it didn't compile (and what the compiler had to say about that is in the log).</returns>
    public static Shader Load(string directory, string name) =>
        Load($"{directory}/{name}", ShaderDescription.FromPath(directory, name));

    /// <summary>Compiles a program out of whatever a description says, and keeps it under a name for everybody who asks after.</summary>
    public static Shader Load(string name, in ShaderDescription description) =>
        ObjectManager.Instance.Shaders.CreateOrGet(name, description) ?? Invalid;

    /// <summary>How many work groups a dispatch over so many items needs, one way, for a compute program.</summary>
    public uint GroupsFor(uint items, int axis = 0)
    {
        uint size = axis < WorkGroupSize.Length ? WorkGroupSize[axis] : 0;
        return size == 0 ? items : (items + size - 1) / size;
    }

    /// <summary>Compiles a program the way a description says. What the asset manager calls.</summary>
    internal static bool TryCreate(in ShaderDescription description, out AssetCreationResult<Shader> result)
    {
        string name = description.Definitions.Length > 0 ? Path.GetFileNameWithoutExtension(description.Definitions[^1].File ?? "shader") : "shader";
        CompiledShader? compiled = ShaderCompiler.Compile(description, name, out string error);

        if (compiled is null)
        {
            result = new AssetCreationResult<Shader> { Asset = Invalid, Message = $"[ShaderCompiler] {name} didn't compile:\n{error}", Status = AssetCreationStatus.Failed };
            return false;
        }

        result = new AssetCreationResult<Shader> { Asset = new Shader(GraphicsDevice.Current, compiled, name), Status = AssetCreationStatus.Success, Message = string.Empty };
        return true;
    }

    protected override void DestroyCore() => device?.DestroyShader(this);
}
