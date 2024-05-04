using System;
using System.Numerics;
using System.Reflection;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Engine;

using Silk.NET.OpenGL;

namespace Horizon.Rendering;

public abstract partial class Tiling<TTextureID>
{
    public class TileMapChunkManager : IGameComponent
    {
        public string Name { get; set; }
        public Entity Parent { get; set; }
        public bool Enabled { get; set; }
        public TileMap Map { get; private set; }
        public TileMapChunk[] Chunks { get; private set; }

        private float _secondTimer = 1.1f;
        private bool _drawParallax = false;

        public TileMapChunkManager(in TileMap map)
        {
            Map = map;

            Chunks = new TileMapChunk[Map.Width * Map.Height];

            for (int i = 0; i < Map.Width * Map.Height; i++)
                Chunks[i] = new TileMapChunk(Map, new Vector2(i % Map.Width, i / Map.Width));
        }

        public void Initialize()
        {
            for (int i = 0; i < Map.Width * Map.Height; i++)
                Chunks[i].Initialize();
        }

        public TileMapChunk? this[int index]
        {
            get
            {
                if (index < 0 || index > Chunks.Length - 1)
                    return null;

                return Chunks[index];
            }
        }

        public TileMapChunk? this[int x, int y]
        {
            get
            {
                int index = x + y * Map.Width;

                if (index < 0 || index > Chunks.Length - 1)
                    return null;

                return Chunks[index];
            }
        }

        public void UpdateState(float dt)
        {
            _secondTimer += dt;
            if (_secondTimer >= 1)
            {
                _secondTimer = 0;
                // slow
            }
        }

        public void UpdatePhysics(float dt)
        { }

        public void Render(float dt, object? obj = null)
        {
            // not used
        }

        public void RenderChunks(in float dt, in int startSlice, in int endSlice)
        {
            for (int chunkIndex = 0; chunkIndex < Map.Width * Map.Height; chunkIndex++)
            {
                for (int sliceIndex = startSlice; sliceIndex < endSlice; sliceIndex++)
                {
                    Chunks[chunkIndex]?.Renderer.DrawSliceAtIndex(sliceIndex, dt);
                }
            }
        }

        public void RenderChunk(in float dt, in int index)
        {
            for (int chunkIndex = 0; chunkIndex < Map.Width * Map.Height; chunkIndex++)
            {
                Chunks[chunkIndex]?.Renderer.DrawSliceAtIndex(index, dt);
            }
        }

        /// <summary>
        /// This method accepts a populator action that is expected to fill the tile[].
        /// </summary>
        /// <param name="action">The populator action</param>
        public void PopulateTiles(Action<List<TileMapChunkSlice>, TileMapChunk> action)
        {
            for (int i = 0; i < Map.Width * Map.Height; i++)
                Chunks[i].Populate(action);

            PostGenerateTiles();
        }

        internal void PostGenerateTiles()
        {
            for (int i = 0; i < Map.Width * Map.Height; i++)
                Chunks[i].PostGenerate();
        }
    }
}