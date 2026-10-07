using Bogz.Logging;
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

/// <summary>
/// The GL side of the sprite renderer: one quad, drawn once for every <see cref="SpriteItem"/> in a buffer.
/// A <see cref="SpriteBatch"/> has one of these for every sprite sheet (its sprites are turned into items here),
/// and one for the items it is handed directly.
/// </summary>
public class SpriteBatchMesh : GameObject
{
    /// <summary>
    /// How many different textures the items of a single draw call can show, see <see cref="SpriteItem.Flags"/>.
    /// </summary>
    public const int MaxTextures = 4;

    private const string UNIFORM_CAMERA_PROJ_MATRIX = "uCameraProjection";
    private const string UNIFORM_CAMERA_VIEW_MATRIX = "uCameraView";
    private const string UNIFORM_MODEL_MATRIX = "uModel";
    private const string UNIFORM_DATA_OFFSET = "uDataOffset";
    private const string UNIFORM_CAMERA_VELOCITY = "uCameraVelocity";
    private const string UNIFORM_MOTION_SCALE = "uMotionScale";
    private const string UNIFORM_NEARNESS = "uNearness";

    // uniform arrays are set one element at a time, by name
    private static readonly string[] UNIFORM_TEXTURES = ["uTextures[0]", "uTextures[1]", "uTextures[2]", "uTextures[3]"];
    private static readonly string[] UNIFORM_TEXEL_SIZES = ["uTexelSizes[0]", "uTexelSizes[1]", "uTexelSizes[2]", "uTexelSizes[3]"];

    private readonly SpriteSheet? sheet;

    public Technique Shader { get; init; }
    public VertexBufferObject Buffer { get; init; }

    // removed 'init' so we can overwrite this when resizing without c# yelling at us
    public BufferObject StorageBuffer { get; private set; }

    // triple buffering state so the cpu doesn't completely gap the gpu
    private const int NUM_BUFFERS = 3;
    private int currentFrameIndex = 0;
    private nint[] fences = new nint[NUM_BUFFERS];
    private int maxItemsPerFrame = 0; // gets set in ResizeBuffer

    // where this frame's items start in the buffer, and how many of them there are
    private int frameOffset, frameCount;

    // eish
    private unsafe SpriteItem* dataPtr;

    public uint ElementCount { get; private set; }

    /// <summary>
    /// How near what this mesh draws is, from 0 (the backdrop) to 1 (right in front). Only a renderer that blurs
    /// motion goes by it: what is nearer blurs over what is further away, see <see cref="DeferredRenderer2D"/>.
    /// </summary>
    public float Nearness { get; set; } = SpriteBatch.DEFAULT_NEARNESS;

    /// <summary>
    /// A mesh for the sprites of a sprite sheet.
    /// </summary>
    public SpriteBatchMesh(SpriteSheet sheet, Technique shader)
        // Room for a fair few sprites (and their masks) to begin with, it grows when more turn up. Making room for tens of
        // thousands up front cost every sheet five and a half megabytes of mapped memory, and a scene the time to map it
        : this(shader, 256)
    {
        this.sheet = sheet;
    }

