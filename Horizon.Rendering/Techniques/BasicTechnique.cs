using Logger = Bogz.Logging.Loggers.ConcurrentLogger;

using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Descriptions;

using Silk.NET.Core.Native;

namespace Horizon.Rendering.Techniques;

public class BasicTechnique : Technique
{
    private const string UNIFORM_VIEW = "uCameraView";
    private const string UNIFORM_PROJECTION = "uCameraProjection";

    public BasicTechnique()
    {
        if (GameEngine
                .Instance
                .ObjectManager
                .Shaders
                .TryCreateOrGet(
                    "basic_technique",
                    ShaderDescription.FromPath("shaders/basic", "basic_technique"),
                    out var result
                ))
        {
            SetShader(result.Asset);
        }
        else
        {
            Logger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
        }
    }

    protected override void SetUniforms()
    {
        SetUniform(
            UNIFORM_VIEW,
            GameEngine.Instance.ActiveCamera.View
        );
        SetUniform(
            UNIFORM_PROJECTION,
            GameEngine.Instance.ActiveCamera.Projection
        );
    }
}

public class BasicMaterialTechnique : Technique
{
    private const string UNIFORM_VIEW = "uCameraView";
    private const string UNIFORM_PROJECTION = "uCameraProjection";

    public BasicMaterialTechnique()
    {
        if (GameEngine
                .Instance
                .ObjectManager
                .Shaders
                .TryCreateOrGet(
                    "basic_material_technique",
                    ShaderDescription.FromPath("shaders/basic", "basic_material"),
                    out var result
                ))
        {
            SetShader(result.Asset);
        }
        else
        {
            Logger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
        }
    }

    protected override void SetUniforms()
    {
        SetUniform(
            UNIFORM_VIEW,
            GameEngine.Instance.ActiveCamera.View
        );
        SetUniform(
            UNIFORM_PROJECTION,
            GameEngine.Instance.ActiveCamera.Projection
        );
    }
}