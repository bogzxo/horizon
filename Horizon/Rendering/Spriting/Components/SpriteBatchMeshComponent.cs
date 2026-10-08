using System.Numerics;

using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

namespace Horizon.Rendering.Spriting.Components;

/// <summary>
/// The GL side of the sprite renderer: the one quad (see <see cref="UnitQuad"/>), drawn once for every
/// <see cref="SpriteItem"/> of a frame. A <see cref="SpriteBatch"/> has one of these for every sprite sheet (its
/// sprites are turned into items here), and one for the items it is handed directly.
/// <para>
/// The items of a frame are written straight into a <see cref="StreamBuffer{T}"/> the shader reads as a storage
/// block, and a frame is one draw call per run of items: the camera comes out of the <see cref="CameraBlock"/>,
/// the textures sit on the units the shader says (0 to 3, bound in one call), and a run that starts partway through
/// the frame's items says so with its base instance rather than a uniform.
/// </para>
/// </summary>
public class SpriteBatchMesh : GameObject
{
    /// <summary>
    /// How many different textures the items of a single draw call can show, see <see cref="SpriteItem.Flags"/>.
    /// </summary>
    public const int MaxTextures = 4;

    /// <summary>The binding of the storage block the items are read out of, which is what sprites.vert says.</summary>
    public const uint ITEMS_BINDING = 1;

    private const string UNIFORM_MODEL_MATRIX = "uModel";
    private const string UNIFORM_NEARNESS = "uNearness";
    private const string UNIFORM_TEXEL_SIZES = "uTexelSizes";

    private readonly SpriteSheet? sheet;
    private readonly StreamBuffer<SpriteItem> items;

    public Technique Shader { get; init; }

    /// <summary>The buffer the items of the frames go into.</summary>
    public StreamBuffer<SpriteItem> Items => items;

    // How many items the current frame has, see BeginItems
    private int frameCount;

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
    public SpriteBatchMesh(Technique shader, int initialItems = 2048)
    {
        Shader = shader;
        items = new StreamBuffer<SpriteItem>(BufferTargetARB.ShaderStorageBuffer, initialItems, "sprite items");
    }

    public override void Render(float dt)
    {
        throw new Exception("Please only draw a SpriteBatchMesh through a SpriteBatch");
    }

    /// <summary>
    /// Starts a frame of items: hands over the memory to write them to (straight into the buffer the GPU reads, so
    /// write only, never read it back), to be drawn with <see cref="DrawItems"/> and finished with <see cref="EndItems"/>.
    /// </summary>
    public Span<SpriteItem> BeginItems(int count)
    {
        Span<SpriteItem> span = items.Begin(count);
        frameCount = span.Length;
        return span;
    }

    /// <summary>
    /// Draws a run of the items of this frame in a single call, in the order they are in.
    /// </summary>
    /// <param name="first">The first item of the run, counted from the start of what <see cref="BeginItems"/> returned.</param>
    /// <param name="textures">The textures the items refer to by slot, <see cref="MaxTextures"/> at the most.</param>
    public unsafe void DrawItems(int first, int count, ReadOnlySpan<SpriteTexture> textures, in Matrix4x4 globalModel, Camera camera)
    {
        if (count < 1 || first + count > frameCount || !UnitQuad.Bind()) return;

        CameraBlock.Use(camera);
        Shader.Bind();
        Shader.SetUniform(UNIFORM_MODEL_MATRIX, in globalModel);
        Shader.SetUniform(UNIFORM_NEARNESS, Nearness);

        // Every unit gets something valid behind its sampler, a slot nothing was given for shows the first texture
        Span<uint> handles = stackalloc uint[MaxTextures];
        Span<uint> samplers = stackalloc uint[MaxTextures];
        Span<Vector2> texelSizes = stackalloc Vector2[MaxTextures];
        bool sampled = false;

        for (int i = 0; i < MaxTextures; i++)
        {
            SpriteTexture texture = i < textures.Length ? textures[i] : textures.Length > 0 ? textures[0] : default;
            handles[i] = texture.Handle;
            samplers[i] = i < textures.Length ? texture.Sampler : 0;
            texelSizes[i] = texture.Size.X > 0 && texture.Size.Y > 0 ? Vector2.One / texture.Size : Vector2.Zero;
            sampled |= samplers[i] != 0;
        }

        Technique.BindTextures(handles);
        if (sampled) Technique.BindSamplers(samplers);
        Shader.SetUniform(UNIFORM_TEXEL_SIZES, texelSizes);

        items.BindRange(BufferTargetARB.ShaderStorageBuffer, ITEMS_BINDING);

        ObjectManager.GL.DrawElementsInstancedBaseInstance(
            PrimitiveType.Triangles,
            UnitQuad.INDICES,
            DrawElementsType.UnsignedInt,
            null,
            (uint)count,
            (uint)first);

        // Samplers stick to a unit whatever texture is bound there next, so the units are left the way they were found
        if (sampled)
        {
            samplers.Clear();
            Technique.BindSamplers(samplers);
        }
    }

    /// <summary>
    /// Ends the frame started by <see cref="BeginItems"/>.
    /// </summary>
    public void EndItems()
    {
        items.End();
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
    public void Draw(Matrix4x4 globalModel, in ReadOnlySpan<Sprite> sprites, Camera engineActiveCamera, SpriteTexture texture)
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

        var gl = ObjectManager.GL;

        // stencil setup
        gl.Enable(EnableCap.StencilTest);
        gl.StencilMask(0xFF);
        gl.Clear(ClearBufferMask.StencilBufferBit);

        // write 1s to the mask wherever we draw
        gl.StencilFunc(StencilFunction.Always, 1, 0xFF);
        gl.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Replace);

        // turn off colors and depth, just rendering the mask for now
        gl.ColorMask(false, false, false, false);
        gl.DepthMask(false);

        // pass 1: mask write
        DrawItems(0, masks, textures, globalModel, camera);

        // pass 2: color draw
        // only draw if the mask equals 1, and don't write to the stencil buffer anymore
        gl.StencilFunc(StencilFunction.Equal, 1, 0x01);
        gl.StencilMask(0x00);
        gl.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Keep);

        // colors back!
        gl.ColorMask(true, true, true, true);
        gl.DepthMask(true);

        DrawItems(masks, colors, textures, globalModel, camera);

        // cleanup state so we don't bleed into other draw calls
        gl.Disable(EnableCap.StencilTest);
        gl.StencilMask(0xFF);
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

    protected override void DisposeOther()
    {
        items.Dispose();
        base.DisposeOther();
    }
}