    /// <summary>
    /// A mesh for items that bring their own textures.
    /// </summary>
    /// <param name="initialItems">How many items to make room for to begin with, it grows when more turn up.</param>
    public unsafe SpriteBatchMesh(Technique shader, int initialItems = 2048)
        : base()
    {
        this.Shader = shader;

        Buffer = VertexBufferObject.Create();

        SetVboLayout();
        GenerateMesh();

        ResizeBuffer(initialItems);
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

    public override void Render(float dt)
    {
        throw new Exception("Please only draw a SpriteBatchMesh through a SpriteBatch");
    }

    /// <summary>
    /// Starts a frame of items: hands over the memory to write them to (straight into the buffer the GPU reads, so
    /// write only, never read it back), to be drawn with <see cref="DrawItems"/> and finished with <see cref="EndItems"/>.
    /// </summary>
    public unsafe Span<SpriteItem> BeginItems(int count)
    {
        // shit too many items, halt the presses and resize
        if (count > maxItemsPerFrame)
        {
            ResizeBuffer(count);
        }

        // make sure gpu is actually done with this chunk of memory before we oozing all over it mmmhhhppphhh
        if (fences[currentFrameIndex] != 0)
        {
            Engine.GL.ClientWaitSync(fences[currentFrameIndex], SyncObjectMask.Bit, 1_000_000_000);
            Engine.GL.DeleteSync(fences[currentFrameIndex]);
            fences[currentFrameIndex] = 0;
        }

        frameOffset = maxItemsPerFrame * currentFrameIndex;
        frameCount = count;

        if (dataPtr == null) // no nullptr c#!!!! woww!!!!
            return default;

        return new Span<SpriteItem>(dataPtr + frameOffset, count);
    }

    /// <summary>
    /// Draws a run of the items of this frame in a single call, in the order they are in.
    /// </summary>
    /// <param name="first">The first item of the run, counted from the start of what <see cref="BeginItems"/> returned.</param>
    /// <param name="textures">The textures the items refer to by slot, <see cref="MaxTextures"/> at the most.</param>
    public unsafe void DrawItems(int first, int count, ReadOnlySpan<SpriteTexture> textures, in Matrix4x4 globalModel, Camera camera)
    {
        if (count < 1 || dataPtr == null || first + count > frameCount) return;

        BindAndSetUniforms(camera, globalModel, textures);
        Shader.SetUniform(UNIFORM_DATA_OFFSET, frameOffset + first);

        Shader.BindBuffer("spriteData", StorageBuffer);
        Buffer.Bind();
        Buffer.VertexBuffer.Bind();
        Buffer.ElementBuffer.Bind();

        Engine.GL.DrawElementsInstanced(
            PrimitiveType.Triangles,
            6,
            DrawElementsType.UnsignedInt,
            null,
            (uint)count
        );

        Buffer.Unbind();
        Shader.Unbind();

        // leave the units the way we found them, samplers stick to a unit whatever texture is bound there next
        for (int i = 0; i < textures.Length && i < MaxTextures; i++)
        {
            if (textures[i].Sampler != 0) Engine.GL.BindSampler((uint)i, 0);
        }
    }

    /// <summary>
    /// Ends the frame started by <see cref="BeginItems"/>.
    /// </summary>
    public void EndItems()
    {
        // drop a fence so we know when the gpu finishes this specific frame
        fences[currentFrameIndex] = Engine.GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, SyncBehaviorFlags.None);

        // loop back around
        currentFrameIndex = (currentFrameIndex + 1) % NUM_BUFFERS;
        frameCount = 0;
    }

    /// <summary>
    /// Draws the sprites of the sprite sheet this mesh was made for.
    /// </summary>
    public void Draw(Matrix4x4 globalModel, in ReadOnlySpan<Sprite> sprites, Camera engineActiveCamera)
    {
        if (sheet is null) return;

        Draw(globalModel, in sprites, engineActiveCamera, new SpriteTexture(sheet));
    }

    /// <summary>
    /// Draws sprites that all show parts of one texture, a sprite sheet or an atlas.
    /// </summary>
    public unsafe void Draw(Matrix4x4 globalModel, in ReadOnlySpan<Sprite> sprites, Camera engineActiveCamera, SpriteTexture texture)
    {
        if (!Enabled) return;

        // Only sprites that are cut out with a mask need the stencil. Without any there is one pass, not three
        bool masked = false;
        foreach (var sprite in sprites)
        {
            if (sprite is { Enabled: true, UseStencilBuffer: true })
            {
                masked = true;
                break;
            }
        }

        Span<SpriteItem> items = BeginItems(sprites.Length * (masked ? 2 : 1));
        if (items.IsEmpty)
        {
            EndItems();
            return;
        }

        // the masks first (if any), then the sprites themselves
        int masks = masked ? AggregateSpriteData(in sprites, true, items) : 0;
        int colors = AggregateSpriteData(in sprites, false, items[masks..]);

        ReadOnlySpan<SpriteTexture> textures = [texture];
        DrawPasses(masks, colors, textures, globalModel, engineActiveCamera);

        EndItems();
    }

    /// <summary>
    /// Draws the items of this frame (see <see cref="BeginItems"/>): the first <paramref name="masks"/> of them as the
    /// stencil mask the rest are cut out by, the <paramref name="colors"/> after them as they look. Without masks the
    /// items are simply drawn, without touching the stencil at all.
    /// </summary>
    public void DrawPasses(int masks, int colors, ReadOnlySpan<SpriteTexture> textures, in Matrix4x4 globalModel, Camera camera)
    {
        if (masks == 0)
        {
            DrawItems(0, colors, textures, globalModel, camera);
            return;
        }

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

        // pass 1: mask write
        DrawItems(0, masks, textures, globalModel, camera);

        // pass 2: color draw
        // only draw if the mask equals 1, and don't write to the stencil buffer anymore
        Engine.GL.StencilFunc(StencilFunction.Equal, 1, 0x01);
        Engine.GL.StencilMask(0x00);
        Engine.GL.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Keep);

