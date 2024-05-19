using System.Numerics;
using System.Runtime.InteropServices;

using AutoVoxel.Data;
using AutoVoxel.Data.Chunks;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Core.Data;
using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;
using Horizon.Rendering;

using Silk.NET.OpenGL;

namespace AutoVoxel.Rendering;

public readonly struct MeshUploadInfo
{
    public readonly ReadOnlyMemory<ChunkVertex> Data { get; init; }
    public readonly uint[] SliceOffsets { get; init; }
    public readonly int Index { get; init; }
    public readonly int OffsetX { get; init; }
    public readonly int OffsetZ { get; init; }
}

public class ChunkRenderer : IGameComponent
{
    private const string UNIFORM_ALBEDO = "uTexAlbedo";
    private const string UNIFORM_NORMAL = "uTexNormal";
    private const string UNIFORM_SPECULAR = "uTexSpecular";
    private readonly ChunkManager manager;
    private unsafe DrawArraysIndirectCommand* indirectBufferPtr;
    private float iTime = 0.0f;

    public Material Material { get; set; }
    public Technique Technique { get; set; }

    public bool Enabled { get; set; }
    public string Name { get; set; }
    public Entity Parent { get; set; }

    private unsafe ChunkOffset* chunkOffsetPtr;
    private VertexArrayObject QuadBuffer;
    private uint instanceCount = 0;
    private BufferObject chunkOffsetBuffer;

