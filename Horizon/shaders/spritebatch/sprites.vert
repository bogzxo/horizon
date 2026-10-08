#version 460 core

#include <common/camera.glsl>

// A corner of the one quad every sprite is drawn as, from -0.5 to 0.5.
layout(location = 0) in vec2 vPos;
layout(location = 1) in vec2 vTexCoords;

// What the whole batch is moved by.
uniform mat4 uModel;

// Must match SpriteItem.cs.
struct SpriteItem {
  vec2 origin;
  vec2 axisX;
  vec2 axisY;
  vec2 texMin;
  vec2 texMax;
  vec2 motion;
  uint color;
  uint flags;
  float depth;
  float ring;
};

// The items of this frame, bound as the range of the stream buffer the frame is in (see StreamBuffer.BindRange), so
// the first item of the frame is data[0]. A draw that starts partway through them says so with its base instance.
layout(std430, binding = 1) readonly restrict buffer SpriteData {
  SpriteItem data[];
};

layout(location = 0) out vec2 oTexel;
layout(location = 1) out vec2 oFragPos;
layout(location = 2) out vec4 oColor;
layout(location = 3) flat out uint oFlags;
layout(location = 4) flat out vec2 oMotion;
layout(location = 5) flat out float oRing;

void main() {
  SpriteItem item = data[gl_BaseInstance + gl_InstanceID];

  // The quad goes from -0.5 to 0.5, the item is described from its bottom left corner.
  vec2 corner = vPos + vec2(0.5);

  // texMin is what the top left corner shows, so V runs against Y. Left in texels: the fragment shader
  // knows which texture they are of.
  oTexel = mix(item.texMin, item.texMax, vec2(corner.x, 1.0 - corner.y));
  oColor = unpackUnorm4x8(item.color);
  oFlags = item.flags;
  oRing = item.ring;

  vec4 worldPos = uModel * vec4(item.origin + item.axisX * corner.x + item.axisY * corner.y, item.depth, 1.0);
  gl_Position = uViewProjection * worldPos;
  oFragPos = worldPos.xy;

  // Across the screen, in halves of it a second: how fast it goes through the world less how fast the camera does.
  oMotion = screenMotion(mat2(uModel) * item.motion);
}
