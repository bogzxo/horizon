#version 460 core

// The path tracing itself. Every texel sends rays out in every direction, each one marched through the distance
// field (a step as long as the way to the nearest thing is, so it never skips one) until it hits something, and
// gathers the radiance that leaves what it hit. The scene's radiance includes what walls throw back from last frame,
// so light that bounced once is gathered as if it came from the wall, and the frame after that it has bounced twice.
// A texel that is itself a wall sends its rays from the air in front of it, only out of the wall and weighted by how
// squarely they leave it, which is the cosine law of a diffuse surface. The rays turn a little every frame and what
// they found is blended with what the last frames found (moved along with the camera), so a few rays a frame add
// up to hundreds.

layout(location = 0) in vec2 texCoords;

layout(binding = 0) uniform sampler2D uTexScene;
layout(binding = 1) uniform sampler2D uTexDistance;
layout(binding = 2) uniform sampler2D uTexHistory;

uniform int uRays;
uniform int uSteps;
uniform float uMaxDistance;   // texels
uniform int uFrame;
uniform vec2 uSize;
uniform vec2 uShift;          // where this texel was last frame, in texture coordinates from where it is now
uniform float uKeep;
uniform bool uReset;

out vec4 FragColor;

const float TAU = 6.28318530718;

// Interleaved gradient noise, a different start for the rays of neighbouring texels so nothing lines up
float noise(vec2 p) {
  return fract(52.9829189 * fract(0.06711056 * p.x + 0.00583715 * p.y));
}

void main() {
  vec2 texel = 1.0 / uSize;
  vec4 here = texture(uTexDistance, texCoords);
  bool wall = here.g > 0.5;

  // A wall gathers at its face, from the air just outside it, and only from directions leaving it
  vec2 origin = texCoords;
  vec2 outward = vec2(0.0);
  if (wall) {
    vec2 free = here.ba;
    outward = free - texCoords;
    float away = length(outward * uSize);
    outward = away > 0.0 ? outward / length(outward) : vec2(0.0, 1.0);
    origin = free + outward * texel * 0.5;
  }

  float jitter = noise(gl_FragCoord.xy + float(uFrame & 63) * 17.0);

  vec3 total = vec3(0.0);
  float weights = 0.0;

  for (int i = 0; i < uRays; i++) {
    float angle = (float(i) + jitter) / float(uRays) * TAU;
    vec2 dir = vec2(cos(angle), sin(angle));

    // Lambert for a wall, everything at once for the air
    float weight = wall ? max(dot(dir, outward), 0.0) * 2.0 : 1.0;
    if (weight <= 0.0) continue;
    weights += weight;

    float t = 0.0;
    for (int s = 0; s < uSteps; s++) {
      vec2 at = origin + dir * t * texel;
      if (any(lessThan(at, vec2(0.0))) || any(greaterThan(at, vec2(1.0)))) break;

      float d = texture(uTexDistance, at).r;
      if (d < 0.5) {
        total += texture(uTexScene, at).rgb * weight;
        break;
      }

      t += max(d, 1.0);
      if (t > uMaxDistance) break;
    }
  }

  vec3 current = weights > 0.0 ? total / max(weights, float(uRays) * (wall ? 0.5 : 1.0)) : vec3(0.0);

  // What the last frames found, from where this texel was then
  vec2 before = texCoords + uShift;
  vec4 history = texture(uTexHistory, before);
  bool valid = !uReset && history.a > 0.0 && all(greaterThanEqual(before, vec2(0.0))) && all(lessThanEqual(before, vec2(1.0)));

  FragColor = vec4(valid ? mix(current, history.rgb, uKeep) : current, 1.0);
}
