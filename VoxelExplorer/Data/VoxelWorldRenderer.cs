using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;

using Bogz.Logging.Loggers;

using Horizon.Core;
using Horizon.Core.Data;
using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;

using ImGuiNET;

using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;
using Silk.NET.SDL;

namespace VoxelExplorer.Data;

internal class VoxelWorldRenderer : Entity
{
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct VoxelVertex(Vector3 position, Vector2 uv)
    {
        [VertexLayout(0, Silk.NET.OpenGL.VertexAttribPointerType.Float)]
        private readonly Vector3 Position = position;

        [VertexLayout(1, Silk.NET.OpenGL.VertexAttribPointerType.Float)]
        private readonly Vector2 UV = uv;
    }

    internal readonly struct MeshUploadInfo
    {
        public readonly int Index { get; init; }
        public readonly uint BottomCount { get; init; }
        public readonly uint TopCount { get; init; }
        public readonly uint LeftCount { get; init; }
        public readonly uint RightCount { get; init; }
        public readonly uint FrontCount { get; init; }
        public readonly uint BackCount { get; init; }
        public readonly Memory<VoxelInstanceData> Array { get; init; }
    }

    private ChunkTechnique WorldTechnique;
    private unsafe ChunkOffset* chunkOffsetPtr;
    private VertexArrayObject QuadBuffer;

    private Horizon.OpenGL.Assets.Texture texture;
    private unsafe DrawArraysIndirectCommand* indirectBufferPtr;

    private BufferObject instanceBuffer, chunkOffsetBuffer;
    private uint instanceCount;

    public VoxelWorld World { get; init; }
    public static readonly VoxelMeshGenerator VoxelMeshGenerator = new();

