using System.Numerics;

using Horizon.Graphics;

namespace Horizon.Rendering.Spriting.Data
{
    /// <summary>A position and a texture coordinate, what a <see cref="Primitives.Mesh2D"/> is made of.</summary>
    public struct Vertex2D : IVertex
    {
        private Vector2 position;

        private Vector2 texCoords;

        private static readonly VertexLayoutDescription[] Layout =
        [
            VertexLayoutDescription.Float(0, 2, 0),
            VertexLayoutDescription.Float(1, 2, sizeof(float) * 2)
        ];

        public static ReadOnlySpan<VertexLayoutDescription> GetLayout() => Layout;

        /// <summary>The position of the vertex in 2D world space.</summary>
        public Vector2 Position
        {
            get => position;
            set => position = value;
        }

        /// <summary>The texture coordinates (UV) of the vertex.</summary>
        public Vector2 TexCoords
        {
            get => texCoords;
            set => texCoords = value;
        }

        public Vertex2D(Vector2 pos, Vector2 coords)
        {
            this.TexCoords = coords;
            this.Position = pos;
        }

        public Vertex2D(float x, float y, float uvX, float uvY)
        {
            this.TexCoords = new Vector2(uvX, uvY);
            this.Position = new Vector2(x, y);
        }

        /// <summary>The size of the vertex in bytes.</summary>
        public static uint SizeInBytes { get; } = (sizeof(float) * 4);
    }
}
