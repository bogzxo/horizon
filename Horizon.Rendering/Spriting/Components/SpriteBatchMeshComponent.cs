using System.Numerics;
using System.Runtime.InteropServices;
using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;
using Horizon.Rendering.Spriting.Data;

using Silk.NET.OpenGL;

namespace Horizon.Rendering.Spriting.Components;

public class SpriteBatchMesh : GameObject
{
    private const string UNIFORM_SINGLE_BUFFER_SIZE = "uSingleFrameSize";
    private const string UNIFORM_CAMERA_PROJ_MATRIX = "uCameraProjection";
    private const string UNIFORM_CAMERA_VIEW_MATRIX = "uCameraView";
    private const string UNIFORM_MODEL_MATRIX = "uModel";

    // TODO: we'll get back to memory alignment later.
    // edit: still havent
    // edit 03/09/25 still havent
    // edit 27/07/26 still havent
    // edit 01/09/26 we are now using that bit :   )
    [StructLayout(LayoutKind.Sequential)]
    private struct SpriteData
    {
        public Matrix4x4 modelMatrix;
        public Vector2 spriteOffset;
        public uint frameIndex;
        public uint spriteSpan;
    }

    private readonly SpriteSheet sheet;

    public Technique Shader { get; init; }
    public VertexBufferObject Buffer { get; init; }

    // removed 'init' so we can overwrite this when resizing without c# yelling at us
    public BufferObject StorageBuffer { get; private set; }

    // triple buffering state so the cpu doesn't completely gap the gpu
    private const int NUM_BUFFERS = 3;
    private int currentFrameIndex = 0;
    private nint[] fences = new nint[NUM_BUFFERS];
    private int maxSpritesPerFrame = 0; // gets set in ResizeBuffer

    // eish
    private unsafe SpriteData* dataPtr;

    public uint ElementCount { get; private set; }

    public unsafe SpriteBatchMesh(SpriteSheet sheet, Technique shader)
        : base()
    {
        this.Shader = shader;
        this.sheet = sheet;

        if (Engine.ObjectManager.VertexArrays.TryCreate(
            VertexArrayObjectDescription.VertexBuffer,
            out var result))
        {
            Buffer = new VertexBufferObject(result.Asset);
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
        }

        SetVboLayout();
        GenerateMesh();

        // 10k sprites in there just to be safe at the start
        ResizeBuffer(10000);
    }

    private void SetVboLayout()
    {
        Buffer.Bind();
        Buffer.VertexBuffer.Bind();
        Buffer.VertexBuffer.SetLayout<Vertex2D>();
        Buffer.VertexBuffer.Unbind();
        Buffer.Unbind();
    }

    private void GenerateMesh()
    {
        float size = 1.0f;

        Vertex2D[] vertices = new Vertex2D[]
        {
            new Vertex2D(-size / 2.0f, -size / 2.0f, 0, 1),
            new Vertex2D(size / 2.0f, -size / 2.0f, 1, 1),
            new Vertex2D(size / 2.0f, size / 2.0f, 1, 0),
            new Vertex2D(-size / 2.0f, size / 2.0f, 0, 0),
        };
        uint[] elements = new uint[] { 0, 1, 2, 0, 2, 3 };

        Buffer.VertexBuffer.BufferData(vertices);
        Buffer.ElementBuffer.BufferData(elements);
    }

    public override void Render(float dt, object? obj = null)
    {
        throw new Exception("Please only draw a SpriteBatchMesh through a SpriteBatch");
    }

    public unsafe void Draw(Matrix4x4 globalModel, in ReadOnlySpan<Sprite> sprites, Camera engineActiveCamera)
    {
        if (!Enabled) return;

        // shit too many sprites, halt the presses and resize
        if (sprites.Length > maxSpritesPerFrame)
        {
            ResizeBuffer(sprites.Length);
        }

        // make sure gpu is actually done with this chunk of memory before we oozing all over it mmmhhhppphhh
        if (fences[currentFrameIndex] != 0)
        {
            Engine.GL.ClientWaitSync(fences[currentFrameIndex], SyncObjectMask.Bit, 1_000_000_000);
            Engine.GL.DeleteSync(fences[currentFrameIndex]);
            fences[currentFrameIndex] = 0;
        }

        BindAndSetUniforms(engineActiveCamera, globalModel);

        Shader.BindBuffer("spriteData", StorageBuffer);
        Buffer.Bind();
        Buffer.VertexBuffer.Bind();
        Buffer.ElementBuffer.Bind();

        // stencil setup
        Engine.GL.Enable(EnableCap.StencilTest);
        Engine.GL.StencilMask(0xFF);
        Engine.GL.Clear(ClearBufferMask.StencilBufferBit);

        // write 1s to the mask wherever we draw
        Engine.GL.StencilFunc(StencilFunction.Always, 1, 0xFF);
        Engine.GL.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Replace);

        // turn off colors and depth, just rendering the mask for now
        Engine.GL.ColorMask(false, false, false, false);
        Engine.GL.DepthMask(false);

        // times 2 because we need space for the mask pass AND the color pass
        int maxDataPerFrame = maxSpritesPerFrame * 2;
        int frameChunkOffset = maxDataPerFrame * currentFrameIndex;

        // pass 1: mask write
        int pass1Offset = frameChunkOffset;
        Shader.SetUniform("uDataOffset", pass1Offset);
        AggregateSpriteData(in sprites, true, pass1Offset);

        Engine.GL.DrawElementsInstanced(
            PrimitiveType.Triangles,
            6,
            DrawElementsType.UnsignedInt,
            null,
            (uint)sprites.Length
        );

