// A pane of glass, what is behind a component blurred and laid inside its bounds with round corners, for a UI that
// draws in goes (see UIGlassPass). One triangle over the lot and everything outside the pane thrown away, which for a
// few panes a frame is cheaper than it sounds. The corners are worked out per pixel like the UI's own boxes, so the
// glass and the box painted over it have the same edge.
//
// Two kinds of it. With no bevel width it's frosted, the blur laid flat. With a bevel width it's liquid, after
// naughtyduk/liquidGL's optical model: center magnification, smooth bevel refraction with a steep pow(edge, 10) lip,
// bidirectional RGB chromatic dispersion, and screen/multiply dye tinting. Unlike liquidGL the bend goes straight
// across the nearest edge (lensNormal) rather than along the line from the middle, so every side bends alike.

#include <common/screen.slang>

struct Params
{
    float4 uRect;        //   0: pane min/max in pixels from top-left
    float4 uClip;        //  16: clip min/max in pixels from top-left
    float4 uSharpRect;   //  32: crop of uSharp in full-res pixels (xy = origin, zw = size)
    float4 uTint;        //  48: rgb = dye colour, a = dye strength
    float2 uTexel;       //  64: 1.0 / full output size
    float  uRadius;      //  72: corner radius in pixels
    float  uOpacity;     //  76: pane opacity [0..1]
    float  uShare;       //  80: GaussianBlur.Share(blurRadius) [0..1]
    float  uRefraction;  //  84: smooth bevel refraction in pixels
    float  uBevelDepth;  //  88: pow(edge, 10) steep lip depth in pixels
    float  uBevelWidth;  //  92: bevel width in pixels (0 = flat frosted)
    float  uMagnify;     //  96: center magnification (>= 1.0)
    float  uAberration;  // 100: bidirectional RGB chromatic dispersion factor
    float2 uPad;         // 104: alignment pad (total 112 bytes)
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;
BIND_TEXTURE(0) Sampler2D uSource;
BIND_TEXTURE(1) Sampler2D uSharp;

// Exact 2D rounded-box signed distance (negative inside, 0 on boundary, positive outside)
float roundedBoxDistance(float2 p, float2 extent, float radius)
{
    float2 q = abs(p) - (extent - radius);
    return length(max(q, float2(0.0, 0.0))) + min(max(q.x, q.y), 0.0) - radius;
}

// Which way the nearest edge of the lens is. Straight out of a straight edge and round a corner with its curve. The
// lens is the pane with its corners at least as round as the bevel is wide (see fsMain), so inside the bevel there is
// no diagonal where two edges are as near as each other, and no seam down a corner the way a picture frame has one.
// liquidGL bends along the line from the middle of the pane instead, which on anything wider than it is tall slides
// what is behind a long edge along that edge rather than across it, one way left of the middle and the other right.
float2 lensNormal(float2 p, float2 b, float r)
{
    float2 q = abs(p) - b + r;
    float2 s = float2(p.x < 0.0 ? -1.0 : 1.0, p.y < 0.0 ? -1.0 : 1.0);
    float2 m = max(q, float2(0.0, 0.0));
    float l = length(m);
    if (l > 0.0)
        return s * (m / l);
    return s * (q.x > q.y ? float2(1.0, 0.0) : float2(0.0, 1.0));
}

// A place past the edge of the picture folded back into it, the way a mirror at the edge would show it. A pane that
// sits against the side of the window bends there like it does everywhere else, rather than not at all.
float2 mirrored(float2 uv)
{
    uv = abs(uv);
    return float2(1.0, 1.0) - abs(float2(1.0, 1.0) - uv);
}

float2 sharpUV(float2 fullUV)
{
    float2 px = fullUV / params.uTexel - params.uSharpRect.xy;
    float2 uv = px / params.uSharpRect.zw;
    float2 halfTexel = 0.5 / params.uSharpRect.zw;
    return clamp(uv, halfTexel, float2(1.0, 1.0) - halfTexel);
}

// Applies light-screen / dark-multiply dye tinting.
float3 dye(float3 c)
{
    if (params.uTint.a <= 0.001)
        return c;
    float3 t = params.uTint.rgb;
    float light = dot(t, float3(0.2126, 0.7152, 0.0722));
    float3 dyed = light > 0.5 ? 1.0 - (1.0 - c) * (1.0 - t) : c * t;
    return lerp(c, dyed, params.uTint.a);
}

// 5-tap crisp cross sample of the sharp backdrop, matching liquidGL when u_frost == 0.
float3 sampleSharp5(float2 uv)
{
    float2 uvMin = params.uTexel * 0.5;
    float2 uvMax = float2(1.0, 1.0) - uvMin;
    float2 dx = float2(params.uTexel.x, 0.0);
    float2 dy = float2(0.0, params.uTexel.y);
    float3 sum = uSharp.Sample(sharpUV(clamp(uv, uvMin, uvMax))).rgb;
    sum += uSharp.Sample(sharpUV(clamp(uv + dx, uvMin, uvMax))).rgb;
    sum += uSharp.Sample(sharpUV(clamp(uv - dx, uvMin, uvMax))).rgb;
    sum += uSharp.Sample(sharpUV(clamp(uv + dy, uvMin, uvMax))).rgb;
    sum += uSharp.Sample(sharpUV(clamp(uv - dy, uvMin, uvMax))).rgb;
    return sum / 5.0;
}

// Samples the unbent glass colour, skipping whichever texture has negligible weight.
float3 sampleFlat(float2 uv, float share)
{
    float2 uvMin = params.uTexel * 0.5;
    float2 uvMax = float2(1.0, 1.0) - uvMin;
    uv = clamp(uv, uvMin, uvMax);
    if (share >= 0.999)
        return uSource.Sample(uv).rgb;
    if (share <= 0.001)
        return sampleSharp5(uv);
    return lerp(sampleSharp5(uv), uSource.Sample(uv).rgb, share);
}

// What is behind one place, the blur and the sharp picture mixed by the share of the blur, one tap of each
float3 sampleOne(float2 uv)
{
    uv = clamp(uv, params.uTexel * 0.5, float2(1.0, 1.0) - params.uTexel * 0.5);
    if (params.uShare >= 0.999)
        return uSource.Sample(uv).rgb;
    if (params.uShare <= 0.001)
        return uSharp.Sample(sharpUV(uv)).rgb;
    return lerp(uSharp.Sample(sharpUV(uv)).rgb, uSource.Sample(uv).rgb, params.uShare);
}

// The same with its colours parted, red short of the place and blue past it
float3 sampleParted(float2 uv, float2 chroma)
{
    if (params.uAberration <= 0.0001)
        return sampleOne(uv);
    return float3(sampleOne(uv - chroma).r, sampleOne(uv).g, sampleOne(uv + chroma).b);
}

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    float2 p = input.position.xy;
    if (any(p < params.uClip.xy) || any(p > params.uClip.zw))
        discard;

