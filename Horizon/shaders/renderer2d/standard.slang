// Puts the picture of a Renderer2D on screen as it is.

#include <common/screen.slang>

BIND_TEXTURE(0) Sampler2D uTexAlbedo;

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    return uTexAlbedo.Sample(input.texCoords);
}
