#version 410 core

// A corner of a quad of one by one around its middle, and the corner of the tile's image that goes there
// (0, 0 being its top left one).
layout(location = 0) in vec2 vPos;
layout(location = 1) in vec2 vTexCoords;

// What is different for every tile, see TileInstance: where its middle is and how big it is, the part of the image it
// is cut out of (in pixels: left, top, right, bottom), and how it is turned (radians) and turned over (a TileFlip).
layout(location = 2) in vec2 iPos;
layout(location = 3) in vec2 iSize;
layout(location = 4) in vec4 iSource;
layout(location = 5) in vec2 iTurn;

uniform mat4 uCameraView;
uniform mat4 uCameraProjection;

// Where the layer is: the corner of the map, how far the layer is moved from it and what its parallax adds to that.
uniform vec2 uOffset;

// One over the size of the image, which turns pixels of it into texture coordinates.
uniform vec2 uTexelSize;

out vec2 texCoords;
flat out int flip;

const int FLIP_HORIZONTAL = 1;
const int FLIP_VERTICAL = 2;
const int FLIP_DIAGONAL = 4;

void main() {
  float c = cos(iTurn.x);
  float s = sin(iTurn.x);
  vec2 corner = vPos * iSize;
  vec2 world = iPos + vec2(corner.x * c - corner.y * s, corner.x * s + corner.y * c) + uOffset;

  // Tiled turns a tile over along its diagonal first, then sideways, then upside down. Finding the part of the image
  // that ends up at a corner is the same thing backwards.
  flip = int(iTurn.y + 0.5);
  vec2 at = vTexCoords;
  if ((flip & FLIP_VERTICAL) != 0) at.y = 1.0 - at.y;
  if ((flip & FLIP_HORIZONTAL) != 0) at.x = 1.0 - at.x;
  if ((flip & FLIP_DIAGONAL) != 0) at = at.yx;

  texCoords = mix(iSource.xy, iSource.zw, at) * uTexelSize;
  gl_Position = uCameraProjection * uCameraView * vec4(world, 0.0, 1.0);
}
