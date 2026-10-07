using Bogz.Logging;

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
        LoadShader("shaders/basic", "basic_technique");
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
        LoadShader("shaders/basic", "basic_material");
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