using System.Numerics;

using Horizon.Engine;
using Horizon.Graphics;

namespace Horizon.Rendering.Spriting.Components;

/// <summary>
/// The GPU side of the sprite renderer. The one quad (see shaders/common/quad.slang, there are no vertices), drawn
/// once for every <see cref="SpriteItem"/> of a frame. A <see cref="SpriteBatch"/> has one of these for every sprite
/// sheet (its sprites are turned into items here), and one for the items it is handed directly.
/// <para>
/// The items of a frame are written straight into a <see cref="StreamBuffer{T}"/> the shader reads as a storage
/// block, and a frame is one draw call. The camera comes out of the <see cref="CameraBlock"/>, every item names its
/// texture by its place in the bindless table, and a run that starts partway through the frame's items says so with
/// its base instance rather than a uniform.
/// </para>
/// </summary>
public class SpriteBatchMesh : GameObject
{
    /// <summary>The binding of the storage block the items are read out of, which is what sprites.slang says.</summary>
    public const uint ITEMS_BINDING = 3;

    private const string UNIFORM_MODEL_MATRIX = "uModel";
    private const string UNIFORM_NEARNESS = "uNearness";

    private readonly SpriteSheet? sheet;
    private readonly StreamBuffer<SpriteItem> items;

    public Technique Shader { get; init; }

    /// <summary>The buffer the items of the frames go into.</summary>
    public StreamBuffer<SpriteItem> Items => items;

    // How many items the current frame has, see BeginItems
    private int frameCount;

    /// <summary>
    /// How near what this mesh draws is, from 0 (the backdrop) to 1 (right in front). Only a renderer that blurs
    /// motion goes by it, what is nearer blurs over what is further away, see <see cref="DeferredRenderer2D"/>.
    /// </summary>
    public float Nearness { get; set; } = SpriteBatch.DEFAULT_NEARNESS;

    /// <summary>A mesh for the sprites of a sprite sheet.</summary>
    public SpriteBatchMesh(SpriteSheet sheet, Technique shader)
        // Room for a fair few sprites (and their masks) to begin with, it grows when more turn up. Making room for tens of
        // thousands up front cost every sheet five and a half megabytes of mapped memory, and a scene the time to map it
        : this(shader, 256)
    {
        this.sheet = sheet;
    }

    /// <summary>A mesh for items that bring their own textures.</summary>
    /// <param name="initialItems">How many items to make room for to begin with, it grows when more turn up.</param>
    public SpriteBatchMesh(Technique shader, int initialItems = 2048)
    {
        Shader = shader;
        items = new StreamBuffer<SpriteItem>(BufferUsage.Storage, initialItems, "sprite items");
    }

    public override void Render(float dt)
    {
        throw new Exception("Please only draw a SpriteBatchMesh through a SpriteBatch");
    }

    /// <summary>
    /// Starts a frame of items. Hands over the memory to write them to (straight into the buffer the GPU reads, so
    /// write only, never read it back), to be drawn with <see cref="DrawItems"/> and finished with <see cref="EndItems"/>.
    /// The texture bits of every item have to be the texture's place in the bindless table by the time it is drawn,
    /// see <see cref="SpriteTexture.Index"/>.
    /// </summary>
    public Span<SpriteItem> BeginItems(int count)
    {
        Span<SpriteItem> span = items.Begin(count);
        frameCount = span.Length;
        return span;
    }

    /// <summary>Draws a run of the items of this frame in a single call, in the order they are in.</summary>
    /// <param name="first">The first item of the run, counted from the start of what <see cref="BeginItems"/> returned.</param>
    public void DrawItems(int first, int count, in Matrix4x4 globalModel, Camera camera)
    {
        if (count < 1 || first + count > frameCount) return;

        CameraBlock.Use(camera);
        Shader.Bind();
        Shader.SetUniform(UNIFORM_MODEL_MATRIX, in globalModel);
        Shader.SetUniform(UNIFORM_NEARNESS, Nearness);

        items.BindRange(ITEMS_BINDING);

        GraphicsDevice.Current.DrawInstanced(Topology.Triangles, 6, (uint)count, (uint)first);
    }

    /// <summary>Ends the frame started by <see cref="BeginItems"/>.</summary>
    public void EndItems()
    {
        items.End();
        frameCount = 0;
    }

    /// <summary>Draws the sprites of the sprite sheet this mesh was made for.</summary>
    public void Draw(Matrix4x4 globalModel, in ReadOnlySpan<Sprite> sprites, Camera engineActiveCamera)
    {
        if (sheet is null) return;

        Draw(globalModel, in sprites, engineActiveCamera, new SpriteTexture(sheet));
    }

    /// <summary>Draws sprites that all show parts of one texture, a sprite sheet or an atlas.</summary>
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

        // The masks first (if any), then the sprites themselves
        uint index = texture.Index;
        int masks = masked ? AggregateSpriteData(in sprites, true, items, index) : 0;
        int colors = AggregateSpriteData(in sprites, false, items[masks..], index);

        DrawPasses(masks, colors, globalModel, engineActiveCamera);

        EndItems();
    }

    /// <summary>
    /// Draws the items of this frame (see <see cref="BeginItems"/>), the first <paramref name="masks"/> of them as the
    /// stencil mask the rest are cut out by, the <paramref name="colors"/> after them as they look. Without masks the
    /// items are simply drawn, without touching the stencil at all.
    /// </summary>
    public void DrawPasses(int masks, int colors, in Matrix4x4 globalModel, Camera camera)
    {
        if (masks == 0)
        {
            DrawItems(0, colors, globalModel, camera);
            return;
        }

        var device = GraphicsDevice.Current;

        // Write 1s to the mask wherever the masks are drawn, and nothing else
        device.SetStencilTest(true);
        device.SetStencilWrite(0xFF);
        device.Clear(ClearTargets.Stencil);
        device.SetStencilFunction(CompareFunction.Always, 1, 0xFF);
        device.SetStencilOperation(StencilAction.Keep, StencilAction.Keep, StencilAction.Replace);
        device.SetColorWrite(false);
        device.SetDepthWrite(false);

        DrawItems(0, masks, globalModel, camera);

        // Then the colours, only where the mask is 1, and the stencil left alone
        device.SetStencilFunction(CompareFunction.Equal, 1, 0x01);
        device.SetStencilWrite(0x00);
        device.SetStencilOperation(StencilAction.Keep, StencilAction.Keep, StencilAction.Keep);
        device.SetColorWrite(true);
        device.SetDepthWrite(true);

        DrawItems(masks, colors, globalModel, camera);

        // Cleaned up so it doesn't bleed into other draw calls
        device.SetStencilTest(false);
        device.SetStencilWrite(0xFF);
    }

    /// <summary>Turns every enabled sprite into an item showing a texture, returns how many there were.</summary>
    /// <param name="mask">Whether this is for the stencil pass, where the sprites that have one are drawn as their mask.</param>
    private static int AggregateSpriteData(in ReadOnlySpan<Sprite> sprites, bool mask, Span<SpriteItem> items, uint texture)
    {
        int i = 0;
        foreach (var sprite in sprites)
        {
            if (sprite == null) break;
            if (!sprite.Enabled) continue;

            // The sprite knows what it shows, a cell of its sheet or a region of its atlas
            if (sprite.TryCreateItem(mask, out SpriteItem item) && i < items.Length)
            {
                item.Flags = SpriteItem.WithTexture(item.Flags, texture);
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
