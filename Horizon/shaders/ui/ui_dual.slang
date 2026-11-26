// Single-attachment dual-source UI shader, paired with BlendMode.DualSource when the card supports dualSrcBlend.
// Keeps ui.slang intact for 3-MRT DeferredRenderer2D and non-dualSrcBlend fallback.

#include <common/camera.slang>
#include <common/quad.slang>

struct Params
{
    // What the whole batch is moved by
    float4x4 uModel;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

// Must match SpriteItem.cs and ui.slang.
struct SpriteItem
{
    float2 origin;
    float2 axisX;
    float2 axisY;
    float2 texMin;
    float2 texMax;
    float2 padding;
    uint color;
    uint flags;
    float depth;
    float ring;
};

BIND_BUFFER(3) StructuredBuffer<SpriteItem> items;

BINDLESS_TEXTURES(uTextures);

static const uint TEXTURE_MASK = 0xFFFFu;
static const uint COVERAGE_FLAG = 0x10000u;
static const uint SMOOTH_FLAG = 0x20000u;
static const uint CORNER_FLAG = 0x40000u;
static const uint FIELD_FLAG = 0x200000u;
static const uint BOLD_FLAG = 0x800000u;
static const uint SHAPE_FLAG = 0x2000000u;
static const uint GLYPH_FLAG = 0x4000000u;
static const uint SUBPIXEL_ERASE_FLAG = 0x8000000u;
static const uint SUBPIXEL_INK_FLAG = 0x10000000u;

static const float GLYPH_CONTRAST = 0.5;
static const float GLYPH_GAMMA_DARK = 1.15;
static const float GLYPH_GAMMA_LIGHT = 0.8;
static const float BOLD_GROWTH = 0.2;

float glyphInk(float coverage, float gamma)
{
    float lifted = coverage * (GLYPH_CONTRAST + 1.0) / (coverage * GLYPH_CONTRAST + 1.0);
    return pow(saturate(lifted), gamma);
}

float3 glyphInk3(float3 coverage, float gamma)
{
    float3 lifted = coverage * (GLYPH_CONTRAST + 1.0) / (coverage * GLYPH_CONTRAST + 1.0);
    return pow(saturate(lifted), float3(gamma, gamma, gamma));
}

struct Varyings
{
    float4 position : SV_Position;
    float2 texel : TEXEL;
    float4 color : COLOR;
    nointerpolation uint flags : FLAGS;
    nointerpolation float ring : RING;
    nointerpolation float2 texelSize : TEXELSIZE;
    nointerpolation float2 shape : SHAPE;
};

struct DualOutput
{
    [[vk::location(0), vk::index(0)]] float4 color : SV_Target0;
    [[vk::location(0), vk::index(1)]] float4 blend : SV_Target1;
};

DualOutput premultipliedOutput(float4 c)
{
    DualOutput output;
    output.color = float4(c.rgb * c.a, c.a);
    output.blend = float4(c.a, c.a, c.a, c.a);
    return output;
}

DualOutput subpixelOutput(float3 inkRgb, float3 eraseRgb)
{
    DualOutput output;
    output.color = float4(inkRgb, 0.0);
    output.blend = float4(eraseRgb, 0.0);
    return output;
}

[shader("vertex")]
Varyings vsMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID, uint baseInstance : SV_StartInstanceLocation)
{
    SpriteItem item = items[quadInstance(instanceId, baseInstance)];
    uint cornerIndex = quadCorner(vertexId);

    float2 corner = QUAD_POSITIONS[cornerIndex] + 0.5;

    Varyings output;
    output.texel = lerp(item.texMin, item.texMax, float2(corner.x, 1.0 - corner.y));
    output.color = unpackColor(item.color);
    output.flags = item.flags;
    output.ring = item.ring;

    uint texture = item.flags & TEXTURE_MASK;
    output.texelSize = float2(0.0, 0.0);
    if (texture != NO_TEXTURE)
    {
        uint width, height;
        uTextures[NonUniformResourceIndex(texture)].GetDimensions(width, height);
        output.texelSize = 1.0 / float2(max(width, 1u), max(height, 1u));
    }

    float2 at = item.origin + item.axisX * corner.x + item.axisY * corner.y;
    output.shape = float2(0.0, 0.0);
    if ((item.flags & SHAPE_FLAG) != 0u)
    {
        float2 middle = item.origin + (item.axisX + item.axisY) * 0.5;
        at = clamp(at, item.padding, item.texMax);
        output.texel = at - middle;
        output.texelSize = float2(length(item.axisX), length(item.axisY)) * 0.5;
        output.shape = item.texMin;
    }
    else if ((item.flags & (GLYPH_FLAG | SUBPIXEL_ERASE_FLAG | SUBPIXEL_INK_FLAG)) != 0u)
    {
        float light = dot(output.color.rgb, float3(0.2126, 0.7152, 0.0722));
        output.shape.x = lerp(GLYPH_GAMMA_DARK, GLYPH_GAMMA_LIGHT, light);
    }

    float4 world = mul(float4(at, item.depth, 1.0), params.uModel);
    output.position = toClip(world);
    return output;
}

