
using Horizon.OpenGL.Assets;

namespace CodeKneading.Rendering;

internal class BufferDataHeap(in BufferObject buffer)
{
    internal readonly BufferObject Buffer = buffer;

    public int UsedBytes { get; protected set; }
    public int ActiveAllocations { get => allocatedRegions.Count; }
    public int Health { get; private set; }

    //private readonly List<BufferRegion> freedRegions = [];
    private readonly List<BufferRegion> allocatedRegions = [];

    public bool TryGetRegion(int size, out BufferRegion region)
    {
        if (size + UsedBytes >= Buffer.Size)
        {
            region = new BufferRegion(this, 0, 0, 0);
            return false;
        }
        /*  var validReturnedRegions = (from freeRegion in freedRegions
                                   where freeRegion.LengthInBytes >= size
                                   orderby freeRegion.LengthInBytes ascending
                                   select freeRegion).ToArray();*/
        //foreach (var freeRegion in freedRegions)
        //{
        //    if (freeRegion.LengthInBytes >= size)
        //    {
        //        allocatedRegions.Add(freeRegion);
        //        freedRegions.Remove(freeRegion);
        //        UsedBytes += freeRegion.LengthInBytes;
        //        region = freeRegion;
        //        return true;
        //    }
        //}

        region = new BufferRegion(this, allocatedRegions.Count, UsedBytes, size);
        UsedBytes += size;
        UpdateHealth();

        allocatedRegions.Add(region);
        return true;
    }

    private void UpdateHealth()
    {
        Health = (int)(((float)UsedBytes / Buffer.Size) * 100.0f);
    }

    public void Return(in BufferRegion region)
    {
        if (!allocatedRegions.Contains(region)) return;

        UsedBytes -= region.LengthInBytes;
        UpdateHealth();
        allocatedRegions.Remove(region);
        //freedRegions.Add(region);
    }

    public void Clear()
    {
        allocatedRegions.Clear();
        UsedBytes = 0;
        UpdateHealth();
    }
}