        // colors back!
        Engine.GL.ColorMask(true, true, true, true);
        Engine.GL.DepthMask(true);

        DrawItems(masks, colors, textures, globalModel, camera);

        // cleanup state so we don't bleed into other draw calls
        Engine.GL.Disable(EnableCap.StencilTest);
        Engine.GL.StencilMask(0xFF);
    }

    private unsafe void ResizeBuffer(int newRequiredItemCount)
    {
        // pad by 1.5x so it doesnt keep recreating the buffer if the count is fluctuating
        maxItemsPerFrame = (int)(newRequiredItemCount * 1.5f);

        // space for 3 frames
        int totalCapacity = maxItemsPerFrame * NUM_BUFFERS;

        Log.Info($"[SpriteBatchMesh] Sizing persistent SSBO for {maxItemsPerFrame} max items per frame (Total Size: {totalCapacity * sizeof(SpriteItem)} bytes)."
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
                Size = (uint)(sizeof(SpriteItem) * totalCapacity),
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
            Log.Error(storeResult.Message);
            return;
        }

        dataPtr = (SpriteItem*)
            StorageBuffer.MapBufferRange(
                (uint)(totalCapacity * sizeof(SpriteItem)),
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

    protected void BindAndSetUniforms(in Camera? camera, in Matrix4x4 globalModel, ReadOnlySpan<SpriteTexture> textures)
    {
        Shader.Bind();

        Shader.SetUniform(UNIFORM_CAMERA_PROJ_MATRIX, camera?.Projection ?? Engine.ActiveCamera.Projection);
        Shader.SetUniform(UNIFORM_CAMERA_VIEW_MATRIX, camera?.View ?? Engine.ActiveCamera.View);
        Shader.SetUniform(UNIFORM_MODEL_MATRIX, in globalModel);

        // What it takes to say how fast an item goes across the screen: how fast the camera goes, and how much of
        // the screen a unit of the world is
        Camera motionCamera = camera ?? Engine.ActiveCamera;
        Vector2 cameraVelocity = motionCamera.Velocity;
        Vector2 motionScale = new(motionCamera.Projection.M11, motionCamera.Projection.M22);

        Shader.SetUniform(UNIFORM_CAMERA_VELOCITY, in cameraVelocity);
        Shader.SetUniform(UNIFORM_MOTION_SCALE, in motionScale);
        Shader.SetUniform(UNIFORM_NEARNESS, Nearness);

        for (int i = 0; i < MaxTextures; i++)
        {
            // a slot nothing was given for still needs something valid behind its sampler
            SpriteTexture texture = i < textures.Length ? textures[i] : textures.Length > 0 ? textures[0] : default;
            Vector2 texelSize = texture.Size.X > 0 && texture.Size.Y > 0 ? Vector2.One / texture.Size : Vector2.Zero;

            Engine.GL.BindTextureUnit((uint)i, texture.Handle);
            if (i < textures.Length && texture.Sampler != 0) Engine.GL.BindSampler((uint)i, texture.Sampler);

            Shader.SetUniform(UNIFORM_TEXTURES[i], i);
            Shader.SetUniform(UNIFORM_TEXEL_SIZES[i], in texelSize);
        }
    }

    /// <summary>
    /// Turns every enabled sprite into an item, returns how many there were.
    /// </summary>
    /// <param name="mask">Whether this is for the stencil pass, where the sprites that have one are drawn as their mask.</param>
    private static int AggregateSpriteData(in ReadOnlySpan<Sprite> sprites, bool mask, Span<SpriteItem> items)
    {
        int i = 0;
        foreach (var sprite in sprites)
        {
            if (sprite == null) break;
            if (!sprite.Enabled) continue;

            // the sprite knows what it shows, a cell of its sheet or a region of its atlas
            if (sprite.TryCreateItem(mask, out SpriteItem item) && i < items.Length)
            {
                items[i++] = item;
            }
        }

        return i;
    }
}
