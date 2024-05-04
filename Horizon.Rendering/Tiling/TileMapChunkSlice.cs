namespace Horizon.Rendering;

public abstract partial class Tiling<TTextureID>
{
    public class TileMapChunkSlice
    {
        /// <summary>
        /// Gets the 2D array of tiles in the chunk.
        /// </summary>
        public List<Tile> Tiles { get; init; }

        /// <summary>
        /// This is the index of the slice.
        /// </summary>
        public uint Index { get; internal set; }

        public TileMapChunkSlice()
        {
            this.Tiles = new();
        }
    }
}