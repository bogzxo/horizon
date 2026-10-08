// The outputs of everything that is drawn into a Renderer2D, see DeferredRenderer2D for what goes where. Drawn
// straight to the window (or into a renderer that doesn't light) only the first of them goes anywhere.
//
//   albedo    what was drawn, unlit
//   surface   rg the normal (0.5 being none), b how emissive it is
//   material  r how shiny it is, g how much of the pixel blocks light (sprites that cast shadows), b how much it
//             lights what is around it (emissive that is a lamp to the path traced lighting, not just unlit, a
//             sprite flashed on a hit shows bright but lights nothing)
//
// The alpha of every one is how much of the pixel the fragment covers, they are all blended alike.

struct GBuffer
{
    float4 albedo : SV_Target0;
    float4 surface : SV_Target1;
    float4 material : SV_Target2;
};

// A fragment that has no surface to speak of (flat, not shiny).
GBuffer writeFlat(float4 albedo, float emissive)
{
    GBuffer output;
    output.albedo = albedo;
    output.surface = float4(0.5, 0.5, emissive, albedo.a);
    output.material = float4(0.0, 0.0, emissive, albedo.a);
    return output;
}
