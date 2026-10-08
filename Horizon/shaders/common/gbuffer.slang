// The outputs of everything that is drawn into a Renderer2D, see DeferredRenderer2D for what goes where. Drawn
// straight to the window (or into a renderer that doesn't light) only the first of them goes anywhere.
//
//   albedo    what was drawn, unlit
//   surface   rg the normal (0.5 being none), b how emissive it is
//   material  r how shiny it is, g above a half how much of the pixel blocks light (sprites that cast shadows
//             write their coverage there) and below it how open the pixel is to the ambient light as the art was
//             painted (a baked ambient occlusion map, see packOcclusion, everything without one says wide open),
//             b how much it lights what is around it (emissive that is a lamp to the path traced lighting, not
//             just unlit, a sprite flashed on a hit shows bright but lights nothing)
//
// The alpha of every one is how much of the pixel the fragment covers, they are all blended alike.
// The occlusion shares a channel with what blocks light because every texture unit of the deferred pass is spoken
// for, a fourth attachment would have to come in through the bindless table and cost a write of every pixel for
// one channel. Packed under the half the blocking is read above, the two never meet, and eight bits of a 0.4 range
// is a hundred shades of corner, plenty.

struct GBuffer
{
    float4 albedo : SV_Target0;
    float4 surface : SV_Target1;
    float4 material : SV_Target2;
};

// Where in the green of the material the occlusion goes, below what counts as blocking light
static const float OCCLUSION_SCALE = 0.4;

// How open a pixel is to the ambient light (1 for wide open), as the green of the material carries it.
float packOcclusion(float occlusion)
{
    return clamp(occlusion, 0.0, 1.0) * OCCLUSION_SCALE;
}

// And back, a pixel that blocks light (a sprite casting a shadow) is wide open to the ambient light as far as the art goes.
float unpackOcclusion(float green)
{
    return green > 0.5 ? 1.0 : clamp(green / OCCLUSION_SCALE, 0.0, 1.0);
}

// A fragment that has no surface to speak of (flat, not shiny, open to the ambient light).
GBuffer writeFlat(float4 albedo, float emissive)
{
    GBuffer output;
    output.albedo = albedo;
    output.surface = float4(0.5, 0.5, emissive, albedo.a);
    output.material = float4(0.0, packOcclusion(1.0), emissive, albedo.a);
    return output;
}
