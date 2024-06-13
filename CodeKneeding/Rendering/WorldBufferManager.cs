using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using Bogz.Logging.Loggers;

using Box2D.NetStandard.Collision.Shapes;

using CodeKneading.Voxel;

using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;

using Silk.NET.Core.Native;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace CodeKneading.Rendering;

internal class WorldBufferManager : IInstantiable
{
    public static readonly BlockingCollection<ChunkMeshData> MeshUploadQueue = [];

    // ### Heap manager ###
    public ChunkletManager ChunkletManager { get; private set; }

    // ### Buffers ###
    public VertexArrayObject Buffer;

    public BufferObject CullIndirectBuffer { get; private set; }
    public BufferObject FinalIndirectBuffer { get; private set; }
    public BufferObject ChunkPositionsBuffer { get; private set; }
    public BufferObject ChunkPositionsIndexBuffer { get; private set; }

    // ### Constants ###
    public const int VertexBufferHeapSizeMB = 256;
    public const int ElementBufferHeapSizeMB = 128;
    public const int IndirectBufferHeapSizeMB = 32;
    public const int DataBufferHeapSizeMB = 64;

    // ### Dynamic properties ###
    public unsafe uint* realCommandsPtr;
    public uint MeshDrawCount { get; private set; }
    public uint RealDrawCount { get; set; }
    public bool ShouldDraw => MeshDrawCount > 0;


    public void Initialize()
    {
        CreateMeshBuffer();
        AllocateIndirectBuffers();
        CreateChunkOffsetBufferAndPointer();
        UploadFaceDataAndLayoutData();
        CreateHeapManager();
    }


