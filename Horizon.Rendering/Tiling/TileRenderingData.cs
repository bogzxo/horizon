using System.Numerics;

namespace Horizon.Rendering;

public abstract partial class Tiling<TTextureID>
    where TTextureID : Enum
{
    /// <summary>
    /// A data structure to store useful visual debugging properties.
    /// </summary>
    public struct TileRenderingData
    {
        public TTextureID TextureID;
        public Vector3 Color;
        public bool IsVisible;

        /// <summary>
        /// Whether the tile blocks light. Nothing here acts on it: whoever lights the map goes over its tiles and
        /// hands the ones that do to the renderer (see OcclusionMap2D).
        /// </summary>
        public bool CastsShadows;

        public TileRenderingData()
        {
            IsVisible = true;
            Color = Vector3.One;
        }
    }
}