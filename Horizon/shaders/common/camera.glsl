// The camera every shader draws through, set once a frame for all of them (see CameraBlock.cs, which this must
// match, std140). Include it rather than asking for the matrices as uniforms of your own:
//
//   #include <common/camera.glsl>
//   gl_Position = uViewProjection * vec4(world, 0.0, 1.0);
//
// uCameraVelocity is how fast the camera goes across the world (units a second), uMotionScale how much of the
// screen a unit of the world is (halves of it): together they turn a speed through the world into a speed across
// the screen, which is what the motion blur goes by, see motion.glsl.

layout(std140, binding = 0) uniform Camera {
  mat4 uView;
  mat4 uProjection;
  mat4 uViewProjection;
  mat4 uInverseViewProjection;
  vec2 uCameraVelocity;
  vec2 uMotionScale;
  vec2 uViewportSize;
  vec2 uViewportTexel;
  float uTotalTime;
  float uDeltaTime;
  float uRuntime;
  float uCameraPadding;
};

// How fast something that goes through the world at a speed goes across the screen, in halves of the screen a second.
vec2 screenMotion(vec2 worldVelocity) {
  return (worldVelocity - uCameraVelocity) * uMotionScale;
}
