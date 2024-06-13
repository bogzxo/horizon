using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Descriptions;

namespace AutoVoxel.Rendering;

public class ChunkTechnique : Technique
{
    private const string UNIFORM_VIEW = "uCameraView";
    private const string UNIFORM_PROJECTION = "uCameraProjection";

    public ChunkTechnique()
    {
        if (GameEngine
                .Instance
                .ObjectManager
                .Shaders
                .TryCreateOrGet(
                    "chunk_technique",
                    ShaderDescription.FromPath("shaders/", "world"),
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