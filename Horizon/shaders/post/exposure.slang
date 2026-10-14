// The picture brought to a brightness, see ExposureEffect. Every colour is scaled by the exposure (the fixed one
// times what the eye has got used to, see exposure_measure.slang), what goes over the knee is rolled off towards
// white rather than cut (by the brightest channel, so a lamp that is too bright keeps the colour of the lamp), and a
// gamma lifts or sinks the middle of the range. With a gamma over 1 the dark end comes up without the bright end
// going anywhere, which is what takes the harshness out of a lit window on a dark street. That one wants to be
// the dark gamma, which only comes in as the eye adapts to the dark and leaves a bright day alone.

#include <common/screen.slang>

struct Params
{
    float uExposure;   // the fixed part of the exposure, 1 for the picture as it is
    float uGamma;      // 1 for none, over 1 lifts the middle, under 1 sinks it
    float uKnee;       // where the roll off of the bright end starts, 1 for no roll off at all
    float uDarkGamma;  // a gamma on top that only comes in as the eye gets used to the dark, 1 for none
    float uMost;       // the most the eye adapts, which is where the dark gamma is all there
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

BIND_TEXTURE(0) Sampler2D uSource;
BIND_BUFFER(2) StructuredBuffer<float> uAdapted;

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    float4 source = uSource.Sample(input.texCoords);

    float adapted = uAdapted[0];
    if (!(adapted > 0.0)) adapted = 1.0;
    float3 color = source.rgb * (params.uExposure * adapted);

    // Up to the knee as it is, past it eased off towards 1, the colour as a whole by its brightest channel
    float knee = clamp(params.uKnee, 0.0, 1.0);
    float brightest = max(color.r, max(color.g, color.b));
    if (knee < 1.0 && brightest > knee)
    {
        float rolled = knee + (1.0 - knee) * (1.0 - exp(-(brightest - knee) / (1.0 - knee)));
        color *= rolled / brightest;
    }

    // The dark gamma by how far the eye has gone into the dark, none of it in daylight. A lift that is always
    // on washes a bright day out to milk, which is exactly what it did
    float dark = saturate((adapted - 1.0) / max(params.uMost - 1.0, 1e-3));
    float gamma = params.uGamma * lerp(1.0, params.uDarkGamma, dark);
    color = pow(max(color, 0.0), 1.0 / max(gamma, 0.05));
    return float4(color, source.a);
}
