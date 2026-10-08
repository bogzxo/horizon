// The second pass of the distance field, see sdf_common.slang.

#include <lighting/sdf_common.slang>

[shader("compute")]
[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    int2 texel = int2(id.xy);
    if (any(texel >= int2(params.uSize))) return;

    bool solid = solidAt(texel);
    float bestSolid = 1e12, bestOpen = 1e12;

    for (int y = 0; y < int(params.uSize.y); y++)
    {
        float2 row = uRows[int2(texel.x, y)];
        float dy = float(y - texel.y);
        bestSolid = min(bestSolid, row.x * row.x + dy * dy);
        bestOpen = min(bestOpen, row.y * row.y + dy * dy);
    }

    // The edge of a wall is half a texel past the last solid texel either way
    float distance = solid ? -(sqrt(bestOpen) - 0.5) : sqrt(bestSolid) - 0.5;
    uField[texel] = distance * params.uTexelWorld;
}
