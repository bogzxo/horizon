using Horizon.Graphics;

namespace Horizon.Rendering.Particles.Materials
{
    /// <summary>The look of a <see cref="ParticleRenderer2D"/>'s particles, shaders/particle/particle.slang with its colours set from the renderer.</summary>
    public class BasicParticle2DTechnique : Technique
    {
        private const string UNIFORM_STARTCOLOR = "uStartColor";
        private const string UNIFORM_ENDCOLOR = "uEndColor";
        private const string UNIFORM_STARTEMISSIVE = "uStartEmissive";
        private const string UNIFORM_ENDEMISSIVE = "uEndEmissive";
        private readonly ParticleRenderer2D renderer;

        public BasicParticle2DTechnique(in ParticleRenderer2D renderer)
        {
            this.renderer = renderer;

            LoadShader("shaders/particle", "particle");
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
