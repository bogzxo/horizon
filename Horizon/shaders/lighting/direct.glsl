// The lights of a DeferredRenderer2D and what blocks them, shared by everything that lights a point of the world
// directly, the deferred pass that puts the picture on screen and the scene pass of the path traced lighting.
// DeferredRenderer2D.BindLighting sets all of this.

#include <common/camera.glsl>

// Must match DeferredRenderer2D.LightData (48 bytes).
struct Light {
  vec2 position;
  float radius;
  float intensity;
  vec3 color;
  float height;
  float glow;
  float castsShadows;
  float size;
  float padding;
};

layout(std430, binding = 0) readonly buffer LightBuffer {
  Light lights[];
};

uniform int uLightCount;

// What blocks the lights, a texel for every cell of a grid over the world, see OcclusionMap2D.
layout(binding = 3) uniform sampler2D uTexOcclusion;
uniform vec2 uOcclusionOrigin;
uniform vec2 uOcclusionCellSize;
uniform vec2 uOcclusionSize;
uniform bool uShadows;

// How tight the highlights on what is shiny are, and how bright.
uniform float uShininess;
uniform float uSpecularIntensity;

// The furthest (in cells) a ray is followed towards a light, further than that nothing casts a shadow.
#define MAX_SHADOW_CELLS 96

bool isSolid(ivec2 cell) {
  if (any(lessThan(cell, ivec2(0))) || any(greaterThanEqual(cell, ivec2(uOcclusionSize)))) return false;

  return texelFetch(uTexOcclusion, cell, 0).r > 0.5;
}

// Which cell of the grid a point of the world is in.
ivec2 cellOf(vec2 position) {
  return ivec2(floor((position - uOcclusionOrigin) / uOcclusionCellSize));
}

// How much of a light gets to a point, going from cell to cell of the grid between the two. every cell that the way there
// crosses is looked at, so nothing is skipped over and the edge of a shadow is exactly the edge of what casts it.
float visibility(vec2 from, vec2 to) {
  vec2 start = (from - uOcclusionOrigin) / uOcclusionCellSize;
  vec2 end = (to - uOcclusionOrigin) / uOcclusionCellSize;

  ivec2 cell = ivec2(floor(start));
  ivec2 last = ivec2(floor(end));

  vec2 way = end - start;
  ivec2 stride = ivec2(sign(way));

  // How far along the way the next border of a cell is on each axis, and how far it is from one border to the next.
  // An axis that isn't moved along never gets its turn.
  vec2 perCell = mix(abs(1.0 / way), vec2(1e30), equal(way, vec2(0.0)));
  vec2 next = mix((vec2(cell) + max(vec2(stride), 0.0) - start) / way, vec2(1e30), equal(way, vec2(0.0)));

  // Something solid is lit as what it is, a face turned towards us, not the inside of a wall.
  // What it is a part of is looked past until the way has left it.
  bool leaving = isSolid(cell);

  for (int i = 0; i < MAX_SHADOW_CELLS; i++) {
    if (cell == last) return 1.0;

    if (next.x < next.y) {
      cell.x += stride.x;
      next.x += perCell.x;
    } else {
      cell.y += stride.y;
      next.y += perCell.y;
    }

    // The cell the light itself is in never blocks it, or a torch on a wall would light nothing.
    if (cell == last) return 1.0;

    bool solid = isSolid(cell);
    if (leaving) {
      leaving = solid;
      continue;
    }

    if (solid) return 0.0;
  }

  return 1.0;
}

// How much of a light gets to a point. A light with a size isn't hidden all at once, the way to it is tried for a few
// spots across it, what only some of them can be seen from is in the half shadow at the edge.
float shadow(vec2 from, vec2 to, float size) {
  if (size <= 0.0) return visibility(from, to);

  vec2 way = to - from;
  float distance = length(way);
  if (distance < 0.001) return 1.0;

  // Across the way to the light is where its size shows, along it nothing changes.
  vec2 across = vec2(-way.y, way.x) * (size / distance);

  return (visibility(from, to - across) +
          visibility(from, to - across / 3.0) +
          visibility(from, to + across / 3.0) +
          visibility(from, to + across)) * 0.25;
}

// The light every light throws straight onto a point of the world. What is shiny mirrors the lights (highlights,
// in the light's own colour) and what hangs in the air around a light (glow) come out separately, they aren't
// multiplied by the surface.
vec3 directLight(vec2 position, vec3 normal, bool hasNormal, float shine, out vec3 highlights, out vec3 glow) {
  vec3 light = vec3(0.0);
  highlights = vec3(0.0);
  glow = vec3(0.0);

  for (int i = 0; i < uLightCount; i++) {
    vec2 toLight = lights[i].position - position;
    float distanceSquared = dot(toLight, toLight);
    float radiusSquared = lights[i].radius * lights[i].radius;

    if (distanceSquared >= radiusSquared) continue;

    // Brightest in the middle, fading smoothly to nothing at the edge.
    float falloff = 1.0 - distanceSquared / radiusSquared;
    falloff *= falloff;

    float reach = lights[i].intensity * falloff;
    if (uShadows && lights[i].castsShadows > 0.5) {
      reach *= shadow(position, lights[i].position, lights[i].size);
      if (reach <= 0.0) continue;
    }

    vec3 direction = normalize(vec3(toLight, lights[i].height));

    // A surface turned towards the light catches more of it than a flat one, one turned away less.
    float facing = hasNormal ? clamp(dot(normal, direction) / max(direction.z, 0.05), 0.0, 2.0) : 1.0;

    light += lights[i].color * (reach * facing);
    glow += lights[i].color * (reach * falloff * lights[i].glow);

    if (shine > 0.0) {
      // What is shiny mirrors the light itself. We are looking straight at the world, so it shows wherever the
      // surface is turned halfway between us and the light.
      vec3 halfway = normalize(direction + vec3(0.0, 0.0, 1.0));
      highlights += lights[i].color * (reach * shine * pow(max(dot(normal, halfway), 0.0), uShininess));
    }
  }

  return light;
}

// The normal a surface of the G-buffer has, flat towards the viewer unless there is a normal map saying otherwise.
vec3 surfaceNormal(vec4 surface, out bool hasNormal) {
  vec3 normal = vec3(surface.rg * 2.0 - 1.0, 0.0);
  hasNormal = dot(normal.xy, normal.xy) > 0.0025;
  normal.z = sqrt(max(1.0 - dot(normal.xy, normal.xy), 0.0));
  return normal;
}
