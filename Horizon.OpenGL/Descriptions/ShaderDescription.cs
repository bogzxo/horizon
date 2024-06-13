﻿using Horizon.Content.Descriptions;

using Silk.NET.OpenGL;

namespace Horizon.OpenGL.Descriptions;

/// <summary>
/// Contains the data needed to create a new <see cref="Shader"/>
/// </summary>
public readonly struct ShaderDescription : IAssetDescription
{
    public readonly ShaderDefinition[] Definitions { get; init; }

    /// <summary>
    /// Creates a shader description from files contained in a directory.
    /// </summary>
    /// <param name="path">The path to be searched.</param>
    /// <param name="name">The common file name of all shaders.</param>
    public static ShaderDescription FromPath(in string path, in string name)
    {
        if (!Path.Exists(path))
            return default;

        List<ShaderDefinition> definitions = new();
        var files = Directory.GetFiles(path, $"{name}.*");
        foreach (var file in files)
        {
            // yummy expensive string manipulations
            string ext = Path.GetExtension(file).ToLower().Trim('.');
            if (ext.CompareTo("h") == 0) continue;
            definitions.Add(
                new ShaderDefinition
                {
                    Type = ext switch
                    {
                        "vert" or "vs" or "vsh" => ShaderType.VertexShader,
                        "frag" or "fs" or "fsh" => ShaderType.FragmentShader,
                        "comp" or "cs" or "csh" => ShaderType.ComputeShader,
                        "geom" or "gs" or "gsh" => ShaderType.GeometryShader,
                    },
                    File = file
                }
            );
        }

        return new ShaderDescription { Definitions = definitions.ToArray() };
    }
}