#version 460 core

#include <common/camera.glsl>

// A corner of the one quad every particle is drawn as.
layout(location = 0) in vec2 vPos;

// What is different for every particle, out of the simulator's buffer.
layout(location = 1) in vec2 vOffset;
layout(location = 2) in float vAlive;
layout(location = 3) in vec2 vVelocity;

// How far a particle is drawn out along the way it moves: it is as long as the way it goes in this many seconds, but
// no longer than the limit (in units of the world) and never shorter than it is anyway.
uniform float uStretch;
uniform float uMaxStretch;

// How far (in seconds, back is negative) from where the particles were last moved to the moment the frame shows. Only
// for particles that are moved a tick at a time, see ParticleRenderer2D. Nothing for those that are moved every frame.
uniform float uTimeOffset;

out float alive;
out vec2 fragPos;
flat out vec2 motion;

void main() {
  alive = vAlive;
  motion = screenMotion(vVelocity);

  // The corners at the back stay behind where the particle was a moment ago, which draws a drop out into a streak:
  // a stream that falls faster and faster would come apart into a string of dots otherwise.
  float speed = length(vVelocity);
  float tail = min(speed * uStretch, uMaxStretch) - 2.0 * abs(vPos.x);

  vec2 corner = vPos;
  if (tail > 0.0) {
    vec2 heading = vVelocity / speed;
    if (dot(vPos, heading) < 0.0) corner -= heading * tail;
  }

  fragPos = vOffset + vVelocity * uTimeOffset + corner;

  // The compute simulator keeps dead particles in its pool, so they still arrive here as instances:
  // push them outside the clip volume so they never reach the rasteriser.
  gl_Position = vAlive > 0.0
    ? uViewProjection * vec4(fragPos, 0.0, 1.0)
    : vec4(2.0, 2.0, 2.0, 1.0);
}
