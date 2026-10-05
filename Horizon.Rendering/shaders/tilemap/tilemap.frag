#version 410 core

// Albedo, surface and material are the attachments of a DeferredRenderer2D, see its summary for what goes where.
// Drawn straight to the window only the first of them goes anywhere.
layout(location = 0) out vec4 AlbedoColor;
layout(location = 1) out vec4 SurfaceColor;
layout(location = 2) out vec4 MaterialColor;

in vec2 texCoords;
in vec3 color;
in float shouldDiscard;
in vec2 fragPos;

uniform sampler2D uTextureAlbedo;
uniform sampler2D uTextureNormal;
uniform sampler2D uTextureSpecular;

// Not every tile set comes with a normal map, or with a specular one.
uniform bool uHasNormal;
uniform bool uHasSpecular;

// How much of the layer shows no matter the light (a sky, a glowing sign).
uniform float uEmissive;

uniform bool uWireframeEnabled;

void main() {
  AlbedoColor = texture(uTextureAlbedo, texCoords) * vec4(color, 1.0);

  if (shouldDiscard == 1.0 || AlbedoColor.a < 0.1)
    discard;

  vec2 normal = uHasNormal ? texture(uTextureNormal, texCoords).xy : vec2(0.5);
  SurfaceColor = vec4(normal, uEmissive, AlbedoColor.a);

  float shine = uHasSpecular ? texture(uTextureSpecular, texCoords).r : 0.0;
  MaterialColor = vec4(shine, 0.0, 0.0, AlbedoColor.a);
}
