// The second pass of the sprite shadows. For every texel, how far up or down its column the nearest edge is (see
// shadow_edges.slang), of what blocks and of what blocks or glows, which takes a sweep down the column and one
// back up. A workgroup a column. The whole group reads the column into shared memory, one thread does the two
// sweeps there (which is quick, nothing waits on memory), and the whole group writes it out. The rows pass
// (shadow_rows.slang) makes the real distances out of these.

struct Params
{
    float2 uSize;   // the picture, in texels
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

BIND_TEXTURE(0) Sampler2D uEdges;

// How far along the column the nearest edge is, in texels, of what blocks (r) and of what blocks or glows (g).
BIND_IMAGE(0) [[vk::image_format("rg16f")]] RWTexture2D<float2> uColumns;

static const float FAR = 4096.0;
static const int LONGEST = 4096;
static const int THREADS = 256;

groupshared float2 column[LONGEST];

[shader("compute")]
[numthreads(THREADS, 1, 1)]
void csMain(uint3 groupId : SV_GroupID, uint3 threadId : SV_GroupThreadID)
{
    int2 size = int2(params.uSize);
    int x = int(groupId.x);
    int height = min(size.y, LONGEST);
    int thread = int(threadId.x);

    // The edges of the column, 1 where there is one
    for (int y = thread; y < height; y += THREADS)
        column[y] = uEdges.Load(int3(x, y, 0)).ba;

    GroupMemoryBarrierWithGroupSync();

    if (thread == 0)
    {
        // Down, how far the last edge above is
        float2 since = float2(FAR, FAR);
        for (int y = 0; y < height; y++)
        {
            float2 edge = column[y];
            since = min(since + 1.0, FAR);
            if (edge.x > 0.5) since.x = 0.0;
            if (edge.y > 0.5) since.y = 0.0;
            column[y] = since;
        }

        // And back up, the nearer of that and the next edge below
        since = float2(FAR, FAR);
        for (int y = height - 1; y >= 0; y--)
        {
            since = min(column[y], since + 1.0);
            column[y] = since;
        }
    }

    GroupMemoryBarrierWithGroupSync();

    for (int y = thread; y < height; y += THREADS)
        uColumns[int2(x, y)] = column[y];
}