        // pass 2: color draw
        // only draw if the mask equals 1, and don't write to the stencil buffer anymore
        Engine.GL.StencilFunc(StencilFunction.Equal, 1, 0x01);
        Engine.GL.StencilMask(0x00);
        Engine.GL.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Keep);

        // colors back!
        Engine.GL.ColorMask(true, true, true, true);
        Engine.GL.DepthMask(true);

        int pass2Offset = frameChunkOffset + sprites.Length;
        Shader.SetUniform("uDataOffset", pass2Offset);
        AggregateSpriteData(in sprites, false, pass2Offset);

        Engine.GL.DrawElementsInstanced(
            PrimitiveType.Triangles,
            6,
            DrawElementsType.UnsignedInt,
            null,
            (uint)sprites.Length
        );

        // drop a fence so we know when the gpu finishes this specific frame
        fences[currentFrameIndex] = Engine.GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, SyncBehaviorFlags.None);

        // loop back around
        currentFrameIndex = (currentFrameIndex + 1) % NUM_BUFFERS;

        // cleanup state so we don't bleed into other draw calls
        Engine.GL.Disable(EnableCap.StencilTest);
        Engine.GL.StencilMask(0xFF);

        Buffer.Unbind();
        Shader.Unbind();
    }

    private unsafe void ResizeBuffer(int newRequiredSpriteCount)
    {
        // pad by 1.5x so it doesnt keep recreating the buffer if the count is fluctuating
        maxSpritesPerFrame = (int)(newRequiredSpriteCount * 1.5f);

        // space for 2 passes per frame * 3 frames
        int maxDataPerFrame = maxSpritesPerFrame * 2;
        int totalCapacity = maxDataPerFrame * NUM_BUFFERS;

        Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(
            Bogz.Logging.LogLevel.Info,
            $"[SpriteBatchMesh] Sizing persistent SSBO for {maxSpritesPerFrame} max sprites per frame (Total Size: {totalCapacity * sizeof(SpriteData)} bytes)."
        );

        // wait for the gpu to literally finish EVERYTHING before we pussynuke the buffer to avoid a crash
        for (int i = 0; i < NUM_BUFFERS; i++)
        {
            if (fences[i] != 0)
            {
                Engine.GL.ClientWaitSync(fences[i], SyncObjectMask.Bit, 1_000_000_000);
                Engine.GL.DeleteSync(fences[i]);
                fences[i] = 0;
            }
        }

        if (StorageBuffer != null)
        {
            StorageBuffer.UnmapBuffer();
            Engine.ObjectManager.Buffers.Remove(StorageBuffer);
        }

        // galactus allocation
        if (Engine.ObjectManager.Buffers.TryCreate(
            new BufferObjectDescription
            {
                IsStorageBuffer = true,
                Size = (uint)(sizeof(SpriteData) * totalCapacity),
                StorageMasks = BufferStorageMask.MapCoherentBit
                             | BufferStorageMask.MapPersistentBit
                             | BufferStorageMask.MapWriteBit,
                Type = BufferTargetARB.ShaderStorageBuffer
            },
            out var storeResult))
        {
            StorageBuffer = storeResult.Asset;
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, storeResult.Message);
            return;
        }

        dataPtr = (SpriteData*)
            StorageBuffer.MapBufferRange(
                (uint)(totalCapacity * sizeof(SpriteData)),
                MapBufferAccessMask.WriteBit
                | MapBufferAccessMask.PersistentBit
                | MapBufferAccessMask.CoherentBit
            );

        currentFrameIndex = 0;

        if (Shader != null)
        {
            Shader.BindBuffer("spriteData", StorageBuffer);
        }
    }

    protected void BindAndSetUniforms(in Camera? camera, in Matrix4x4 globalModel)
    {
        Shader.Bind();

        Shader.SetUniform(UNIFORM_CAMERA_PROJ_MATRIX, camera?.Projection ?? Engine.ActiveCamera.Projection);
        Shader.SetUniform(UNIFORM_CAMERA_VIEW_MATRIX, camera?.View ?? Engine.ActiveCamera.View);
        Shader.SetUniform(UNIFORM_SINGLE_BUFFER_SIZE, sheet.SingleSpriteSize);
        Shader.SetUniform(UNIFORM_MODEL_MATRIX, in globalModel);

        Engine.GL.BindTextureUnit(0, sheet.Handle);
        Shader.SetUniform("uTexture", 0);
    }

    // TODO: @bogz this is a fuckup and a half my guy
    private unsafe void AggregateSpriteData(in ReadOnlySpan<Sprite> sprites, bool useStencilBuffer, int memoryOffset)
    {
        if (dataPtr == null) // no nullptr c#!!!! woww!!!!
            return;

        for (int i = 0; i < sprites.Length; i++)
        {
            if (sprites[i] is null)
                return; // incase we modified the array while itterating!! thanks multithreading!!

            // shift the pointer by memoryOffset so we hit the right third of the buffer
            dataPtr[memoryOffset + i].modelMatrix = sprites[i].UseStencilBuffer && useStencilBuffer ? sprites[i].StencilTransform.ModelMatrix : sprites[i].Transform.ModelMatrix;
            dataPtr[memoryOffset + i].spriteOffset = sprites[i].GetFrameOffset();
            dataPtr[memoryOffset + i].frameIndex = sprites[i].GetFrameIndex();
            dataPtr[memoryOffset + i].spriteSpan = sprites[i].GetFrameSpan();
        }
    }
}