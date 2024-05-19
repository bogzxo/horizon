using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using CodeKneading.Voxel;

using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Descriptions;

namespace CodeKneading.Rendering;

public class WorldTechnique : Technique
{
    private const string UNIFORM_VIEW = "uCameraView";
    private const string UNIFORM_PROJECTION = "uCameraProjection";

    public WorldTechnique()
    {
        SetShader(
            GameEngine
                .Instance
                .ObjectManager
                .Shaders
                .CreateOrGet(
                    "world_technique",
                    ShaderDescription.FromPath("content/shaders/", "world")
                )
        );
    }

    protected override void SetUniforms()
    {
        SetUniform(
            UNIFORM_VIEW,
            GameEngine.Instance.SceneManager.CurrentInstance.ActiveCamera.View
        );

        SetUniform(
            UNIFORM_PROJECTION,
            GameEngine.Instance.SceneManager.CurrentInstance.ActiveCamera.Projection
        );
    }
}
public class ShadowTechnique : Technique
{
    private const string UNIFORM_VIEW = "uCameraView";
    private const string UNIFORM_PROJECTION = "uCameraProjection";

    public ShadowTechnique()
    {
        SetShader(
            GameEngine
                .Instance
                .ObjectManager
                .Shaders
                .CreateOrGet(
                    "world_technique",
                    ShaderDescription.FromPath("content/shaders/", "shadow")
                )
        );
    }

    protected override void SetUniforms()
    {
        SetUniform(
            UNIFORM_VIEW,
            SkyManager.SunView
        );

        SetUniform(
            UNIFORM_PROJECTION,
            SkyManager.SunProj
        );
    }
}
