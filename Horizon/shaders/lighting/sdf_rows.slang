// The first pass of the distance field, see sdf_common.slang.
// Uses a groupshared bitmask for rows up to 4096 texels wide (O(W/32) bit ops per texel, zero per-step texture reads)
// and falls back to exact cell-stride marching for wider rows so any map width is handled cleanly.

#include <lighting/sdf_common.slang>

static const int MAX_MASK_WORDS = 128; // 128 * 32 = 4096 texels
groupshared uint rowBits[MAX_MASK_WORDS];

[shader("compute")]
[numthreads(64, 1, 1)]
void csMain(uint3 groupId : SV_GroupID, uint3 localId : SV_GroupThreadID, uint3 id : SV_DispatchThreadID)
{
    int width = int(params.uSize.x);
    int height = int(params.uSize.y);
    int y = int(id.y);
    if (y >= height) return;

    int x = int(id.x);

    if (width <= MAX_MASK_WORDS * 32)
    {
        int wordCount = (width + 31) >> 5;
        for (int w = int(localId.x); w < wordCount; w += 64)
        {
            uint mask = 0u;
            int baseX = w << 5;
            int limit = min(32, width - baseX);
            for (int b = 0; b < limit; b++)
            {
                if (solidAt(int2(baseX + b, y)))
                    mask |= (1u << uint(b));
            }
            rowBits[w] = mask;
        }
        GroupMemoryBarrierWithGroupSync();

        if (x >= width) return;

        int w0 = x >> 5;
        uint b0 = uint(x & 31);
        bool solid = ((rowBits[w0] >> b0) & 1u) != 0u;

        int lastWord = wordCount - 1;
        uint rem = uint(width & 31);
        uint lastMask = rem == 0u ? 0xFFFFFFFFu : ((1u << rem) - 1u);

        // Search only for the opposite bit; distance to own kind is 0
        float nearest = 1e6;

        // Right scan from (w0, b0)
        uint word0 = solid ? ~rowBits[w0] : rowBits[w0];
        if (w0 == lastWord) word0 &= lastMask;
        uint rightBits = word0 & (~0u << b0);
        if (rightBits != 0u)
        {
            nearest = float(firstbitlow(rightBits) - b0);
        }
        else
        {
            for (int w = w0 + 1; w < wordCount; w++)
            {
                uint bits = solid ? ~rowBits[w] : rowBits[w];
                if (w == lastWord) bits &= lastMask;
                if (bits != 0u)
                {
                    nearest = float((w << 5) + int(firstbitlow(bits)) - x);
                    break;
                }
            }
        }

        // Left scan from (w0, b0)
        uint leftMask = (1u << b0) | ((1u << b0) - 1u);
        uint leftBits = word0 & leftMask;
        if (leftBits != 0u)
        {
            nearest = min(nearest, float(b0 - firstbithigh(leftBits)));
        }
        else
        {
            for (int w = w0 - 1; w >= 0; w--)
            {
                uint bits = solid ? ~rowBits[w] : rowBits[w];
                if (bits != 0u)
                {
                    nearest = min(nearest, float(x - ((w << 5) + int(firstbithigh(bits)))));
                    break;
                }
            }
        }

        uRows[int2(x, y)] = solid ? float2(0.0, nearest) : float2(nearest, 0.0);
        return;
    }

    if (x >= width) return;

    // Wide-map fallback: walk cell-by-cell in uOcclusion (each cell spans uTexelsPerCell texels)
    int perCell = max(params.uTexelsPerCell, 1);
    int cellsX = int(params.uCells.x);
    int cellY = clamp(y / perCell, 0, int(params.uCells.y) - 1);
    int cellX = clamp(x / perCell, 0, cellsX - 1);
    bool solid = uOcclusion.Load(int3(cellX, cellY, 0)).r > 0.5;
    float nearest = 1e6;

    for (int step = 1; step < cellsX; step++)
    {
        bool found = false;
        int leftCell = cellX - step;
        if (leftCell >= 0)
        {
            bool s = uOcclusion.Load(int3(leftCell, cellY, 0)).r > 0.5;
            if (s != solid)
            {
                int edgeX = (leftCell + 1) * perCell - 1;
                nearest = min(nearest, float(x - edgeX));
                found = true;
            }
        }

        int rightCell = cellX + step;
        if (rightCell < cellsX)
        {
            bool s = uOcclusion.Load(int3(rightCell, cellY, 0)).r > 0.5;
            if (s != solid)
            {
                int edgeX = rightCell * perCell;
                nearest = min(nearest, float(edgeX - x));
                found = true;
            }
        }

        if (found) break;
    }

    uRows[int2(x, y)] = solid ? float2(0.0, nearest) : float2(nearest, 0.0);
}
