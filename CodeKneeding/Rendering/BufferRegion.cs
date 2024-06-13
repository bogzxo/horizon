namespace CodeKneading.Rendering;

internal class BufferRegion(in BufferDataHeap heap, in int index, in int offset, in int length)
{
    protected readonly BufferDataHeap heap = heap;

    public readonly int Index = index;
    public readonly int OffsetInBytes = offset;
    public readonly int LengthInBytes = length;

    public unsafe void BufferData<T>(ReadOnlySpan<T> data)
        where T : unmanaged
    {
        if ((data.Length * sizeof(T)) > LengthInBytes)
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Attempt to write data larger than allocated chunk heap region.");
            return;
        }
        heap.Buffer.NamedBufferSubData(data, OffsetInBytes);
    }
}