    public static readonly Queue<MeshUploadInfo> MeshUploadQueue = new();

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct VoxelVertex(Vector3 position, Vector2 uv)
    {
        [VertexLayout(0, Silk.NET.OpenGL.VertexAttribPointerType.Float)]
        private readonly Vector3 Position = position;

        [VertexLayout(1, Silk.NET.OpenGL.VertexAttribPointerType.Float)]
        private readonly Vector2 UV = uv;
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct ChunkOffset
    {
        public int posX;
        public int posY;
        public int posZ;
    }

    public ChunkRenderer(in ChunkManager manager)
    {
        this.manager = manager;
    }

    public void Initialize()
    {
        Material = MaterialFactory.Create("content/atlas", "atlas");
        Technique = new ChunkTechnique();

        // enable depth testing and culling
        GameEngine.Instance.GL.Enable(Silk.NET.OpenGL.EnableCap.DepthTest);
        GameEngine.Instance.GL.Disable(Silk.NET.OpenGL.EnableCap.CullFace);
        GameEngine.Instance.GL.Enable(Silk.NET.OpenGL.EnableCap.Blend);

        // Generate buffers
        GenerateBuffers();
    }


    private unsafe void GenerateBuffers()
    {
        chunkOffsetBuffer = GameEngine.Instance.ObjectManager.Buffers.Create(BufferObjectDescription.ShaderStorageBuffer with
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

        this.QuadBuffer = GameEngine.Instance.ObjectManager.VertexArrays.Create(new VertexArrayObjectDescription
        {
            Buffers = new()
            {
                { VertexArrayBufferAttachmentType.ArrayBuffer, BufferObjectDescription.ArrayBuffer },
                { VertexArrayBufferAttachmentType.AdditionalBuffer0, BufferObjectDescription.ArrayBuffer },
                {VertexArrayBufferAttachmentType.IndirectBuffer, BufferObjectDescription.IndirectBuffer with {
                    IsStorageBuffer = true,
                    Size = 1024 * 1024 * 512, // TODO: Calculate memory pre-allocation footprint dynamically
                    StorageMasks = BufferStorageMask.MapWriteBit | BufferStorageMask.MapPersistentBit | BufferStorageMask.MapCoherentBit
                } }
            }
        }).Asset;

        QuadBuffer[VertexArrayBufferAttachmentType.IndirectBuffer].BufferStorage(1024 * 1024 * 512); // TODO: Same as above
        indirectBufferPtr = (DrawArraysIndirectCommand*)QuadBuffer[VertexArrayBufferAttachmentType.IndirectBuffer]
            .MapBufferRange(1024 * 1024 * 512, // TODO: Same as above
             MapBufferAccessMask.PersistentBit | MapBufferAccessMask.WriteBit | MapBufferAccessMask.CoherentBit
            );

        var quad_vertices = new VoxelVertex[] {
            new(new Vector3(0, 0, 1), new Vector2(0, 1)),  // Top left
            new(new Vector3(1, 0, 1), new Vector2(1, 1)), // Top right
            new(new Vector3(1, 0, 0), new Vector2(1, 0)), // Bottom right
            new(new Vector3(0, 0, 0), new Vector2(0, 0)), // Bottom left
        };



        QuadBuffer.Buffers[VertexArrayBufferAttachmentType.AdditionalBuffer0].NamedBufferData(1024 * 1024 * 512);

        QuadBuffer.Bind();
        QuadBuffer.Buffers[VertexArrayBufferAttachmentType.ArrayBuffer].Bind();
        QuadBuffer.Buffers[VertexArrayBufferAttachmentType.ArrayBuffer].SetLayout<VoxelVertex>();
        QuadBuffer.Buffers[VertexArrayBufferAttachmentType.ArrayBuffer].Unbind();

        QuadBuffer.Buffers[VertexArrayBufferAttachmentType.ArrayBuffer].NamedBufferData(quad_vertices);

        GameEngine.Instance.GL.BindVertexArray(QuadBuffer.Handle);
        QuadBuffer.Buffers[VertexArrayBufferAttachmentType.AdditionalBuffer0].Bind();
        QuadBuffer.Buffers[VertexArrayBufferAttachmentType.AdditionalBuffer0].VertexAttributeIPointer(2, 1, VertexAttribIType.Int, 4, 0);
        GameEngine.Instance.GL.VertexAttribDivisor(2, 1);
        QuadBuffer.Buffers[VertexArrayBufferAttachmentType.AdditionalBuffer0].Unbind();
        QuadBuffer.Unbind();
    }

    public unsafe void Render(float dt, object? obj = null)
    {
        iTime += dt;

        // Try to upload a new mesh this frame
        UploadChunks();

        // make sure to clear the color and depth buffers
        GameEngine.Instance.GL.Clear(Silk.NET.OpenGL.ClearBufferMask.DepthBufferBit | Silk.NET.OpenGL.ClearBufferMask.ColorBufferBit);

        Technique.Bind();
        Technique.BindBuffer("b_chunkOffsets", chunkOffsetBuffer);
        BindMaterialAttachments();

        QuadBuffer.Bind();
        GameEngine.Instance.GL.MultiDrawArraysIndirect(PrimitiveType.TriangleFan, null, instanceCount, 0);
        QuadBuffer.Unbind();

        Technique.Unbind();
    }

    private uint nextChunkOffset = 0;
    private unsafe void UploadChunks()
    {
        if (MeshUploadQueue.Count == 0) return;

        if (MeshUploadQueue.TryDequeue(out MeshUploadInfo info))
        {
            QuadBuffer[VertexArrayBufferAttachmentType.AdditionalBuffer0].NamedBufferSubData<ChunkVertex>(info.Data.Span, (int)(nextChunkOffset * sizeof(ChunkVertex)));
             
            for (int i = 0; i < info.SliceOffsets.Length; i++)
            {
                indirectBufferPtr[(info.Index * Chunk.SLICES) + i] = new DrawArraysIndirectCommand
                {
                    count = 4,
                    instanceCount = (uint)info.SliceOffsets[i],
                    first = 0,
                    baseInstance = (uint)nextChunkOffset,
                };

                nextChunkOffset += info.SliceOffsets[i];

                chunkOffsetPtr[(info.Index * Chunk.SLICES) + i] = new ChunkOffset
                {
                    posX = info.OffsetX,
                    posY = i * Chunk.DEPTH,
                    posZ = info.OffsetZ,
                };
            }
            instanceCount += (uint)info.Data.Length;
        }
    }

    public void UpdateState(float dt)
    { }

    public void UpdatePhysics(float dt)
    { }

    protected void BindMaterialAttachments()
    {
        Material.BindAttachment(MaterialAttachment.Albedo, 0);
        Technique.SetUniform(UNIFORM_ALBEDO, 0);

        Material.BindAttachment(MaterialAttachment.Normal, 1);
        Technique.SetUniform(UNIFORM_NORMAL, 1);

        Material.BindAttachment(MaterialAttachment.Specular, 2);
        Technique.SetUniform(UNIFORM_SPECULAR, 2);
    }
}