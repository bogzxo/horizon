// The sprite renderer. Every item is a quad out of the frame's storage buffer, drawn as an instance of the one quad
// (see common/quad.slang), showing part of whichever texture it names out of the bindless table. However many
// textures the items of a frame show between them, they are one draw.

#include <common/camera.slang>
#include <common/gbuffer.slang>
#include <common/quad.slang>

struct Params
{
    // What the whole batch is moved by
    float4x4 uModel;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

// Must match SpriteItem.cs.
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

// The items of this frame, bound as the range of the stream buffer the frame is in (see StreamBuffer.BindRange), so
// the first item of the frame is items[0]. A draw that starts partway through them says so with its base instance.
BIND_BUFFER(3) StructuredBuffer<SpriteItem> items;

BINDLESS_TEXTURES(uTextures);

// Must match SpriteItem.cs. The texture is the low sixteen bits of the flags, its slot in the bindless table.
static const uint TEXTURE_MASK = 0xFFFFu;
static const uint COVERAGE_FLAG = 0x10000u;
static const uint SMOOTH_FLAG = 0x20000u;
static const uint CORNER_FLAG = 0x40000u;
static const uint FLASH_FLAG = 0x80000u;
static const uint SHADOW_FLAG = 0x100000u;
static const uint FIELD_FLAG = 0x200000u;
static const uint LAMP_FLAG = 0x400000u;

struct Varyings
{
    float4 position : SV_Position;
    float2 texel : TEXEL;
    float2 fragPos : FRAGPOS;
    float4 color : COLOR;
    nointerpolation uint flags : FLAGS;
    nointerpolation float ring : RING;
    nointerpolation float2 texelSize : TEXELSIZE;
};

[shader("vertex")]
Varyings vsMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID, uint baseInstance : SV_StartInstanceLocation)
{
    SpriteItem item = items[quadInstance(instanceId, baseInstance)];
    uint cornerIndex = quadCorner(vertexId);

    // The quad goes from -0.5 to 0.5, the item is described from its bottom left corner
    float2 corner = QUAD_POSITIONS[cornerIndex] + 0.5;

    Varyings output;

    // texMin is what the top left corner shows, so V runs against Y. Left in texels, the fragment shader turns them
    // into texture coordinates with the size it is handed here
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

    float4 world = mul(float4(item.origin + item.axisX * corner.x + item.axisY * corner.y, item.depth, 1.0), params.uModel);
    output.position = toClip(world);
    output.fragPos = world.xy;
    return output;
}

// What a spot of a texture looks like when its texels are drawn as sharp squares, one flat colour inside of a
// texel, blended with its neighbour only in the one screen pixel their edge runs through (box is how many texels
// a screen pixel covers). Pixel art drawn at a scale that isn't a whole number keeps its hard edges this way
// without some of its pixels coming out wider than others. At a whole scale it is exactly what a nearest
// filter gives.
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

    // Blended by how much of each there is, or the colour of see-through texels would darken the edges
    a.rgb *= a.a;
    b.rgb *= b.a;
    c.rgb *= c.a;
    d.rgb *= d.a;

    float4 result = lerp(lerp(a, b, blend.x), lerp(c, d, blend.x), blend.y);
    if (result.a > 0.0) result.rgb /= result.a;
    return result;
}

[shader("fragment")]
GBuffer fsMain(Varyings input)
{
    uint texture = input.flags & TEXTURE_MASK;

    // How many texels a screen pixel covers, and for the corner of a rounded box, where the texel is how far from
    // the middle of the disc this is (1 is its edge) and how much of that a screen pixel is. Worked out up front,
    // outside of any branch, so the derivatives are always well defined
    float2 box = clamp(fwidth(input.texel), float2(0.00001, 0.00001), float2(1.0, 1.0));
    float fromMiddle = length(input.texel);
    float cornerPixel = max(fwidth(fromMiddle), 0.00001);
    float2 uv = input.texel * input.texelSize;

    // A distance field is read between its texels like any smooth picture, the pixel art filter below keeps the
    // texels hard and at a big size that made a staircase of every edge
    float4 tex = float4(1.0, 1.0, 1.0, 1.0);
    if (texture != NO_TEXTURE)
    {
        tex = (input.flags & SMOOTH_FLAG) != 0u && (input.flags & FIELD_FLAG) == 0u
            ? smoothTexel(uTextures[NonUniformResourceIndex(texture)], input.texel, box)
            : uTextures[NonUniformResourceIndex(texture)].SampleGrad(uv, ddx(uv), ddy(uv));
    }

    // The texture only says where the ink is (fonts), the colour is all the item's
    if ((input.flags & COVERAGE_FLAG) != 0u) tex = float4(1.0, 1.0, 1.0, tex.a);

    // Or how far from the ink's edge every texel is (text out of a distance field), the edge is wherever the
    // field crosses the middle, smoothed over the width of a screen pixel so it is crisp big and small alike.
    // The width is worked out outside of the branch, derivatives want every lane along
    float field = tex.r;
    float fieldPixel = max(fwidth(field), 0.0001);
    if ((input.flags & FIELD_FLAG) != 0u)
    {
        float ink = clamp((field - 0.5) / fieldPixel + 0.5, 0.0, 1.0);
        tex = float4(1.0, 1.0, 1.0, ink);
    }

    // A quarter of a disc. Inside of its edge, and (for the corner of an outline) outside of the hole in it
    if ((input.flags & CORNER_FLAG) != 0u)
    {
        float inside = clamp((1.0 - fromMiddle) / cornerPixel + 0.5, 0.0, 1.0);
        float pastHole = input.ring < 1.0 ? clamp((fromMiddle - (1.0 - input.ring)) / cornerPixel + 0.5, 0.0, 1.0) : 1.0;
        tex.a *= inside * pastHole;
    }

    float4 result = tex * input.color;
    float glow = 0.0;

    // Flashed. The colour is painted over the sprite instead of multiplied into it, and it shows whatever the light is
    if ((input.flags & FLASH_FLAG) != 0u)
    {
        result = float4(lerp(tex.rgb, input.color.rgb, input.ring), tex.a * input.color.a);
        glow = input.ring;
    }

    if (result.a <= 0.0) discard;

    // Sprites have no normal map, are lit like everything else (unless they are flashed) and aren't shiny
    GBuffer output = writeFlat(result, glow);

    // One that blocks light leaves its shape in the material for the lights to go by, see SpriteShadows
    // What blocks light says how much of the pixel it covers, and never less than wide open to the ambient light
    output.material.g = (input.flags & SHADOW_FLAG) != 0u ? max(result.a, packOcclusion(1.0)) : packOcclusion(1.0);

    // A flash is for looks, it shows bright whatever the light is but lights nothing round it, unless the sprite
    // says it is a lamp (Sprite.FlashLights). A fighter flashing white on every hit would otherwise light the
    // whole arena up with every punch in the path traced mode
    output.material.b = (input.flags & LAMP_FLAG) != 0u ? glow : 0.0;
    return output;
}
