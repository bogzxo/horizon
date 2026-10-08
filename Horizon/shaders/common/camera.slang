// The camera every shader draws through, set once a frame for all of them (see CameraBlock.cs, which this must
// match, std140). Include it rather than asking for the matrices as uniforms of your own.
//
//   #include <common/camera.slang>
//   output.position = toClip(float4(world, 0.0, 1.0));
//
// The matrices are row major and multiplied the System.Numerics way, a row vector times the matrix, so what the
// engine works out on the CPU is exactly what the GPU gets. The projection has already been turned the way Vulkan
// wants its clip space, nothing here has to think about that.
// uProjectionScale is how much of the screen a unit of the world is (halves of it), the camera's own projection
// before Vulkan's way round is applied to it, for whoever wants to know how big a pixel is in the world.

struct Camera
{
    float4x4 uView;
    float4x4 uProjection;
    float4x4 uViewProjection;
    float4x4 uInverseViewProjection;
    float2 uProjectionScale;
    float2 uCameraPadding2;
    float2 uViewportSize;
    float2 uViewportTexel;
    float uTotalTime;
    float uDeltaTime;
    float uRuntime;
    float uCameraPadding;
};

BIND_UNIFORM(0) ConstantBuffer<Camera> camera;

// A point of the world, to where it lands on screen.
float4 toClip(float4 world)
{
    return mul(world, camera.uViewProjection);
}

// Where in the world a point of the picture (0 to 1 either way, from the top left) is looking.
float2 worldOf(float2 texCoords)
{
    return mul(float4(texCoords * 2.0 - 1.0, 0.0, 1.0), camera.uInverseViewProjection).xy;
}

// Where on the picture (0 to 1 either way) a point of the world lands.
float2 pictureOf(float2 world)
{
    float4 clip = mul(float4(world, 0.0, 1.0), camera.uViewProjection);
    return clip.xy / clip.w * 0.5 + 0.5;
}
