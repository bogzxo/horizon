using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

using AutoVoxel;

using CodeKneading.Voxel;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;
using Horizon.Rendering;

using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.SDL;

namespace CodeKneading.Rendering;

internal static class ChunkDataGenerator
{
    public static Task GenerateTilesAsync(TileChunk chunk)
    {
        return Task.Factory.StartNew(() =>
        {
            for (int z = 0; z < TileChunk.SIZE; z++)
            {
                for (int x = 0; x < TileChunk.SIZE; x++)
                {
                    int yStart = (int)(Perlin.perlin((x + (chunk.ChunkPosition.X * TileChunk.SIZE)) * 0.01, 1, (z + (chunk.ChunkPosition.Y * TileChunk.SIZE)) * 0.01) * TileChunk.SIZE);

                    for (int y = yStart; y > 0; y--)
                    {
                        chunk.GroundTiles[x, y, z] = new Tile { Type = TileType.Ground };
                    }
                }
            }

            // add ourselves to mesh generation queue
            VoxelWorld.MeshGeneratorWorker.Enqueue(ChunkMeshGenerator.GenerateMeshAsync(chunk));
        });
    }
}

internal class WorldRenderer : IGameComponent
{
    public static readonly ConcurrentQueue<ChunkMeshData> MeshUploadQueue = [];

    internal FrameBufferObject FrameBuffer;

    private readonly VoxelWorld world;
    private SkyManager skyManager;
    private Horizon.OpenGL.Assets.Texture atlas;

    private WorldTechnique technique;
    private ShadowTechnique shadow;
    private BufferObject chunkPosBuffer;
    private VertexArrayObject buffer;
    private RenderRectangle target;
    private PostProcessingTechnique postProcessingTechnique;
    private uint drawCount = 0;
    private unsafe DrawArraysIndirectCommand* drawCommandsPtr;
    private unsafe Vector2D<int>* chunkPosPtr;

    public WorldRenderer(in VoxelWorld world)
    {
        Name = "WorldRenderer";
        Enabled = true;
        this.world = world;
    }

    public bool Enabled { get; set; }
    public string Name { get; set; }
    public Entity Parent { get; set; }

    public void Initialize()
    {
        skyManager = (Parent as VoxelWorld).Sky;

        technique = new WorldTechnique();
        shadow = new ShadowTechnique();
        atlas = GameEngine.Instance.ObjectManager.Textures.Create(new TextureDescription
        {
            Path = "content/textures/atlas_albedo.png",
            Definition = TextureDefinition.RgbaUnsignedByte
        }).Asset;

        CreateFaceInstanceBuffer();
        CreateBufferStreamingPointers();
        CreateChunkOffsetBufferAndPointer();
        UploadFaceDataAndLayoutData();

        FrameBuffer = GameEngine.Instance.ObjectManager.FrameBuffers.Create(new FrameBufferObjectDescription
        {
            Width = 4096,
            Height = 4096,
            Attachments = new() {
                { FramebufferAttachment.DepthAttachment, TextureDefinition.DepthComponent},
            },
        }).Asset;

        postProcessingTechnique = new PostProcessingTechnique(Parent);
        target = new RenderRectangle(postProcessingTechnique);
        target.Initialize();


        GameEngine.Instance.GL.ActiveTexture(TextureUnit.Texture0);
    }


    private unsafe void CreateChunkOffsetBufferAndPointer()
    {
        const uint chunkPosBufferSize = 4096 * 1024;
        chunkPosBuffer = GameEngine.Instance.ObjectManager.Buffers.Create(BufferObjectDescription.ShaderStorageBuffer with
        {
            IsStorageBuffer = true,
            Size = chunkPosBufferSize,
            StorageMasks =
                Silk.NET.OpenGL.BufferStorageMask.MapWriteBit
                | Silk.NET.OpenGL.BufferStorageMask.MapCoherentBit
                | Silk.NET.OpenGL.BufferStorageMask.MapPersistentBit
        }).Asset;
        chunkPosBuffer.BufferStorage(chunkPosBufferSize);
        chunkPosPtr = (Vector2D<int>*)chunkPosBuffer.MapBufferRange(chunkPosBufferSize, Silk.NET.OpenGL.MapBufferAccessMask.PersistentBit | Silk.NET.OpenGL.MapBufferAccessMask.CoherentBit | Silk.NET.OpenGL.MapBufferAccessMask.WriteBit);
    }

    private readonly struct VoxelVertex(in uint data)
    {
        // No need to bother with smaller types, we are forced to align to 4 bytes
        public readonly uint PackedData { get; init; } = data;

        /// <summary>
        /// Returns an array of packed vertices as bytes, where the first 2 bits are the X and Z coordinates, and the last two bits are the texture coordinates
        /// </summary>
        /// <returns>Packed quad vertices.</returns>
        public static VoxelVertex[] GetQuad()
        {
            return [
                new (0b01),
                new (0b11),
                new (0b10),
                new (0b00),
                ];
        }
    }

