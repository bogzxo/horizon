namespace AutoVoxel.Data.Chunks;

public interface ISliceData
{
    public Tile this[int x, int y, int z] { get; set; }
}