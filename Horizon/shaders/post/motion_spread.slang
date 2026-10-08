// The second pass of the motion blur. Every square takes the fastest of itself and the eight squares around it.
// Something that moves blurs past the edge of the square it is in, the pixels over there have to hear about it.

#include <common/screen.slang>

BIND_TEXTURE(0) Sampler2D uTiles;

[shader("fragment")]
float4 fsMain(ScreenVaryings input, float4 fragCoord : SV_Position) : SV_Target0
{
    int2 tile = int2(fragCoord.xy);
    uint width, height;
    uTiles.GetDimensions(width, height);
    int2 last = int2(width, height) - 1;

    float2 fastest = float2(128.0 / 255.0, 128.0 / 255.0);
    float speed = 0.0;

    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            float2 motion = uTiles.Load(int3(clamp(tile + int2(x, y), int2(0, 0), last), 0)).rg;
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