    private void UploadFaceDataAndLayoutData()
    {
        VoxelVertex[] quadVertices = VoxelVertex.GetQuad();
        buffer[VertexArrayBufferAttachmentType.ArrayBuffer].NamedBufferData(quadVertices);

        buffer.Bind();

        // Configure base instance buffer
        buffer[VertexArrayBufferAttachmentType.ArrayBuffer].Bind();
        buffer[VertexArrayBufferAttachmentType.ArrayBuffer].VertexAttributeIPointer(0, 1, Silk.NET.OpenGL.VertexAttribIType.UnsignedByte, sizeof(uint), 0); // Configure single byte compressed vertex data

        // Configure instanced buffer
        buffer[VertexArrayBufferAttachmentType.AdditionalBuffer0].Bind();
        buffer[VertexArrayBufferAttachmentType.AdditionalBuffer0].VertexAttributeIPointer(1, 1, Silk.NET.OpenGL.VertexAttribIType.UnsignedInt, sizeof(uint), 0);
        buffer[VertexArrayBufferAttachmentType.AdditionalBuffer0].VertexAttributeDivisor(1, 1);
        buffer[VertexArrayBufferAttachmentType.AdditionalBuffer0].NamedBufferData(1024 * 1024);
        buffer[VertexArrayBufferAttachmentType.AdditionalBuffer0].Unbind();
        buffer.Unbind();
    }

    private unsafe void CreateBufferStreamingPointers()
    {
        const uint indBufferSize = 1024;

        // Map a streaming pointer to modify the draw commands for realtime frusctum culling
        buffer[VertexArrayBufferAttachmentType.IndirectBuffer].BufferStorage(indBufferSize);
        drawCommandsPtr = (DrawArraysIndirectCommand*)buffer[VertexArrayBufferAttachmentType.IndirectBuffer].MapBufferRange(indBufferSize, Silk.NET.OpenGL.MapBufferAccessMask.CoherentBit |
            Silk.NET.OpenGL.MapBufferAccessMask.PersistentBit |
            Silk.NET.OpenGL.MapBufferAccessMask.WriteBit);
    }

    private void CreateFaceInstanceBuffer()
    {
        buffer = GameEngine.Instance.ObjectManager.VertexArrays.Create(new VertexArrayObjectDescription
        {
            Buffers = new() {
                { VertexArrayBufferAttachmentType.ArrayBuffer, BufferObjectDescription.ArrayBuffer },
                { VertexArrayBufferAttachmentType.AdditionalBuffer0, BufferObjectDescription.ArrayBuffer },
                { VertexArrayBufferAttachmentType.IndirectBuffer, GenerateIndirectBufferDescription() },
            }
        }).Asset;
    }

    private static BufferObjectDescription GenerateIndirectBufferDescription()
    {
        return BufferObjectDescription.IndirectBuffer with
        {
            IsStorageBuffer = true, // Configure the buffer as a storage buffer
            Size = 1024, // Map it as a KB
            StorageMasks = Silk.NET.OpenGL.BufferStorageMask.MapWriteBit // Allow writing 
            | Silk.NET.OpenGL.BufferStorageMask.MapCoherentBit // Allow simultaneous access
            | Silk.NET.OpenGL.BufferStorageMask.MapPersistentBit // Keep this buffer mapped
        };
    }


    public unsafe void Render(float dt, object? obj = null)
    {
        UploadMesh();

        if (drawCount == 0) return;

        DrawShadowPass();
        DrawRenderPass();
    }

    private unsafe void DrawRenderPass()
    {
        GameEngine.Instance.GL.Viewport(0, 0, (uint)GameEngine.Instance.WindowManager.WindowSize.X, (uint)GameEngine.Instance.WindowManager.WindowSize.Y);
        GameEngine.Instance.GL.Clear(Silk.NET.OpenGL.ClearBufferMask.DepthBufferBit | ClearBufferMask.ColorBufferBit);

        buffer.Bind();
        technique.Bind();

        FrameBuffer.BindAttachment(FramebufferAttachment.DepthAttachment, 0);
        technique.SetUniform("uTexSunDepth", 0);

        atlas.Bind(1);
        technique.SetUniform("uTexAlbedo", 1);


        technique.SetUniform("uSunDirection", skyManager.SunDirection);
        technique.SetUniform("uSunView", SkyManager.SunView);
        technique.SetUniform("uSunProjection", SkyManager.SunProj);
        technique.BindBuffer("b_chunkOffsets", chunkPosBuffer);

        GameEngine.Instance.GL.MultiDrawArraysIndirect(Silk.NET.OpenGL.PrimitiveType.TriangleFan, null, drawCount, 0);
        technique.Unbind();
    }

    private unsafe void DrawShadowPass()
    {
        FrameBuffer.Bind();
        FrameBuffer.Viewport();
        GameEngine.Instance.GL.Clear(Silk.NET.OpenGL.ClearBufferMask.DepthBufferBit);

        buffer.Bind();
        shadow.Bind();

        shadow.BindBuffer("b_chunkOffsets", chunkPosBuffer);

        GameEngine.Instance.GL.MultiDrawArraysIndirect(Silk.NET.OpenGL.PrimitiveType.TriangleFan, null, drawCount, 0);


        shadow.Unbind();
        buffer.Unbind();
        GameEngine.Instance.GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private int meshOffset = 0;

    private unsafe void UploadMesh()
    {
        if (MeshUploadQueue.TryDequeue(out ChunkMeshData result))
        {
            lock (buffer)
            {
                buffer[VertexArrayBufferAttachmentType.AdditionalBuffer0]
                    .NamedBufferSubData(result.Data.Span, meshOffset * sizeof(VoxelInstance));

                drawCommandsPtr[drawCount] = new DrawArraysIndirectCommand
                {
                    first = 0,
                    count = 4,
                    instanceCount = (uint)result.Data.Length,
                    baseInstance = (uint)meshOffset
                };
                chunkPosPtr[drawCount++] = result.ChunkPos;

                meshOffset += result.Data.Length;
            }
        }
    }

    public void UpdatePhysics(float dt)
    {
    }

    public void UpdateState(float dt)
    {
    }
}