    public VoxelWorldRenderer(in VoxelWorld world)
    {
        World = world;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChunkOffset
    {
        public int posX;
        public int posY;
        public int face;
    }

    public override unsafe void Initialize()
    {
        base.Initialize();

        texture = GameEngine.Instance.ObjectManager.Textures.CreateOrGet("dirt_texture", new TextureDescription
        {
            Paths = "content/textures/default_dirt.png",
            Definition = TextureDefinition.RgbaUnsignedByte
        });


        WorldTechnique = new ChunkTechnique();

        VoxelMeshGenerator.StartTask();

        SetupBaseQuadBuffer();

        GameEngine.Instance.GL.BindVertexArray(QuadBuffer.Handle);
        QuadBuffer.Buffers[VertexArrayBufferAttachmentType.AdditionalBuffer0].Bind();
        QuadBuffer.Buffers[VertexArrayBufferAttachmentType.AdditionalBuffer0].VertexAttributeIPointer(2, 1, VertexAttribIType.Int, 4, 0);
        GameEngine.Instance.GL.VertexAttribDivisor(2, 1);
        QuadBuffer.Buffers[VertexArrayBufferAttachmentType.AdditionalBuffer0].Unbind();
        GameEngine.Instance.GL.BindVertexArray(0);

        instanceBuffer = QuadBuffer.Buffers[VertexArrayBufferAttachmentType.AdditionalBuffer0];
        instanceBuffer.NamedBufferData(1024 * 1024 * 1024);
    }

    private unsafe void SetupBaseQuadBuffer()
    {
        chunkOffsetBuffer = GameEngine.Instance.ObjectManager.Buffers.TryCreate(BufferObjectDescription.ShaderStorageBuffer with
        {
            IsStorageBuffer = true,
            Size = 1024 * 1024, // : TODO: eish
            StorageMasks = BufferStorageMask.MapWriteBit | BufferStorageMask.MapCoherentBit | BufferStorageMask.MapPersistentBit
        }).Asset;
        chunkOffsetBuffer.BufferStorage(1024 * 1024); // : TODO: eish
        chunkOffsetPtr = (ChunkOffset*)chunkOffsetBuffer
            .MapBufferRange(1024 * 1024, // : TODO: eish
            MapBufferAccessMask.PersistentBit | MapBufferAccessMask.CoherentBit | MapBufferAccessMask.WriteBit
            );


        QuadBuffer = GameEngine.Instance.ObjectManager.VertexArrays.TryCreate(new VertexArrayObjectDescription
        {
            Buffers = new()
            {
                { VertexArrayBufferAttachmentType.ArrayBuffer, BufferObjectDescription.ArrayBuffer },
                { VertexArrayBufferAttachmentType.AdditionalBuffer0, BufferObjectDescription.ArrayBuffer },
                {VertexArrayBufferAttachmentType.IndirectBuffer, BufferObjectDescription.IndirectBuffer with {
                    IsStorageBuffer = true,
                    Size = 1024 * 1024 * 6, // : TODO: eish
                    StorageMasks = BufferStorageMask.MapWriteBit | BufferStorageMask.MapPersistentBit | BufferStorageMask.MapCoherentBit
                } }
            }
        }).Asset;

        // TODO: eish
        QuadBuffer[VertexArrayBufferAttachmentType.IndirectBuffer].BufferStorage(1024 * 1024 * 6); // : TODO: eish
        indirectBufferPtr = (DrawArraysIndirectCommand*)QuadBuffer[VertexArrayBufferAttachmentType.IndirectBuffer]
            .MapBufferRange(1024 * 1024 * 6, // : TODO: eish
             MapBufferAccessMask.PersistentBit | MapBufferAccessMask.WriteBit | MapBufferAccessMask.CoherentBit
            );

        var quad_vertices = new VoxelVertex[] {
            new(new Vector3(0, 0, 1), new Vector2(0, 1)),  // Top left
            new(new Vector3(1, 0, 1), new Vector2(1, 1)), // Top right
            new(new Vector3(1, 0, 0), new Vector2(1, 0)), // Bottom right
            new(new Vector3(0, 0, 0), new Vector2(0, 0)), // Bottom left
        };


        GameEngine.Instance.GL.BindVertexArray(QuadBuffer.Handle);
        QuadBuffer.Buffers[VertexArrayBufferAttachmentType.ArrayBuffer].Bind();
        QuadBuffer.Buffers[VertexArrayBufferAttachmentType.ArrayBuffer].SetLayout<VoxelVertex>();
        QuadBuffer.Buffers[VertexArrayBufferAttachmentType.ArrayBuffer].Unbind();

        QuadBuffer.Buffers[VertexArrayBufferAttachmentType.ArrayBuffer].NamedBufferData(quad_vertices);

        GameEngine.Instance.GL.BindVertexArray(0);
    }

    public override unsafe void Render(float dt, object? obj = null)
    {
        base.Render(dt, obj);

        //if (ImGui.Begin("chunk offsets"))
        //{
        //    for (int i = 0; i < drawCount; i++)
        //    {
        //        for (int j = 0; j < 6; j++)
        //        {
        //            ImGui.DragInt("baseInstance", ref indirectBufferPtr[i * 6 + j].baseInstance);

        //            ImGui.Separator();
        //        }
        //    }

        //    ImGui.End();
        //}

        UpdateChunkInfo();
        WorldTechnique.Bind();
        WorldTechnique.BindBuffer("b_chunkOffsets", chunkOffsetBuffer);
        GameEngine.Instance.GL.ActiveTexture(TextureUnit.Texture0);
        GameEngine.Instance.GL.BindTextureUnit(0, texture.Handle);
        WorldTechnique.SetUniform("uTexture", 0);
        GameEngine.Instance.GL.BindVertexArray(QuadBuffer.Handle);
        GameEngine.Instance.GL.MultiDrawArraysIndirect(PrimitiveType.TriangleFan, null, (uint)drawCount, 0);
        WorldTechnique.Unbind();
    }

    int nextChunkOffset = 0, drawCount = 0;
    internal static readonly ConcurrentQueue<MeshUploadInfo> MeshUploadQueue = new();

    private unsafe void UpdateChunkInfo()
    {
        if (!MeshUploadQueue.IsEmpty && MeshUploadQueue.TryDequeue(out var chunkData))
        {
            instanceBuffer.NamedBufferSubData<VoxelInstanceData>(chunkData.Array.Span, nextChunkOffset * sizeof(VoxelInstanceData));

            void updateBuffers(in uint instanceCount, in int face)
            {
                indirectBufferPtr[chunkData.Index * 6 + face] = new DrawArraysIndirectCommand
                {
                    baseInstance = 0,
                    count = 4,
                    first = 0,
                    instanceCount = instanceCount
                };

                Console.WriteLine($"Face[{face}] writing to indirect buffer {chunkData.Index * 6 + face}");

                chunkOffsetPtr[chunkData.Index * 6 + face] = new ChunkOffset
                {
                    posX = chunkData.Index % VoxelWorld.WIDTH * Chunk.SIZE,
                    posY = chunkData.Index / VoxelWorld.WIDTH * Chunk.SIZE,
                    face = face
                };
                drawCount++;
                nextChunkOffset += (int)instanceCount;
            }

            updateBuffers(chunkData.BottomCount, 0);
            updateBuffers(chunkData.TopCount, 1);
            updateBuffers(chunkData.FrontCount, 2);
            updateBuffers(chunkData.BackCount, 3);
            updateBuffers(chunkData.RightCount, 4);
            updateBuffers(chunkData.LeftCount, 5);
        }
    }
}