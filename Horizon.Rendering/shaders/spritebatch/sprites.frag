#version 410 core

// Albedo, surface and material are the attachments of a DeferredRenderer2D, see its summary for what goes where.
// Drawn straight to the window only the first of them goes anywhere.
layout(location = 0) out vec4 AlbedoColor;
layout(location = 1) out vec4 SurfaceColor;
layout(location = 2) out vec4 MaterialColor;

layout(location = 0) in vec2 texCoords;
layout(location = 1) in vec2 fragPos;

uniform sampler2D uTexture;

void main() {
  vec4 tex = texture(uTexture, texCoords);
  if (tex.a == 0) discard;
  AlbedoColor = tex;

  // Sprites have no normal map, are lit like everything else and aren't shiny.
  SurfaceColor = vec4(0.5, 0.5, 0.0, tex.a);
  MaterialColor = vec4(0.0, 0.0, 0.0, tex.a);
}
