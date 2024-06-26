﻿#version 460

layout(location = 0) in vec2 vPos;
layout(location = 1) in vec2 vTexCoords;

uniform mat4 uCameraView;
uniform mat4 uCameraProjection;

uniform vec2 uSingleFrameSize;


struct SpriteData {
  mat4 modelMatrix;
  vec2 spriteOffset;
  uint frameIndex;
  uint spacer;
  //vec2 spacer0, spacer1, spacer2, spacer3, spacer4, spacer5;
};

layout(std430) buffer spriteData 
{
  SpriteData data[];
};


layout(location = 0) out vec2 oTexCoords;
layout(location = 1) out vec2 oFragPos;

void main() {
  // calculate texture coords with animation data.
  oTexCoords = data[gl_InstanceID].spriteOffset + vTexCoords * uSingleFrameSize + vec2(uSingleFrameSize.x * data[gl_InstanceID].frameIndex, 0);
  
  // Transform the vertex position
  vec4 worldPos = data[gl_InstanceID].modelMatrix * vec4(vPos, 0.0, 1.0);
  gl_Position = uCameraProjection * uCameraView * worldPos;
  oFragPos = worldPos.xy;
}
