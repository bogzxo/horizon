// The primitive renderer. Every shape is a signed distance field drawn as an instance of the one quad, how far a
// fragment is from the edge of the shape, negative inside. A filled shape is everything at a negative distance, an
// outline is everything within half the thickness of the edge, and the edge itself is smoothed over a pixel either
// way, whatever size the shape is drawn at. So a circle is a circle and not a polygon, and a line is as thin as it
// is asked to be.

#include <common/camera.slang>
#include <common/gbuffer.slang>
#include <common/quad.slang>

struct Params
{
    // What the whole renderer is moved by
    float4x4 uModel;

    // How much of the shapes shows whatever the light is (debug drawings want to be seen in the dark, which is 1)
    float uEmissive;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

// Must match ShapeInstance.cs (64 bytes). What the fields mean depends on the kind, see ShapeKind.
//   center    the middle of a rectangle, circle or triangle, one end of a segment
//   size      half the width and height of a rectangle or triangle, the radius (twice) of a circle, the other end of a segment
//   rotation  in radians, anticlockwise around the middle. A segment has none
//   thickness 0 for a filled shape, the width of the line for an outline or a segment
//   rounding  how far the corners of a rectangle are rounded
struct Shape
{
    float2 center;
    float2 size;
    float rotation;
    float thickness;
    float rounding;
    float depth;
    uint color;
    uint kind;
    float2 padding0;
    float2 padding1;
    float2 padding2;
};

// Must match ShapeKind.
static const uint KIND_RECTANGLE = 0u;
static const uint KIND_CIRCLE = 1u;
static const uint KIND_SEGMENT = 2u;
static const uint KIND_TRIANGLE = 3u;
static const uint KIND_MASK = 0xFFu;

// The shapes of this frame, bound as the range of the stream buffer the frame is in, see StreamBuffer.BindRange.
BIND_BUFFER(3) StructuredBuffer<Shape> shapes;

struct Varyings
{
    float4 position : SV_Position;
    float2 local : LOCALPOS;
    float4 color : COLOR;
    nointerpolation float2 halfSize : HALFSIZE;
    nointerpolation float thickness : THICKNESS;
    nointerpolation float rounding : ROUNDING;
    nointerpolation uint kind : KIND;
};

[shader("vertex")]
Varyings vsMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID, uint baseInstance : SV_StartInstanceLocation)
{
    Shape shape = shapes[quadInstance(instanceId, baseInstance)];
    float2 quad = QUAD_POSITIONS[quadCorner(vertexId)];
    uint kind = shape.kind & KIND_MASK;

    // The edge of a shape is smoothed over about a pixel, so the quad reaches a little past it. A pixel of the world
    // at the scale things are drawn at, which uProjectionScale (halves of the screen a unit) and the viewport give
    float pixel = 2.0 / max(camera.uViewportSize.x * abs(camera.uProjectionScale.x), 0.0001);

    float2 center = shape.center;
    float2 halfSize = shape.size;
    float rotation = shape.rotation;

    if (kind == KIND_SEGMENT)
    {
        // A segment is a box as long as the line and as wide as it is thick, turned to lie along it, with round caps
        // that are the thickness over its ends
        float2 along = shape.size - shape.center;
        center = (shape.center + shape.size) * 0.5;
        halfSize = float2(length(along) * 0.5, shape.thickness * 0.5);
        rotation = atan2(along.y, along.x);
    }

    // A line is drawn either side of the edge, so an outline reaches half its thickness out
    float reach = (kind == KIND_SEGMENT ? shape.thickness * 0.5 : (shape.thickness > 0.0 ? shape.thickness * 0.5 : 0.0)) + pixel * 1.5;
    float2 extent = halfSize + float2(reach, reach);

    float2 local = quad * 2.0 * extent;
    float c = cos(rotation), s = sin(rotation);
    float2 turned = float2(local.x * c - local.y * s, local.x * s + local.y * c);

    float4 world = mul(float4(center + turned, shape.depth, 1.0), params.uModel);

    Varyings output;
    output.position = toClip(world);
    output.local = local;
    output.color = unpackColor(shape.color);
    output.halfSize = halfSize;
    output.thickness = shape.thickness;
    output.rounding = shape.rounding;
    output.kind = kind;
    return output;
}

float rectangle(float2 at, float2 extent, float rounded)
{
    rounded = min(rounded, min(extent.x, extent.y));
    float2 d = abs(at) - extent + float2(rounded, rounded);
    return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - rounded;
}

float circle(float2 at, float radius)
{
    return length(at) - radius;
}

// A box with round caps, the segment itself lies along X from -extent.x to extent.x, and the caps are the thickness
float capsule(float2 at, float2 extent)
{
    at.x = max(abs(at.x) - extent.x, 0.0);
    return length(at) - extent.y;
}

// An isosceles triangle standing on its base, its tip at the top, the shape the old renderer drew, kept so a
// rotation of 0 still points up
float triangle(float2 at, float2 extent)
{
    float2 a = float2(-extent.x, -extent.y);
    float2 b = float2(extent.x, -extent.y);
    float2 c = float2(0.0, extent.y);

    float2 e0 = b - a, e1 = c - b, e2 = a - c;
    float2 v0 = at - a, v1 = at - b, v2 = at - c;

    float2 pq0 = v0 - e0 * clamp(dot(v0, e0) / dot(e0, e0), 0.0, 1.0);
    float2 pq1 = v1 - e1 * clamp(dot(v1, e1) / dot(e1, e1), 0.0, 1.0);
    float2 pq2 = v2 - e2 * clamp(dot(v2, e2) / dot(e2, e2), 0.0, 1.0);

    float s = sign(e0.x * e2.y - e0.y * e2.x);
    float2 d = min(min(float2(dot(pq0, pq0), s * (v0.x * e0.y - v0.y * e0.x)),
                       float2(dot(pq1, pq1), s * (v1.x * e1.y - v1.y * e1.x))),
                       float2(dot(pq2, pq2), s * (v2.x * e2.y - v2.y * e2.x)));
    return -sqrt(d.x) * sign(d.y);
}

[shader("fragment")]
GBuffer fsMain(Varyings input)
{
    float distance;
    if (input.kind == KIND_CIRCLE) distance = circle(input.local, input.halfSize.x);
    else if (input.kind == KIND_SEGMENT) distance = capsule(input.local, input.halfSize);
    else if (input.kind == KIND_TRIANGLE) distance = triangle(input.local, input.halfSize);
    else distance = rectangle(input.local, input.halfSize, input.rounding);

    // An outline is a band around the edge. A segment is a shape of its own, its thickness is its width
    if (input.thickness > 0.0 && input.kind != KIND_SEGMENT) distance = abs(distance) - input.thickness * 0.5;

    // Smoothed over about a pixel of the screen, however big the shape is on it
    float pixel = max(fwidth(distance), 0.00001);
    float coverage = clamp(0.5 - distance / pixel, 0.0, 1.0);

    float4 result = float4(input.color.rgb, input.color.a * coverage);
    if (result.a <= 0.0) discard;

    return writeFlat(result, params.uEmissive);
}
