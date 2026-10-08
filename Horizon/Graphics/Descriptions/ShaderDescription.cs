using Horizon.Content.Descriptions;

namespace Horizon.Graphics;

/// <summary>One file of a program and the stage it is for, or the source itself for one made up in code.</summary>
public readonly record struct ShaderDefinition(ShaderStage Stage, string? File, string Source = "");

/// <summary>
/// The stages that make up a program.
/// </summary>
public readonly struct ShaderDescription : IAssetDescription
{
    public readonly ShaderDefinition[] Definitions { get; init; }

    /// <summary>
    /// The program of a name in a folder, so "shaders/post" and "blur" is blur.slang (or blur.hlsl) in there. One file
    /// holds every stage of it, the stage in the definition only says what the file is for when it holds one.
    /// </summary>
    public static ShaderDescription FromPath(in string path, in string name)
    {
        if (!Path.Exists(path))
            return new ShaderDescription { Definitions = [] };

        List<ShaderDefinition> definitions = [];
        foreach (string file in Directory.GetFiles(path, $"{name}.*"))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant().Trim('.');
            if (ext is not ("slang" or "hlsl"))
                continue;

            definitions.Add(new ShaderDefinition(ShaderStage.Fragment, file));
        }

        return new ShaderDescription { Definitions = [.. definitions] };
    }

    /// <summary>
    /// A program that draws over the whole screen out of one Slang file, which is every post pass. The file includes
    /// common/screen.slang for its vertex stage and has the fragment stage of its own.
    /// </summary>
    public static ShaderDescription Screen(string path) => new() { Definitions = [new ShaderDefinition(ShaderStage.Fragment, path)] };

    /// <summary>A compute program out of one file.</summary>
    public static ShaderDescription Compute(string path) => new() { Definitions = [new ShaderDefinition(ShaderStage.Compute, path)] };
}
