// The last pass of the sprite shadows. With the nearest edge up or down every column known (see
// shadow_columns.slang), the distance to the nearest edge anywhere is, for a texel of a row, the least over the
// row of how far along it plus how far up or down there, squared and added. Done the exact way (Felzenszwalb and
// Huttenlocher's lower envelope of parabolas), a walk along the row for every row. A workgroup a row, the whole
// group reads the row into shared memory, one thread walks it to build the envelope there, and every thread then
// finds the piece of the envelope its texels are under and writes them out. What comes out is the signed
// distance in world units, negative inside of whatever blocks (r) and inside of whatever blocks or glows (g). The
// lights march the first the way they march the distance field of the occlusion map, the path traced lighting
// marches the second, see direct.slang, and a sprite throws a shadow as sharp as its pixels.
// A row can be as long as LONGEST, a picture wider than that only gets its first texels of every row. Nobody
// draws a G-buffer that wide.

struct Params
{
    float2 uSize;        // the picture, in texels
    float uPixelWorld;   // how big a texel of the picture is in world units
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

BIND_TEXTURE(0) Sampler2D uColumns;
BIND_TEXTURE(1) Sampler2D uEdges;
BIND_IMAGE(0) [[vk::image_format("rg16f")]] RWTexture2D<float2> uField;

static const float FAR = 4096.0;
static const int LONGEST = 2048;
static const int THREADS = 256;

// The squared column distances along the row, which texel each piece of the envelope stands on, and where the
// pieces meet
groupshared float f[LONGEST];
groupshared int v[LONGEST];
groupshared float z[LONGEST + 1];
groupshared int pieces;

[shader("compute")]
[numthreads(THREADS, 1, 1)]
void csMain(uint3 groupId : SV_GroupID, uint3 threadId : SV_GroupThreadID)
{
    int2 size = int2(params.uSize);
    int y = int(groupId.x);
    int width = min(size.x, LONGEST);
    int thread = int(threadId.x);

    for (int channel = 0; channel < 2; channel++)
    {
        for (int x = thread; x < width; x += THREADS)
        {
            float2 both = uColumns.Load(int3(x, y, 0)).rg;
            float g = channel == 0 ? both.r : both.g;
            f[x] = g * g;
        }

        GroupMemoryBarrierWithGroupSync();

        if (thread == 0)
        {
            int k = 0;
            v[0] = 0;
            z[0] = -1e20;
            z[1] = 1e20;

            for (int q = 1; q < width; q++)
            {
                float s;
                for (;;)
                {
                    int p = v[k];
                    s = ((f[q] + float(q * q)) - (f[p] + float(p * p))) / (2.0 * float(q - p));
                    if (s > z[k] || k == 0) break;
                    k--;
                }

                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = 1e20;
            }

            pieces = k + 1;
        }

        GroupMemoryBarrierWithGroupSync();

        for (int x = thread; x < width; x += THREADS)
        {
            // The piece this texel is under, the pieces meet at ever greater x so it is a halving search
            int low = 0, high = pieces - 1;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (z[middle] < float(x)) low = middle; else high = middle - 1;
            }

            int p = v[low];
            float dx = float(x - p);
            float toEdge = min(sqrt(dx * dx + f[p]), FAR);

            // The edge of what blocks is half a texel past the texels on it either way
            float4 edges = uEdges.Load(int3(x, y, 0));
            bool inside = (channel == 0 ? edges.r : edges.g) > 0.5;
            float distance = (inside ? -(toEdge + 0.5) : toEdge - 0.5) * params.uPixelWorld;

            int2 texel = int2(x, y);
            float2 field = channel == 0 ? float2(distance, 0.0) : uField[texel];
            if (channel == 1) field.g = distance;
            uField[texel] = field;
        }

        GroupMemoryBarrierWithGroupSync();
    }
}
