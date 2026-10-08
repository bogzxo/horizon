// How fast a fragment is going across the screen, the way the motion attachment of a DeferredRenderer2D (and a
// PostLayer) stores it: two bytes, halves of the screen a second, MOTION_RANGE of them either way, with 128 standing
// for none so that standing still is exact. Everything that writes or reads the motion attachment includes this.

const float MOTION_RANGE = 2.0;

vec2 encodeMotion(vec2 speed) {
  return (128.0 + clamp(speed / MOTION_RANGE, -1.0, 1.0) * 127.0) / 255.0;
}

vec2 decodeMotion(vec2 encoded) {
  return (encoded * 255.0 - 128.0) / 127.0 * MOTION_RANGE;
}
