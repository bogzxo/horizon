namespace Horizon.Rendering.Lighting;

/// <summary>
/// The settings of the ambient occlusion of a <see cref="DeferredRenderer2D"/>, see its <c>AmbientOcclusion</c>.
/// Ambient light comes from everywhere, so what is hemmed in gets less of it, the corner where the floor meets a
/// wall, the ground under a fighter's feet, the air right next to a pillar. The deferred pass marches a few short
/// rays through the distance field of what blocks light round every pixel it lights (walls and sprites alike) and
/// takes a share of the ambient and the bounced light for every one that meets something, and the tiles can bring
/// an occlusion map of their own (<c>_ao</c> next to the image, white for open) that is taken into account too.
/// It is cheap, a few dozen reads of the field a pixel, and it is part of the plain deferred lighting, path traced
/// or not. Off to begin with. Every setting takes at the next frame, from any thread.
/// </summary>
public sealed class AmbientOcclusion2D
{
    /// <summary>Whether there is any. Off to begin with.</summary>
    public bool Enabled { get; set; }

    /// <summary>How far round a pixel the field is looked at, in world units. Further is darker corners that reach further out.</summary>
    public float Radius { get; set; } = 24.0f;

    /// <summary>How dark a corner gets at most, from 0 (not at all) to 1 (no ambient light at all in the tightest corner).</summary>
    public float Strength { get; set; } = 0.7f;

    /// <summary>How many directions are marched round every pixel. More is smoother and costs as much more.</summary>
    public int Samples { get; set; } = 8;

    /// <summary>How much the occlusion maps of the tiles count, 0 to leave them out, 1 to take them as painted.</summary>
    public float BakedStrength { get; set; } = 1.0f;

    /// <summary>
    /// How much of the direct light (the lights themselves) the occlusion takes as well, 0 for none, which is how
    /// ambient occlusion usually goes, a lamp shines into a corner all the same. Up to 1 for all of it.
    /// </summary>
    public float DirectStrength { get; set; }

    /// <summary>Shows the occlusion on its own in place of the picture, for seeing what it is up to.</summary>
    public bool Show { get; set; }

    /// <summary>The settings as of one tick, see <see cref="DeferredRenderer2D.Capture"/>.</summary>
    public readonly record struct State(bool Enabled, float Radius, float Strength, int Samples, float BakedStrength, float DirectStrength, bool Show);

    internal State Capture() => new(Enabled, Radius, Strength, Samples, BakedStrength, DirectStrength, Show);
}
