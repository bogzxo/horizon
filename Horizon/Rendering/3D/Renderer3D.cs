namespace Horizon.Rendering;

/// <summary>
/// A <see cref="Renderer2D"/> for a world with depth. The one difference is that what is added to it is drawn with
/// the depth test on, so a thing in front hides the thing behind it whatever order they were added in, and the
/// depth attachment every renderer has anyway (the sprites use it for their stencil) finally earns its keep. A
/// <see cref="Meshes.Mesh3D"/> goes in here, a <see cref="Engine.Camera3D"/> is the scene's camera, and the
/// picture comes out through the same post processing and onto the same screen as everything else.
/// <code>
/// var world = AddEntity(new Renderer3D(width, height) { FollowWindow = true });
/// world.AddEntity(Mesh3D.Cube(1.0f));
/// </code>
/// A sprite batch or a UI can still be added to it, they draw flat with their depth test off and end up on top.
/// </summary>
public class Renderer3D : Renderer2D
{
    public Renderer3D(in uint width, in uint height) : base(width, height) { }

    protected override bool DepthForChildren => true;
}
