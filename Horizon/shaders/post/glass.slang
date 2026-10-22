// A pane of glass, what is behind a component blurred and laid inside its bounds with round corners, for a UI that
// draws in goes (see UIGlassPass). One triangle over the lot and everything outside the pane thrown away, which for a
// few panes a frame is cheaper than it sounds. The corners are worked out per pixel like the UI's own boxes, so the
// glass and the box painted over it have the same edge.
//
// Two kinds of it. With no bend it's frosted, the blur laid flat. With a bend it's liquid, Apple's Liquid Glass more or
// less, a slab with a rounded rim that bends what's behind it hard as the rim gets near (what's just outside the pane
// squeezed into its edge), splits the light a little there the way a prism does, and blurs less at the rim than in the
// middle. It costs a handful of reads more a pixel of the pane than the frosted kind and nothing else, the blur is the
// dear part and both have that.

#include <common/screen.slang>

struct Params
{
    // The pane and what it's cut down to, in pixels of what is drawn into from its top left, min then max
    float4 uRect;
    float4 uClip;

    // How round its corners are in pixels, how much of it is there, and how much of the blurred picture is shown
    // over the sharp one (all of it, unless the blur is barely there, see GaussianBlur.Share)
    float uRadius;
    float uOpacity;
    float uShare;

    // How far what is behind is pulled in at the rim, in pixels, the lens of a thick piece of glass, and how big a
    // pixel of what is drawn into is in texture coordinates
    float uBend;
    float2 uTexel;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;
BIND_TEXTURE(0) Sampler2D uSource;
BIND_TEXTURE(1) Sampler2D uSharp;

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    float2 p = input.position.xy;
    if (any(p < params.uClip.xy) || any(p > params.uClip.zw))
        discard;

    // How far outside of the rounded box the pixel is, the usual distance to a box with its corners taken off
    float2 middle = (params.uRect.xy + params.uRect.zw) * 0.5;
    float2 extent = max((params.uRect.zw - params.uRect.xy) * 0.5, float2(0.0, 0.0));
    float radius = min(params.uRadius, min(extent.x, extent.y));
    float2 q = abs(p - middle) - (extent - radius);
    float outside = length(max(q, float2(0.0, 0.0))) + min(max(q.x, q.y), 0.0) - radius;

    float coverage = saturate(0.5 - outside) * params.uOpacity;
    if (coverage <= 0.0)
        discard;

    // Which way is out of the box from here, along the axis that is nearest its edge or out of the round of a corner
    float2 outward = q.x > q.y ? float2(1.0, 0.0) : float2(0.0, 1.0);
    if (q.x > 0.0 && q.y > 0.0)
        outward = normalize(q);
    outward *= sign(p - middle);

    float3 colour;
    if (params.uBend <= 0.0)
    {
        // Frosted, the blur as it is
        float2 uv = input.texCoords;
        colour = lerp(uSharp.Sample(uv).rgb, uSource.Sample(uv).rgb, params.uShare);
    }
    else
    {
        // Liquid. How far into the rim this is, 0 on the flat of the slab and 1 at its very edge, the rim three bends wide
        float rim = max(params.uBend * 3.0, 6.0);
        float t = saturate(1.0 + outside / rim);

        // The rim is the round of a quarter circle, its slope steepening to the edge, and what's behind is taken from as
        // far further out as the slope is steep. Held short of straight up at the very edge or it reads from the moon
        float slope = min(t / sqrt(max(1.0 - t * t, 0.0625)), 4.0);
        float2 bend = outward * params.uBend * slope * params.uTexel;

        // Split, the red bent the least and the blue the most, only where it bends at all
        float2 uv = input.texCoords;
        float3 sharp = float3(uSharp.Sample(uv + bend * 0.94).r, uSharp.Sample(uv + bend).g, uSharp.Sample(uv + bend * 1.08).b);
        float3 soft = float3(uSource.Sample(uv + bend * 0.94).r, uSource.Sample(uv + bend).g, uSource.Sample(uv + bend * 1.08).b);

        // Less of the blur at the rim, where the glass is a lens rather than a pane, and a thin line of light right on the
        // edge. It lifted the whole pane a little towards white as well, which on a dark theme was the wash over everything
        colour = lerp(sharp, soft, params.uShare * (1.0 - 0.55 * t * t));
        colour += pow(t, 8.0) * 0.06;
    }

    // The sheen, Liquid Glass's light caught along the top inside of the pane, strongest at the rim and gone by a
    // third of the way down. One term, no reads
    float fromTop = (p.y - params.uRect.y) / max(extent.y * 0.66, 1.0);
    float sheen = pow(saturate(1.0 - fromTop), 3.0) * 0.035;
    colour += sheen;

    return float4(colour, coverage);
}
