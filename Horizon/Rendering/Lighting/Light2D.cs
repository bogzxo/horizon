using System.Numerics;

namespace Horizon.Rendering.Lighting;

/// <summary>What kind of light a <see cref="Light2D"/> is.</summary>
public enum LightType
{
    /// <summary>Shines every way from where it is, brightest in the middle and gone at its radius. A lamp, a torch, a fire.</summary>
    Point,

    /// <summary>
    /// The same but only within a cone, <see cref="Light2D.Direction"/> says which way and <see cref="Light2D.ConeAngle"/>
    /// how wide. A street lamp shining down, a torch somebody is pointing, a spotlight on a stage. Turn it by setting
    /// its direction, every frame if it is to swing.
    /// </summary>
    Spot,

    /// <summary>
    /// Everything is lit the same, from one direction, as if the light were far off. The sun, the moon, the glow of a
    /// city behind the hills. It has no position and no radius, its rays go the way of <see cref="Light2D.Direction"/>
    /// and its shadows are looked for as far back along them as <see cref="Light2D.Reach"/> says.
    /// </summary>
    Directional
}

/// <summary>
/// A light of a <see cref="DeferredRenderer2D"/>. A point light lights everything within its radius, brightest at the centre
/// and fading to nothing at the edge, a spot only within its cone and a directional light everything the same way, see
/// <see cref="LightType"/>. Hand it over with <see cref="DeferredRenderer2D.AddLight"/> and change it whenever,
/// whatever it is set to when a frame is drawn is what that frame is lit with.
/// </summary>
public sealed class Light2D
{
    /// <summary>What kind of light it is, a point light unless told otherwise.</summary>
    public LightType Type { get; set; } = LightType.Point;

    /// <summary>Where the light is in the world. A directional light has no use for it.</summary>
    public Vector2 Position { get; set; }

    /// <summary>
    /// Which way a spot shines, or a directional light's rays go, as an angle in radians, 0 along +X and turning
    /// towards +Y (anticlockwise with Y up, so straight down is -π/2). Set it every frame to swing or spin the light.
    /// </summary>
    public float Direction { get; set; } = -MathF.PI / 2.0f;

    /// <summary>
    /// How wide a spot's cone is, the whole of it, in radians. Within it the light is full, outside of it there is
    /// none, and <see cref="ConeSoftness"/> says how much of the edge fades between the two.
    /// </summary>
    public float ConeAngle { get; set; } = MathF.PI / 2.0f;

    /// <summary>
    /// How much of a spot's cone is the fade at its edge, 0 for a hard edged cone and 1 for one that fades all the
    /// way from its middle. Half makes a pool of light with a soft rim.
    /// </summary>
    public float ConeSoftness { get; set; } = 0.5f;

    /// <summary>
    /// How far back along its rays a directional light looks for something in the way, in world units. Longer
    /// shadows cost more steps and anything further off than this throws no shadow.
    /// </summary>
    public float Reach { get; set; } = 400.0f;

    /// <summary>
    /// How much of this light the sprites that block light (<see cref="Spriting.Sprite.CastsShadows"/>) take, 0 for
    /// none at all (they throw no shadow from it) and 1 for all of it, the way a wall does. In between a sprite
    /// throws a shadow that is only partly dark and the light shows through it by the rest, so a fighter standing in
    /// front of a lantern isn't able to put it out. Walls always take all of it.
    /// </summary>
    public float SpriteShadow { get; set; } = 1.0f;

    public Vector3 Color { get; set; } = Vector3.One;

    /// <summary>How far the light reaches, in world units.</summary>
    public float Radius { get; set; } = 128.0f;

    /// <summary>
    /// How bright the light is at its centre. On top of an ambient of 1 anything lit is brighter than it was painted,
    /// which is fine, what goes over is rolled off towards white rather than cut off.
    /// </summary>
    public float Intensity { get; set; } = 1.0f;

    /// <summary>
    /// How far the light hovers in front of the world, which only matters to surfaces with a normal map.
    /// The lower it is the more it rakes across them.
    /// </summary>
    public float Height { get; set; } = 48.0f;

    /// <summary>
    /// How much of the light hangs in the air around it (0 for none), whatever is behind it.
    /// This is what makes a torch glow in front of a wall that is too dark to show much of being lit.
    /// </summary>
    public float Glow { get; set; } = 0.0f;

    /// <summary>
    /// How big the light itself is, in world units. A light with no size is a point and its shadows have a hard edge.
    /// Give it one and the edges turn soft, what is only hidden from a part of the light is only partly in shadow,
    /// more so the further the shadow falls from what casts it. That costs a few times what a hard shadow does.
    /// </summary>
    public float Size { get; set; } = 0.0f;

    /// <summary>
    /// How much the light wavers (0 for steady), the way a flame does. It gets a little brighter and dimmer,
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
