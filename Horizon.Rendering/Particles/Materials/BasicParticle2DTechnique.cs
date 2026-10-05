using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;

namespace Horizon.Rendering.Particles.Materials
{
    public class BasicParticle2DTechnique : Technique
    {
        private const string UNIFORM_STARTCOLOR = "uStartColor";
        private const string UNIFORM_ENDCOLOR = "uEndColor";
        private const string UNIFORM_STARTEMISSIVE = "uStartEmissive";
        private const string UNIFORM_ENDEMISSIVE = "uEndEmissive";
        private ParticleRenderer2D renderer;
        private bool initialized = false;

        public BasicParticle2DTechnique(in ParticleRenderer2D renderer)
        {
            this.renderer = renderer;

            if (GameEngine
                .Instance
                .ObjectManager
                .Shaders
                .TryCreateOrGet("particle2d",
                ShaderDescription.FromPath(
                    "shaders/particle",
                    "basic"),
                out var result))
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
            SetUniform(UNIFORM_STARTCOLOR, renderer.StartColor);
            SetUniform(UNIFORM_ENDCOLOR, renderer.EndColor);
            SetUniform(UNIFORM_STARTEMISSIVE, renderer.StartEmissive);
            SetUniform(UNIFORM_ENDEMISSIVE, renderer.EndEmissive);
        }
    }
}