// The picture as it is, for an effect that has nothing to do to it this frame.

#include <common/screen.slang>

BIND_TEXTURE(0) Sampler2D uSource;

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    return uSource.Sample(input.texCoords);
}
