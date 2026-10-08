// The first pass of the motion blur. The picture cut into squares, and for each of them the fastest thing in it.
// This is what lets the blur find out cheaply that something fast is passing near a pixel that itself stands still.

#include <common/screen.slang>

BIND_TEXTURE(0) Sampler2D uMotion;

// How many pixels a square is wide and high. Must match MotionBlurEffect.TILE_SIZE.
static const int TILE = 16;

// Every other pixel of a square is looked at, each way. A quarter of the reads, and what moves is rarely a pixel wide.
static const int STEP = 2;

[shader("fragment")]
float4 fsMain(ScreenVaryings input, float4 fragCoord : SV_Position) : SV_Target0
{
    int2 corner = int2(fragCoord.xy) * TILE;
    uint width, height;
    uMotion.GetDimensions(width, height);
    int2 last = int2(width, height) - 1;

    // Kept the way it is written into the picture (see encodeMotion in the shaders that do), 128 being none
    float2 fastest = float2(128.0 / 255.0, 128.0 / 255.0);
    float speed = 0.0;

    for (int y = 0; y < TILE; y += STEP)
    {
        for (int x = 0; x < TILE; x += STEP)
        {
            float2 motion = uMotion.Load(int3(min(corner + int2(x, y), last), 0)).rg;
            float2 away = motion * 255.0 - 128.0;
            float found = dot(away, away);

            if (found > speed)
            {
                speed = found;
                fastest = motion;
            }
        }
    }

    return float4(fastest, 0.0, 1.0);
}
