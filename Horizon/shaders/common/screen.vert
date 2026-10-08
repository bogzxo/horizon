#version 460 core

// The vertex shader of everything that works on a whole picture: the post processing passes, the technique that
// puts a renderer on screen, the scene transitions. One triangle over everything, made up here out of the vertex
// number alone (see ScreenTriangle.Draw, which draws three vertices out of an empty vertex array): it reaches well
// past two of the edges, and what sticks out is never drawn. There is no seam down the middle for the pixels along
// it to be shaded twice, the way two triangles would.

layout(location = 0) out vec2 texCoords;

void main() {
  // (0, 0), (2, 0), (0, 2) in the picture: the triangle covers the square from 0 to 1
  texCoords = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
  gl_Position = vec4(texCoords * 2.0 - 1.0, 0.0, 1.0);
}
