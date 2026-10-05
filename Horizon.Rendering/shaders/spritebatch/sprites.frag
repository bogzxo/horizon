#version 410 core

// Albedo, surface and material are the attachments of a DeferredRenderer2D, see its summary for what goes where.
// Drawn straight to the window only the first of them goes anywhere.
layout(location = 0) out vec4 AlbedoColor;
layout(location = 1) out vec4 SurfaceColor;
layout(location = 2) out vec4 MaterialColor;

layout(location = 0) in vec2 texel;
layout(location = 1) in vec2 fragPos;
layout(location = 2) in vec4 color;
layout(location = 3) flat in uint flags;

// Must match SpriteItem.cs.
const uint NO_TEXTURE = 0xFFu;
const uint COVERAGE_FLAG = 0x100u;
const uint SMOOTH_FLAG = 0x200u;

// Every item says which of these it shows, so things with different textures can be drawn in order in one call.
uniform sampler2D uTextures[4];

// One over the size of the texture in every slot, items say what they show in texels.
uniform vec2 uTexelSizes[4];

// What a spot of a texture looks like when its texels are drawn as sharp squares: one flat colour inside of a
// texel, blended with its neighbour only in the one screen pixel their edge runs through (box is how many texels
// a screen pixel covers). Pixel art drawn at a scale that isn't a whole number keeps its hard edges this way
// without some of its pixels coming out wider than others. At a whole scale it is exactly what a nearest
// filter gives.
vec4 smoothTexel(sampler2D tex, vec2 at, vec2 box) {
  vec2 corner = at - 0.5 * box;
  vec2 blend = clamp((fract(corner) - (1.0 - box)) / box, 0.0, 1.0);

  ivec2 first = ivec2(floor(corner));
  ivec2 last = textureSize(tex, 0) - 1;

  vec4 a = texelFetch(tex, clamp(first, ivec2(0), last), 0);
  vec4 b = texelFetch(tex, clamp(first + ivec2(1, 0), ivec2(0), last), 0);
  vec4 c = texelFetch(tex, clamp(first + ivec2(0, 1), ivec2(0), last), 0);
  vec4 d = texelFetch(tex, clamp(first + ivec2(1, 1), ivec2(0), last), 0);

  // Blended by how much of each there is, or the colour of see-through texels would darken the edges.
  a.rgb *= a.a;
  b.rgb *= b.a;
  c.rgb *= c.a;
  d.rgb *= d.a;

  vec4 result = mix(mix(a, b, blend.x), mix(c, d, blend.x), blend.y);
  if (result.a > 0.0) result.rgb /= result.a;
  return result;
}

void main() {
  // Sampled up front, outside of any branch, so the mip level is always well defined.
  vec4 slot0 = texture(uTextures[0], texel * uTexelSizes[0]);
  vec4 slot1 = texture(uTextures[1], texel * uTexelSizes[1]);
  vec4 slot2 = texture(uTextures[2], texel * uTexelSizes[2]);
  vec4 slot3 = texture(uTextures[3], texel * uTexelSizes[3]);

  // How many texels a screen pixel covers. Worked out here for the same reason.
  vec2 box = clamp(fwidth(texel), vec2(0.00001), vec2(1.0));

  uint slot = flags & 0xFFu;
  vec4 tex = vec4(1.0);

  if ((flags & SMOOTH_FLAG) != 0u) {
    if (slot == 0u) tex = smoothTexel(uTextures[0], texel, box);
    else if (slot == 1u) tex = smoothTexel(uTextures[1], texel, box);
    else if (slot == 2u) tex = smoothTexel(uTextures[2], texel, box);
    else if (slot == 3u) tex = smoothTexel(uTextures[3], texel, box);
  }
  else if (slot == 0u) tex = slot0;
  else if (slot == 1u) tex = slot1;
  else if (slot == 2u) tex = slot2;
  else if (slot == 3u) tex = slot3;

  // The texture only says where the ink is (fonts), the colour is all the item's.
  if ((flags & COVERAGE_FLAG) != 0u) tex = vec4(1.0, 1.0, 1.0, tex.a);

  vec4 result = tex * color;
  if (result.a <= 0.0) discard;
  AlbedoColor = result;

  // Sprites have no normal map, are lit like everything else and aren't shiny.
  SurfaceColor = vec4(0.5, 0.5, 0.0, result.a);
  MaterialColor = vec4(0.0, 0.0, 0.0, result.a);
}