    float2 middle = (params.uRect.xy + params.uRect.zw) * 0.5;
    float2 extent = max((params.uRect.zw - params.uRect.xy) * 0.5, float2(0.0, 0.0));
    float minExtent = max(min(extent.x, extent.y), 1.0);
    float radius = min(params.uRadius, minExtent);
    float2 local = p - middle;

    float d = roundedBoxDistance(local, extent, radius);
    float coverage = saturate(0.5 - d) * params.uOpacity;
    if (coverage <= 0.0)
        discard;

    float mag = max(params.uMagnify, 0.001);
    float2 mapped = (middle + local / mag) * params.uTexel;

    float bevelPx = params.uBevelWidth;
    if (bevelPx <= 0.0)
        return float4(dye(sampleFlat(mapped, params.uShare)), coverage);

    // The lens is the pane with corners no tighter than its bevel is wide, a square pane's glass still rounds its
    // corners the way a drop of water does. In the tip of a square corner, outside that lens, the bend is at its most
    float lensRadius = min(max(radius, bevelPx), minExtent);
    float t = saturate(-roundedBoxDistance(local, extent, lensRadius) / bevelPx);
    float edge = 1.0 - t * t * (3.0 - 2.0 * t);
    if (edge <= 0.0)
        return float4(dye(sampleFlat(mapped, params.uShare)), coverage);

    // The same bend on every side: as far as the depth under the edge says, straight across the nearest edge
    float lip = pow(edge, 9.0);
    float offsetPx = edge * params.uRefraction + lip * edge * params.uBevelDepth;
    float2 dir = lensNormal(local, extent, lensRadius);
    float2 offset = dir * offsetPx * params.uTexel;
    float2 sampleUV = mirrored(mapped + offset);

    // The colours part along the bend, red short of it and blue past it. Mirrored places keep the bend's own way
    float2 chroma = mirrored(mapped + offset * (1.0 + params.uAberration)) - sampleUV;

    // How many pixels of what is behind one pixel of the rim takes in, across the edge: the bend grows faster than
    // the pane does there, so the rim shows a strip squeezed thin. Read at one place that was every other line of a
    // page of text and none of the ones between, a shimmer of stripes. It is read at as many places as it takes in
    float squeeze = 1.0 / mag + (params.uRefraction + 10.0 * lip * params.uBevelDepth) * 6.0 * t * (1.0 - t) / bevelPx;
    int taps = clamp(int(ceil(squeeze)), 1, 12);
    float3 colour = float3(0.0, 0.0, 0.0);
    float2 along = dir * params.uTexel * squeeze / float(taps);
    // Pair consecutive sub-texel taps at their midpoint via bilinear filtering, halving sample count while preserving the bevel average.
    float halfSpan = 0.5 * float(taps - 1);
    for (int i = 0; i + 1 < taps; i += 2)
        colour += 2.0 * sampleParted(mirrored(sampleUV + along * (float(i) + 0.5 - halfSpan)), chroma);
    if ((taps & 1) != 0)
        colour += sampleParted(mirrored(sampleUV + along * halfSpan), chroma);
    colour /= float(taps);

    return float4(dye(colour), coverage);
}
