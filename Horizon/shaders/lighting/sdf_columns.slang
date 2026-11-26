// The second pass of the distance field, see sdf_common.slang.
// Spirals outward from texel.y and exits as soon as dy^2 >= best, bit-identical to scanning the whole column.

#include <lighting/sdf_common.slang>

[shader("compute")]
[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    int2 texel = int2(id.xy);
    int height = int(params.uSize.y);
    if (any(texel >= int2(params.uSize))) return;

    bool solid = solidAt(texel);
    float2 row0 = uRows[texel];
    float d0 = solid ? row0.y : row0.x;
    float best = d0 * d0;

    for (int dy = 1; dy < height; dy++)
    {
        float dy2 = float(dy) * float(dy);
        if (dy2 >= best) break;

        int up = texel.y - dy;
        int down = texel.y + dy;
        bool anyRow = false;

        if (up >= 0)
        {
            anyRow = true;
            float2 r = uRows[int2(texel.x, up)];
            float rx = solid ? r.y : r.x;
            best = min(best, rx * rx + dy2);
        }

        if (down < height)
        {
            anyRow = true;
            float2 r = uRows[int2(texel.x, down)];
            float rx = solid ? r.y : r.x;
            best = min(best, rx * rx + dy2);
        }

        if (!anyRow) break;
    }

    // The edge of a wall is half a texel past the last solid texel either way
    float distance = solid ? -(sqrt(best) - 0.5) : sqrt(best) - 0.5;
    uField[texel] = distance * params.uTexelWorld;
}
