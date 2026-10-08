// The first pass of the distance field, see sdf_common.slang.

#include <lighting/sdf_common.slang>

[shader("compute")]
[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    int2 texel = int2(id.xy);
    if (any(texel >= int2(params.uSize))) return;

    bool solid = solidAt(texel);
    float toSolid = 1e6, toOpen = 1e6;

    // Walking out both ways from the texel, the first of each kind is the nearest in the row
    for (int step = 0; step < int(params.uSize.x); step++)
    {
        int left = texel.x - step, right = texel.x + step;
        bool any = false;

        if (left >= 0)
        {
            any = true;
            bool s = solidAt(int2(left, texel.y));
            if (s) toSolid = min(toSolid, float(step)); else toOpen = min(toOpen, float(step));
        }

        if (right < int(params.uSize.x))
        {
            any = true;
            bool s = solidAt(int2(right, texel.y));
            if (s) toSolid = min(toSolid, float(step)); else toOpen = min(toOpen, float(step));
        }

        if (!any || (toSolid < 1e5 && toOpen < 1e5)) break;
    }

    uRows[texel] = float2(toSolid, toOpen);
}

