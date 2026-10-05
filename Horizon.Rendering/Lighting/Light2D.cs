using System.Numerics;

namespace Horizon.Rendering.Lighting;

/// <summary>
/// A point light of a <see cref="DeferredRenderer2D"/>. It lights everything within its radius, brightest at the centre
/// and fading to nothing at the edge. Hand it over with <see cref="DeferredRenderer2D.AddLight"/> and change it whenever,
/// whatever it is set to when a frame is drawn is what that frame is lit with.
/// </summary>
public sealed class Light2D
{
    /// <summary>Where the light is in the world.</summary>
    public Vector2 Position { get; set; }

    public Vector3 Color { get; set; } = Vector3.One;

    /// <summary>How far the light reaches, in world units.</summary>
    public float Radius { get; set; } = 128.0f;

    /// <summary>
    /// How bright the light is at its centre. On top of an ambient of 1 anything lit is brighter than it was painted,
    /// which is fine: what goes over is rolled off towards white rather than cut off.
    /// </summary>
    public float Intensity { get; set; } = 1.0f;

    /// <summary>
    /// How far the light hovers in front of the world, which only matters to surfaces with a normal map:
    /// the lower it is the more it rakes across them.
    /// </summary>
    public float Height { get; set; } = 48.0f;

    /// <summary>
    /// How much of the light hangs in the air around it (0 for none), whatever is behind it.
    /// This is what makes a torch glow in front of a wall that is too dark to show much of being lit.
    /// </summary>
    public float Glow { get; set; } = 0.0f;

    /// <summary>
    /// How big the light itself is, in world units. A light with no size is a point and its shadows have a hard edge.
    /// Give it one and the edges turn soft: what is only hidden from a part of the light is only partly in shadow,
    /// more so the further the shadow falls from what casts it. That costs a few times what a hard shadow does.
    /// </summary>
    public float Size { get; set; } = 0.0f;

    /// <summary>
    /// How much the light wavers (0 for steady), the way a flame does: it gets a little brighter and dimmer,
    /// and reaches a little further and less far, never quite the same way twice. Every light wavers by itself.
    /// </summary>
    public float Flicker { get; set; } = 0.0f;

    /// <summary>
    /// Whether what is solid (see <see cref="DeferredRenderer2D.Occlusion"/>) blocks this light.
    /// Lights without shadows are cheaper, and right for the ones that are only there to fill in.
    /// </summary>
    public bool CastsShadows { get; set; } = true;

    /// <summary>A light that is switched off stays where it is but lights nothing.</summary>
    public bool Enabled { get; set; } = true;
}
