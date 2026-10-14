#version 410 core

// The second pass of the motion blur: every square takes the fastest of itself and the eight squares around it.
// Something that moves blurs past the edge of the square it is in, the pixels over there have to hear about it.

uniform sampler2D uTiles;

out vec4 FragColor;

void main() {
  ivec2 tile = ivec2(gl_FragCoord.xy);
  ivec2 last = textureSize(uTiles, 0) - 1;

  vec2 fastest = vec2(128.0 / 255.0);
  float speed = 0.0;

  for (int y = -1; y <= 1; y++) {
    for (int x = -1; x <= 1; x++) {
      vec2 motion = texelFetch(uTiles, clamp(tile + ivec2(x, y), ivec2(0), last), 0).rg;
      vec2 away = motion * 255.0 - 128.0;
      float found = dot(away, away);

      if (found > speed) {
        speed = found;
        fastest = motion;
      }
    }
  }

  FragColor = vec4(fastest, 0.0, 1.0);
}