    private void CreateHeapManager()
    {
        ChunkletManager = new ChunkletManager(
            Buffer[VertexArrayBufferAttachmentType.ArrayBuffer],
            Buffer[VertexArrayBufferAttachmentType.ElementBuffer],
            Buffer[VertexArrayBufferAttachmentType.IndirectBuffer],
            CullIndirectBuffer,
            ChunkPositionsBuffer
            );
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void RenderAll() => GameEngine.Instance.GL.MultiDrawElementsIndirect(PrimitiveType.Triangles, DrawElementsType.UnsignedShort, null, MeshDrawCount, (uint)sizeof(TileChunkDrawCommand));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Render() => GameEngine.Instance.GL.MultiDrawElementsIndirect(PrimitiveType.Triangles, DrawElementsType.UnsignedShort, (void*)sizeof(uint), RealDrawCount, (uint)sizeof(TileChunkDrawCommand));

    public unsafe void UploadMesh()
    {
        int counter = 0;
        while (MeshUploadQueue.TryTake(out ChunkMeshData result) && ++counter < 512)
        {
            if (result.VerticesMemory.Length == 0 && result.ElementsMemory.Length == 0)
            {
                counter--;
                continue;
            }

            if (!ChunkletManager.TryUploadMeshData(ref result))
            {
                ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Failed to upload mesh!");
                MeshUploadQueue.Add(result);
            }
            MeshDrawCount = (uint)ChunkletManager.DrawIndirectHeap.ActiveAllocations * 6;
        }
        if (counter > 0)
        {
            GameEngine.Instance.GL.MemoryBarrier(MemoryBarrierMask.AllBarrierBits);
        }
    }

    private unsafe void CreateChunkOffsetBufferAndPointer()
    {
        const uint chunkPosBufferSize = 1024 * 1024 * DataBufferHeapSizeMB;

        if (GameEngine.Instance.ObjectManager.Buffers.TryCreate(BufferObjectDescription.ShaderStorageBuffer with
        {
            Size = chunkPosBufferSize,
        }, out var result))
        {
            ChunkPositionsBuffer = result.Asset;
            ChunkPositionsBuffer.NamedBufferData(chunkPosBufferSize);
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
        }

        // index buffer
        if (GameEngine.Instance.ObjectManager.Buffers.TryCreate(BufferObjectDescription.ShaderStorageBuffer with
        {
            Size = chunkPosBufferSize,
        }, out var indexRes))
        {
            ChunkPositionsIndexBuffer = indexRes.Asset;
            ChunkPositionsIndexBuffer.NamedBufferData(chunkPosBufferSize);
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
        }
    }

    private unsafe void UploadFaceDataAndLayoutData()
    {
        Buffer.Bind();

        // Configure base instance Buffer
        Buffer[VertexArrayBufferAttachmentType.ArrayBuffer].Bind();
        // Configure single byte compressed vertex data
        Buffer[VertexArrayBufferAttachmentType.ArrayBuffer].VertexAttributeIPointer(0, 1, Silk.NET.OpenGL.VertexAttribIType.UnsignedInt, (uint)sizeof(VoxelVertex), 0);

        Buffer[VertexArrayBufferAttachmentType.ElementBuffer].Bind();

        Buffer.Unbind();
    }

    private unsafe void AllocateIndirectBuffers()
    {
        Buffer[VertexArrayBufferAttachmentType.IndirectBuffer]
            .NamedBufferData(Buffer[VertexArrayBufferAttachmentType.IndirectBuffer].Size);
        CullIndirectBuffer.NamedBufferData(CullIndirectBuffer.Size);
    }

    private void CreateMeshBuffer()
    {
        if (GameEngine.Instance.ObjectManager.VertexArrays.TryCreate(new VertexArrayObjectDescription
        {
            Buffers = new() {
                { VertexArrayBufferAttachmentType.ArrayBuffer, BufferObjectDescription.ArrayBuffer with { Size = 1024 * 1024 * VertexBufferHeapSizeMB } },
                { VertexArrayBufferAttachmentType.ElementBuffer, BufferObjectDescription.ElementArrayBuffer with { Size = 1024 * 1024 * ElementBufferHeapSizeMB} },
                { VertexArrayBufferAttachmentType.IndirectBuffer, GenerateIndirectBufferDescription() },
            }
        }, out var result))
        {
            Buffer = result.Asset;

            // reserve space
            Buffer[VertexArrayBufferAttachmentType.ArrayBuffer]
                .NamedBufferData(Buffer[VertexArrayBufferAttachmentType.ArrayBuffer].Size);
            Buffer[VertexArrayBufferAttachmentType.ElementBuffer]
                .NamedBufferData(Buffer[VertexArrayBufferAttachmentType.ElementBuffer].Size);
            Buffer[VertexArrayBufferAttachmentType.IndirectBuffer]
                .NamedBufferData(Buffer[VertexArrayBufferAttachmentType.IndirectBuffer].Size);

            if (GameEngine.Instance.ObjectManager.Buffers.TryCreate(
                GenerateIndirectBufferDescription(),
                out var indResult))
            {
                CullIndirectBuffer = indResult.Asset;
                CullIndirectBuffer.NamedBufferData(CullIndirectBuffer.Size);
            }
            else
            {
                Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, indResult.Message);
            }
            if (GameEngine.Instance.ObjectManager.Buffers.TryCreate(
               GenerateIndirectBufferDescription() with
               {
                   IsStorageBuffer = true,
                   StorageMasks = BufferStorageMask.MapCoherentBit | BufferStorageMask.MapReadBit | BufferStorageMask.MapPersistentBit | BufferStorageMask.DynamicStorageBit
               },
               out var realIndResult))
            {
                FinalIndirectBuffer = realIndResult.Asset;
                FinalIndirectBuffer.NamedBufferData(FinalIndirectBuffer.Size + 8);

                unsafe
                {
                    realCommandsPtr = (uint*)FinalIndirectBuffer.MapBufferRange(sizeof(uint), MapBufferAccessMask.ReadBit | MapBufferAccessMask.CoherentBit | MapBufferAccessMask.PersistentBit);
                }
            }
            else
            {
                Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, indResult.Message);
            }

        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
        }
    }

    private static BufferObjectDescription GenerateIndirectBufferDescription()
    {
        return BufferObjectDescription.IndirectBuffer with
        {
            Size = 1024 * 1024 * IndirectBufferHeapSizeMB + sizeof(uint),
        };
    }
}