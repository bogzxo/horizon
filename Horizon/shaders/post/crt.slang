// The look of a picture tube, soft dots along scan lines and glass that bulges, without a shadow mask. A scan line
// is two beams here. The narrow one is what draws the line, and what it leaves dark between itself and the next
// would cost the picture its brightness, so it is driven as hard as it can go without burning out, and whatever
// light is still missing after that goes into a wide beam that fills the gaps. Dark colours keep crisp lines,
// bright ones bloom into each other, and all of them come out as bright as they went in.
// After the CRT shader Timothy Lottes put in the public domain, in the version with the beams above. This does
// what that does for less. That one reads the picture 11 times for every pixel and turns each read from sRGB into
// linear light with a power a channel. This one reads it 7 times, the picture comes in already shrunk to the size
// of the tube in a texture that hands back linear light (see crt_shrink.slang), and a read that lands between two
// dots is the GPU blending the two by how close it is to each. How much light a beam gives over a whole line is
// worked out once a frame by the effect rather than a root for every pixel.

#include <common/screen.slang>

struct Params
{
    // How many dots a scan line has, and how many scan lines there are
    float2 uResolution;

    // How much the glass bulges, sideways and upwards. 0 is flat, an eighth is a goldfish bowl
    float2 uWarp;

    // How quickly the narrow beam falls off to either side of its line (-8 soft, -16 hard), the wide one that fills
    // the gaps (-2 to -4), and a dot to the dot next to it (-2 soft, -4 hard)
    float uHardScan;
    float uHardBloom;
    float uHardPix;

    // What the narrow beam comes to over the height of a line when it is 1 at its middle, and one over what the wide
    // beam comes to, see CrtEffect
    float uScanMean;
    float uBloomNorm;

    // How much of the light the wide beam may carry. 1 keeps everything as bright as it was, less keeps more of the
    // lines on what is white
    float uMaxBloom;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

// The picture at the size of the tube, in linear light.
BIND_TEXTURE(0) Sampler2D uSource;

float2 warp(float2 pos)
{
    pos = pos * 2.0 - 1.0;
    pos *= 1.0 + pos.yx * pos.yx * params.uWarp;
    return pos * 0.5 + 0.5;
}

float3 toSrgb(float3 colour)
{
    colour = max(colour, float3(0.0, 0.0, 0.0));
    return lerp(colour * 12.92, 1.055 * pow(colour, float3(1.0 / 2.4, 1.0 / 2.4, 1.0 / 2.4)) - 0.055, step(float3(0.0031308, 0.0031308, 0.0031308), colour));
}

// What one scan line gives a pixel, the narrow beam driven up to where its brightest colour would clip, and what
// light that still leaves missing put into the wide one.
float3 beam(float3 colour, float narrow, float wide)
{
    float brightest = max(max(colour.r, colour.g), max(colour.b, 1e-4));
    float drive = clamp((1.0 / brightest - 1.0) / (1.0 - params.uScanMean), 0.0, 1.0 / params.uScanMean);
    float fill = min(1.0 - drive * params.uScanMean, params.uMaxBloom);

    return colour * (drive * narrow + fill * wide);
}

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    float2 pos = warp(input.texCoords) * params.uResolution;

    // The dot we are in, and how far its middle is from us. The dots to either side are one and two further
    float2 cell = floor(pos);
    float2 away = 0.5 - (pos - cell);

    // How much of the dots of a line gets here. They share out what light there is, so they are brought to a sum
    // of one, over three dots for the lines above and below, over five for the one we are on
    float4 across = away.x + float4(-2.0, -1.0, 1.0, 2.0);
    float4 sides = exp2(params.uHardPix * across * across);
    float centre = exp2(params.uHardPix * away.x * away.x);

    float three = 1.0 / (sides.y + centre + sides.z);
    float five = 1.0 / (sides.x + sides.y + centre + sides.z + sides.w);

    // The two beams of the three nearest lines
    float3 down = away.y + float3(-1.0, 0.0, 1.0);
    float3 narrow = exp2(params.uHardScan * down * down);
    float3 wide = exp2(params.uHardBloom * down * down) * params.uBloomNorm;

    float2 texel = 1.0 / params.uResolution;
    float2 middle = (cell + 0.5) * texel;

    // Three dots in two reads, one between the first and the middle one, one between the middle one and the last,
    // the middle one going half to each
    float2 halves = float2(sides.y, sides.z) + centre * 0.5;
    float2 halfAt = float2(-1.0 + centre * 0.5 / halves.x, sides.z / halves.y) * texel.x;

    // Five dots in three, the outer two of either side in one read each, the middle one by itself
    float2 outer = sides.xz + sides.yw;
    float2 outerAt = float2(-2.0 + sides.y / outer.x, 1.0 + sides.w / outer.y) * texel.x;

    float above = middle.y - texel.y, below = middle.y + texel.y;

    float3 lineA = (
        uSource.Sample(float2(middle.x + halfAt.x, above)).rgb * halves.x +
        uSource.Sample(float2(middle.x + halfAt.y, above)).rgb * halves.y) * three;

    float3 lineB = (
        uSource.Sample(float2(middle.x + outerAt.x, middle.y)).rgb * outer.x +
        uSource.Sample(middle).rgb * centre +
        uSource.Sample(float2(middle.x + outerAt.y, middle.y)).rgb * outer.y) * five;

    float3 lineC = (
        uSource.Sample(float2(middle.x + halfAt.x, below)).rgb * halves.x +
        uSource.Sample(float2(middle.x + halfAt.y, below)).rgb * halves.y) * three;

    float3 colour =
        beam(lineA, narrow.x, wide.x) +
        beam(lineB, narrow.y, wide.y) +
        beam(lineC, narrow.z, wide.z);

    return float4(toSrgb(colour), 1.0);
}
