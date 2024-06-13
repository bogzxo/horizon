using System.Numerics;

using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Descriptions;

namespace CodeKneading.Rendering;

public class ShadowTechnique : Technique
{
    private const string UNIFORM_VIEW = "uCameraView";
    private const string UNIFORM_PROJECTION = "uCameraProjection";

    private Matrix4x4 SunProj, SunView;

    public ShadowTechnique()
    {
        if(
            GameEngine
                .Instance
                .ObjectManager
                .Shaders
                .TryCreateOrGet(
                    "world_technique",
                    ShaderDescription.FromPath("content/shaders/", "shadow"),
                    out var result
                )
        )
        {
            SetShader(result.Asset);
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
        }
    }

    public void SetSun(ref readonly Matrix4x4 proj, ref readonly Matrix4x4 view)
    {
        SunProj = proj;
        SunView = view;
    }

    protected override void SetUniforms()
    {
        //BindBuffer("CameraData", MainScene.CameraData);

        SetUniform("view", SunView);
        SetUniform("projection", SunProj);
    }
}
