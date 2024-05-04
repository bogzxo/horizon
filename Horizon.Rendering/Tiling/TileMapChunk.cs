using System.Numerics;

using Box2D.NetStandard.Dynamics.Bodies;

using Horizon.Core.Components;

using SixLabors.ImageSharp;

namespace Horizon.Rendering;

public abstract partial class Tiling<TTextureID>
{
    /// <summary>
    /// Represents a chunk of tiles in a tile map.
    /// </summary>
    public class TileMapChunk : IInstantiable
    {
        /// <summary>
        /// Gets or sets the physics body associated with the chunk.
        /// </summary>
        public Body? Body { get; set; }

        /// <summary>
        /// The width of the tile map chunk in tiles.
        /// </summary>
        public const int WIDTH = 32;

        /// <summary>
        /// The height of the tile map chunk in tiles.
        /// </summary>
        public const int HEIGHT = 32;

        public List<TileMapChunkSlice> Slices { get; init; }
        private List<int> alwaysOnTop = new();

        /// <summary>
        /// Gets the position of the chunk in the tile map.
        /// </summary>
        public Vector2 Position { get; init; }

        /// <summary>
        /// Gets the parent tile map to which this chunk belongs.
        /// </summary>
        public TileMap Map { get; init; }

        /// <summary>
        /// Gets the renderer responsible for rendering the chunk.
        /// </summary>
        public TileMapChunkRenderer Renderer { get; init; }

        /// <summary>
        /// Gets the bounds of the chunk in world space.
        /// </summary>
        public RectangleF Bounds { get; init; }

        /// <summary>
        /// Gets or sets a value indicating whether the chunk is visible by the camera.
        /// </summary>
        public bool IsVisibleByCamera { get; set; } = true;

        /// <summary>
        /// Gets a value indicating whether the chunk should be updated.
        /// </summary>
        public bool IsDirty { get; internal set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="TileMapChunk"/> class.
        /// </summary>
        /// <param name="map">The parent tile map to which this chunk belongs.</param>
        /// <param name="pos">The position of the chunk in the tile map.</param>
        public TileMapChunk(TileMap map, Vector2 pos)
        {
            Renderer = new(this);

            Map = map;
            Position = pos;

            if (map.World is not null)
                Body = map.World.CreateBody(new BodyDef { type = BodyType.Static });

            Slices = new();

            Bounds = new RectangleF(
                pos * new Vector2(WIDTH * Map.TileSize.X, HEIGHT * Map.TileSize.Y)
                    - new Vector2(Map.TileSize.X / 2.0f, Map.TileSize.Y / 2.0f),
                new(WIDTH * Map.TileSize.X, HEIGHT * Map.TileSize.Y)
            );

            IsDirty = true;
        }

        public void Initialize()
        {
            Renderer.Initialize();
        }



        /// <summary>
        /// Flags the chunk for mesh regeneration.
        /// </summary>
        /// <returns>True if the chunk was flagged for regeneration; otherwise, false.</returns>
        public bool MarkDirty()
        {
            if (IsDirty)
                return false;

            return IsDirty = true;
        }


        /// <summary>
        /// Populates the chunk with tiles using a custom action.
        /// </summary>
        /// <param name="action">The custom action to populate the chunk.</param>
        public void Populate(Action<List<TileMapChunkSlice>, TileMapChunk> action)
        {
            action(Slices, this);
        }

        /// <summary>
        /// Performs post-generation actions for the chunk.
        /// </summary>
        public void PostGenerate()
        {
            alwaysOnTop.Clear();

            for (int s = 0; s < Slices.Count; s++)
            {
                var slice = Slices[s];
                for (int i = 0; i < slice.Tiles.Count; i++)
                {
                    if (slice.Tiles[i] is null)
                        continue;

                    slice.Tiles[i]!.PostGeneration();
                }
            }
        }
    }
}