using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using CodeKneading.Player;
using CodeKneading.Voxel;

using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Descriptions;

using Silk.NET.Core.Native;

namespace CodeKneading.Rendering;

public class WorldTechnique : Technique
{
    private const string UNIFORM_VIEW = "uCameraView";
    private const string UNIFORM_PROJECTION = "uCameraProjection";
    private const string UNIFORM_CAMPOS = "uCameraPosition";

    public WorldTechnique()
    {
        if (
            GameEngine
                .Instance
                .ObjectManager
                .Shaders
                .TryCreateOrGet(
                    "world_technique",
                    ShaderDescription.FromPath("content/shaders/", "world"),
                    out var result
                ))
        {
            SetShader(result.Asset);
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
        }
    }

    protected override void SetUniforms()
    {
        BindBuffer(2, MainScene.CameraData);
    }
}
