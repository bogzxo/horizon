using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Core;
using Horizon.Core.Data;
using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;

using Silk.NET.OpenGL;

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

    private ChunkTechnique WorldTechnique;
    private VertexArrayObject QuadBuffer;
    private BufferObject instanceBuffer, chunkOffsetBuffer;
    private uint instanceCount;

    public VoxelWorld World { get; init; }

    public VoxelWorldRenderer(in VoxelWorld world)
    {
        World = world;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChunkOffset
    {
        public uint posX;
        public uint posY;
        private readonly uint spacer0, spacer1;
    }

    public override void Initialize()
    {
        base.Initialize();

        WorldTechnique = new ChunkTechnique();

        SetupBaseQuadBuffer();

        GameEngine.Instance.GL.BindVertexArray(QuadBuffer.Handle);
        QuadBuffer.Buffers[VertexArrayBufferAttachmentType.AdditionalBuffer0].Bind();
        QuadBuffer.Buffers[VertexArrayBufferAttachmentType.AdditionalBuffer0].VertexAttributeIPointer(2, 1, VertexAttribIType.Int, 4, 0);
        GameEngine.Instance.GL.VertexAttribDivisor(2, 1);
        QuadBuffer.Buffers[VertexArrayBufferAttachmentType.AdditionalBuffer0].Unbind();
        GameEngine.Instance.GL.BindVertexArray(0);

        instanceBuffer = QuadBuffer.Buffers[VertexArrayBufferAttachmentType.AdditionalBuffer0];
        int offset = 0, indOffset = 0;

        instanceBuffer.NamedBufferData(1024 * 1024);
        List<DrawArraysIndirectCommand> indirectCommands = [];
        List<ChunkOffset> chunkOffsets = [];

        for (int i = 0; i < World.Chunks.Length; i++)
        {
            var chunk = World.Chunks[i];
            chunkOffsets.Add(new ChunkOffset
            {
                posX = (uint)(i % VoxelWorld.WIDTH) * Chunk.SIZE,
                posY = (uint)(i / VoxelWorld.WIDTH) * Chunk.SIZE
            });

            var chunkData = World.Chunks[i].DataProvider;
            var instance_data = new List<VoxelInstanceData>();

            //instance_data.Add(new VoxelInstanceData(new Vector3(0), 0));
            //instance_data.Add(new VoxelInstanceData(new Vector3(0), 1));
            //instance_data.Add(new VoxelInstanceData(new Vector3(0), 2));
            //instance_data.Add(new VoxelInstanceData(new Vector3(0), 3));
            //instance_data.Add(new VoxelInstanceData(new Vector3(0), 4));
            //instance_data.Add(new VoxelInstanceData(new Vector3(0), 5));

            for (int x = 0; x < Chunk.SIZE; x++)
            {
                for (int z = 0; z < Chunk.SIZE; z++)
                {
                    for (int y = 0; y < Chunk.SIZE; y++)
                    {
                        if (chunkData[x, y - 1, z].DataPack0 == 0)
                        {
                            instance_data.Add(VoxelInstanceData.Encode(x, y, z, 0));
                        }
                        if (chunkData[x, y + 1, z].DataPack0 == 0)
                        {
                            instance_data.Add(VoxelInstanceData.Encode(x, y, z, 1));
                        }
                        if (chunkData[x + 1, y, z].DataPack0 == 0)
                        {
                            instance_data.Add(VoxelInstanceData.Encode(x, y, z, 2));
                        }
                        if (chunkData[x - 1, y, z].DataPack0 == 0)
                        {
                            instance_data.Add(VoxelInstanceData.Encode(x, y, z, 3));
                        }
                        if (chunkData[x, y, z + 1].DataPack0 == 0)
                        {
                            instance_data.Add(VoxelInstanceData.Encode(x, y, z, 4));
                        }
                        if (chunkData[x, y, z - 1].DataPack0 == 0)
                        {
                            instance_data.Add(VoxelInstanceData.Encode(x, y, z, 5));
                        }
                    }
                }
            }

            indirectCommands.Add(new DrawArraysIndirectCommand
            {
                baseInstance = 0,
                count = 4,
                first = (uint)indOffset,
                instanceCount = (uint)instance_data.Count
            });
            unsafe
            {
                instanceBuffer.NamedBufferSubData<VoxelInstanceData>(CollectionsMarshal.AsSpan(instance_data), offset);
                offset += instance_data.Count * sizeof(VoxelInstanceData);
            }
            indOffset += instance_data.Count;
            chunk.BufferSize = instance_data.Count;
        }
        instanceCount = (uint)offset;

        chunkOffsetBuffer.NamedBufferData<ChunkOffset>(CollectionsMarshal.AsSpan(chunkOffsets));
        QuadBuffer.Buffers[VertexArrayBufferAttachmentType.IndirectBuffer].NamedBufferData<DrawArraysIndirectCommand>(CollectionsMarshal.AsSpan<DrawArraysIndirectCommand>(indirectCommands));
    }

    private unsafe void SetupBaseQuadBuffer()
    {
        chunkOffsetBuffer = GameEngine.Instance.ObjectManager.Buffers.Create(BufferObjectDescription.ShaderStorageBuffer).Asset;

        QuadBuffer = GameEngine.Instance.ObjectManager.VertexArrays.Create(new VertexArrayObjectDescription
        {
            Buffers = new()
            {
                { VertexArrayBufferAttachmentType.ArrayBuffer, BufferObjectDescription.ArrayBuffer },
                { VertexArrayBufferAttachmentType.AdditionalBuffer0, BufferObjectDescription.ArrayBuffer },
                {VertexArrayBufferAttachmentType.IndirectBuffer, BufferObjectDescription.IndirectBuffer }
            }
        }).Asset;

        var quad_vertices = new VoxelVertex[] {
            new(new Vector3(0, 0, 0), new Vector2(0, 0)),
            new(new Vector3(1, 0, 0), new Vector2(1, 0)),
            new(new Vector3(0, 0, 1), new Vector2(0, 1)),
            new(new Vector3(1, 0, 1), new Vector2(1, 1))
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
        WorldTechnique.Bind();
        WorldTechnique.BindBuffer("b_chunkOffsets", chunkOffsetBuffer);
        GameEngine.Instance.GL.BindVertexArray(QuadBuffer.Handle);
        GameEngine.Instance.GL.MultiDrawArraysIndirect(PrimitiveType.TriangleFan, null, (uint)World.Chunks.Length, 0);
        WorldTechnique.Unbind();
    }
}