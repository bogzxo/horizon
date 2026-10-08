#version 460 core

// The pass of the motion blur that shows it: the trails laid over the picture as it is right now.
// Blending the two everywhere would thin out whatever is on the move, as where it has only just got to there is next to
// nothing of it in the trails yet. A fighter who dashes would go see-through. So where the renderer says something is
// moving right now the picture is shown as it is, and the trail is only what that leaves behind.

layout(location = 0) in vec2 texCoords;

layout(binding = 0) uniform sampler2D uSource;
layout(binding = 1) uniform sampler2D uHistory;

// How fast everything in the picture is going (see encodeMotion in the shaders that write it), if anybody kept track.
layout(binding = 2) uniform sampler2D uMotion;
uniform bool uHasMotion;

// How much of the trail shows, 0 to 1.
uniform float uStrength;

// How much of what is moving right now is kept as it is, 0 to 1.
uniform float uSolid;

// What the motion of something that stands still in the world reads as, in steps away from 128. Nothing unless the
// camera is moving, in which case everything that stands still goes across the screen the other way.
uniform vec2 uStill;

out vec4 FragColor;

void main() {
  vec4 now = texture(uSource, texCoords);
  vec4 trailed = mix(now, texture(uHistory, texCoords), uStrength);

  float moving = 0.0;
  if (uHasMotion) {
    // How fast it goes through the world, which is what leaves a trail. A step and a half is as slow as that can be
    // told apart from standing still, the speeds are rounded and the camera's is an estimate
    vec2 away = texture(uMotion, texCoords).rg * 255.0 - 128.0 - uStill;
    moving = clamp(length(away) - 1.5, 0.0, 1.0);
  }

  FragColor = mix(trailed, now, moving * uSolid);
}
