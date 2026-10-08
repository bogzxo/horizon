using Horizon.Content;
using Horizon.Content.Descriptions;
using Horizon.Core;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;
using Horizon.OpenGL.Processors;

using Silk.NET.OpenGL;

using Shader = Horizon.OpenGL.Assets.Shader;

namespace Horizon.OpenGL.Factories;

/// <summary>
/// Asset factory for creating instances of <see cref="Shader"/>.
/// </summary>
public class ShaderFactory : IAssetFactory<Shader, ShaderDescription>
{
    public static bool TryCreate(
        in ShaderDescription description,
        out AssetCreationResult<Shader> result
        )
    {
        // Program compilation result.
        var asset = new Shader { Handle = ObjectManager.GL.CreateProgram() };

        // Every stage as the compiler gets to see it, includes and all: what a program that was kept is looked up by
        var sources = PreprocessSources(description.Definitions);

        // Made before (by this machine, with this driver) and kept: no compiling, which is what makes a scene that
        // is set up for the first time hitch
        string? cached = ShaderCache.KeyFor(sources);
        if (cached is not null && ShaderCache.TryLoad(asset.Handle, cached))
        {
            result = new() { Asset = asset, Status = AssetCreationStatus.Success };
            return true;
        }

        // Enumerate and compile each program in the source.
        var aggregatedResults = sources.Select(source => CompileShaderFromSource(source.Type, source.Source)).ToArray();
        var failed = aggregatedResults.Where(res => res.Status == CompilationStatus.Fail).ToArray();

        // If any of our shaders failed to compile throw an error.
        if (failed.Length != 0)
        {
            string errorMessage =
                "[ShaderFactory] Cannot compile Program({handle}) with failed shaders:\n\n";
            foreach (var item in failed)
                errorMessage += $"[{item.Handle}:{item.Status}] {item.ErrorMessage}\n";

            result = new()
            {
                Asset = asset,
                Message = errorMessage,
                Status = AssetCreationStatus.Failed
            };
            return false;
        }

        // Attach each shader to the program.
        foreach (var res in aggregatedResults)
            ObjectManager.GL.AttachShader(asset.Handle, res.Handle);

        // So what it links to can be kept for next time
        if (cached is not null)
            ShaderCache.PrepareToKeep(asset.Handle);

        // Attempt to link them together.
        ObjectManager.GL.LinkProgram(asset.Handle);

        // (cleanup) Detach and delete each shader to the program.
        foreach (var res in aggregatedResults)
        {
            ObjectManager.GL.DetachShader(asset.Handle, res.Handle);
            ObjectManager.GL.DeleteShader(res.Handle);
        }

        // Check for linking errors.
        ObjectManager.GL.GetProgram(asset.Handle, GLEnum.LinkStatus, out var status);
        if (status == 0)
        {
            result = new()
            {
                Asset = asset,
                Message =
                    $"Shader Failed to link with error: {ObjectManager.GL.GetProgramInfoLog(asset.Handle)}",
                Status = AssetCreationStatus.Failed
            };
            return false;
        }

        if (cached is not null)
            ShaderCache.Keep(asset.Handle, cached);

        // Success
        result = new() { Asset = asset, Status = AssetCreationStatus.Success };
        return true;
    }

    /// <summary>
    /// Helper method to put every stage of a program through the preprocessor, which is what is compiled.
    /// </summary>
    private static (ShaderType Type, string Source)[] PreprocessSources(ShaderDefinition[] shaderDefinitions)
    {
        using var preprocessor = new ShaderDirectiveProcessor();

        var sources = new (ShaderType, string)[shaderDefinitions.Length];
        for (int i = 0; i < shaderDefinitions.Length; i++)
        {
            var (type, file, source) = shaderDefinitions[i];

            // If a file is provided we can manually parse #include preprocessor statements for convenience.
            sources[i] = (type, file is null
                ? preprocessor.ProcessSource(string.Empty, source.SplitToLines())
                : preprocessor.ProcessFile(file));
        }

        return sources;
    }

    /* Internal data structures to help transfer state information between stages. */

    private enum CompilationStatus
    {
        Pass = 0,
        Fail = 1
    }

    private readonly record struct CompilationResult(
        uint Handle,
        CompilationStatus Status,
        string ErrorMessage
    );

    /// <summary>
    /// Compiles the shader from source.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <param name="source">The source.</param>
    /// <returns>A <see cref="CompilationResult"/> fully encapsulating the result of attempting to compile the shader, including the shader handle if successful.</returns>
    private static CompilationResult CompileShaderFromSource(in ShaderType type, in string source)
    {
        // TryCreate a shader handle.
        uint handle = ObjectManager.GL.CreateShader(type);

        // Shader compilation result.
        CompilationResult result =
            new()
            {
                ErrorMessage = "",
                Status = CompilationStatus.Pass,
                Handle = handle
            };

        // Stream shader source into the gl shader.
        ObjectManager.GL.ShaderSource(handle, source);

        // Attempt to compile the shader.
        ObjectManager.GL.CompileShader(handle);

        // Whether it compiled is what the driver says it is. What it has to say besides is not always an error: some
        // drivers warn about things others don't mention, and a shader that compiled is not to be thrown away for that
        ObjectManager.GL.GetShader(handle, ShaderParameterName.CompileStatus, out int compiled);
        string infoLog = ObjectManager.GL.GetShaderInfoLog(handle);

        if (compiled == 0)
        {
            return result with
            {
                Status = CompilationStatus.Fail,
                ErrorMessage = $"[ShaderFactory] Error compiling shader of type {type}: {infoLog}"
            };
        }

        if (!string.IsNullOrWhiteSpace(infoLog))
            Horizon.Logging.Log.Warning($"[ShaderFactory] The {type} compiled, with something to say: {infoLog.Trim()}");

        return result;
    }
}