float4 smoothTexel(Sampler2D tex, float2 at, float2 box)
{
    float2 corner = at - 0.5 * box;
    float2 blend = clamp((frac(corner) - (1.0 - box)) / box, 0.0, 1.0);

    uint width, height;
    tex.GetDimensions(width, height);
    int2 first = int2(floor(corner));
    int2 last = int2(width, height) - 1;

    float4 a = tex.Load(int3(clamp(first, int2(0, 0), last), 0));
    float4 b = tex.Load(int3(clamp(first + int2(1, 0), int2(0, 0), last), 0));
    float4 c = tex.Load(int3(clamp(first + int2(0, 1), int2(0, 0), last), 0));
    float4 d = tex.Load(int3(clamp(first + int2(1, 1), int2(0, 0), last), 0));

    a.rgb *= a.a;
    b.rgb *= b.a;
    c.rgb *= c.a;
    d.rgb *= d.a;

    float4 result = lerp(lerp(a, b, blend.x), lerp(c, d, blend.x), blend.y);
    if (result.a > 0.0) result.rgb /= result.a;
    return result;
}

float roundBox(float2 p, float2 half, float radius)
{
    float2 q = abs(p) - half + radius;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
}

[shader("fragment")]
DualOutput fsMain(Varyings input)
{
    if ((input.flags & SHAPE_FLAG) != 0u)
    {
        float4 tex = float4(1.0, 1.0, 1.0, 1.0);
        float2 local = input.texel, half = input.texelSize;
        if (input.shape.y > 0.0)
        {
            float reach = max(input.ring, 0.0001);
            float2 inner = max(half - reach, 0.0);
            float t = saturate(1.0 - length(max(abs(local) - inner, 0.0)) / reach);
            tex.a = t * t * (3.0 - 2.0 * t);
        }
        else
        {
            float outside = roundBox(local, half, input.ring);
            tex.a = saturate(0.5 - outside);
            float thickness = input.shape.x;
            if (thickness > 0.0)
            {
                float hole = roundBox(local, max(half - thickness, 0.0), max(input.ring - thickness, 0.0));
                tex.a *= saturate(0.5 + hole);
            }
        }
        float4 result = tex * input.color;
        if (result.a <= 0.0) discard;
        return premultipliedOutput(result);
    }

    uint texture = input.flags & TEXTURE_MASK;

    if (texture == NO_TEXTURE && (input.flags & (FIELD_FLAG | CORNER_FLAG)) == 0u)
    {
        if (input.color.a <= 0.0) discard;
        return premultipliedOutput(input.color);
    }

    if (texture != NO_TEXTURE
        && (input.flags & (GLYPH_FLAG | SUBPIXEL_ERASE_FLAG | SUBPIXEL_INK_FLAG)) != 0u
        && (input.flags & (SMOOTH_FLAG | FIELD_FLAG | CORNER_FLAG)) == 0u)
    {
        float2 uv = input.texel * input.texelSize;
        float4 tex = uTextures[NonUniformResourceIndex(texture)].SampleLevel(uv, 0.0);
        if ((input.flags & (SUBPIXEL_ERASE_FLAG | SUBPIXEL_INK_FLAG)) != 0u)
        {
            float3 eraseRgb = glyphInk3(tex.rgb, input.shape.x) * input.color.a;
            if (max(eraseRgb.r, max(eraseRgb.g, eraseRgb.b)) <= 0.0) discard;
            float3 inkRgb = (input.flags & SUBPIXEL_INK_FLAG) != 0u ? input.color.rgb * eraseRgb : float3(0.0, 0.0, 0.0);
            return subpixelOutput(inkRgb, eraseRgb);
        }

        float4 result = float4(input.color.rgb, glyphInk(tex.r, input.shape.x) * input.color.a);
        if (result.a <= 0.0) discard;
        return premultipliedOutput(result);
    }

    float2 box = clamp(fwidth(input.texel), float2(0.00001, 0.00001), float2(1.0, 1.0));
    float fromMiddle = length(input.texel);
    float cornerPixel = max(fwidth(fromMiddle), 0.00001);
    float2 uv = input.texel * input.texelSize;
    float2 uvAcross = ddx(uv), uvDown = ddy(uv);
    float texelsAPixel = max(length(ddx(input.texel)), length(ddy(input.texel)));

    float4 tex = float4(1.0, 1.0, 1.0, 1.0);
    if (texture != NO_TEXTURE && ((input.flags & FIELD_FLAG) == 0u || input.ring <= 0.0))
    {
        tex = (input.flags & SMOOTH_FLAG) != 0u && (input.flags & FIELD_FLAG) == 0u
            ? smoothTexel(uTextures[NonUniformResourceIndex(texture)], input.texel, box)
            : uTextures[NonUniformResourceIndex(texture)].SampleGrad(uv, uvAcross, uvDown);
    }

    if ((input.flags & (SUBPIXEL_ERASE_FLAG | SUBPIXEL_INK_FLAG)) != 0u)
    {
        float3 eraseRgb = glyphInk3(tex.rgb, input.shape.x) * input.color.a;
        if (max(eraseRgb.r, max(eraseRgb.g, eraseRgb.b)) <= 0.0) discard;
        float3 inkRgb = (input.flags & SUBPIXEL_INK_FLAG) != 0u ? input.color.rgb * eraseRgb : float3(0.0, 0.0, 0.0);
        return subpixelOutput(inkRgb, eraseRgb);
    }

    if ((input.flags & COVERAGE_FLAG) != 0u) tex = float4(1.0, 1.0, 1.0, tex.a);
    if ((input.flags & GLYPH_FLAG) != 0u) tex = float4(1.0, 1.0, 1.0, glyphInk(tex.r, input.shape.x));

    float field = tex.r;
    float fieldPixel = max(fwidth(field), 0.0001);
    if ((input.flags & FIELD_FLAG) != 0u)
    {
        float pixel = input.ring > 0.0 ? max(input.ring * texelsAPixel, 0.0001) : fieldPixel;
        float edge = pixel * 0.5;
        float middle = (input.flags & BOLD_FLAG) != 0u ? 0.5 - 0.5 * BOLD_GROWTH : 0.5;

        float ink = 0.0;
        ink += clamp((uTextures[NonUniformResourceIndex(texture)].SampleLevel(uv + uvAcross * 0.125 + uvDown * 0.375, 0.0).r - middle) / edge + 0.5, 0.0, 1.0);
        ink += clamp((uTextures[NonUniformResourceIndex(texture)].SampleLevel(uv - uvAcross * 0.375 + uvDown * 0.125, 0.0).r - middle) / edge + 0.5, 0.0, 1.0);
        ink += clamp((uTextures[NonUniformResourceIndex(texture)].SampleLevel(uv + uvAcross * 0.375 - uvDown * 0.125, 0.0).r - middle) / edge + 0.5, 0.0, 1.0);
        ink += clamp((uTextures[NonUniformResourceIndex(texture)].SampleLevel(uv - uvAcross * 0.125 - uvDown * 0.375, 0.0).r - middle) / edge + 0.5, 0.0, 1.0);

        tex = float4(1.0, 1.0, 1.0, ink * 0.25);
    }

    if ((input.flags & CORNER_FLAG) != 0u)
    {
        float inside = clamp((1.0 - fromMiddle) / cornerPixel + 0.5, 0.0, 1.0);
        float pastHole = input.ring < 1.0 ? clamp((fromMiddle - (1.0 - input.ring)) / cornerPixel + 0.5, 0.0, 1.0) : 1.0;
        tex.a *= inside * pastHole;
    }

    float4 result = tex * input.color;
    if (result.a <= 0.0) discard;
    return premultipliedOutput(result);
}
