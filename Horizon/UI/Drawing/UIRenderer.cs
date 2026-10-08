using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering.Spriting;
using Horizon.UI.Skinning;
using Horizon.OpenGL;

using Silk.NET.OpenGL;

namespace Horizon.UI.Drawing;

/// <summary>
/// Hands a <see cref="UIDrawList"/> to the sprite renderer: the UI has no shader or buffers of its own, its
/// quads are <see cref="SpriteItem"/>s and a <see cref="SpriteBatch"/> draws them. What is left to do here
/// is the GL state a UI needs around that (blending on, depth off) and saying which textures the quads show.
/// </summary>
internal sealed class UIRenderer
{
    private SpriteBatch? batch;

    // One sampler serves every renderer, so making and dropping compositors doesn't pile samplers up.
    private static uint fontSampler;

    // The last list that was handed over, as the simulation thread is free to reuse its own right away.
    private SpriteItem[] items = [];
    private int itemCount;
    private readonly List<UIDrawList.Run> runs = [];
    private readonly List<TextureAtlas> atlases = [];
    private readonly List<SpriteRun> spriteRuns = [];
    private UISkin? skin;

    /// <summary>The quads that were uploaded last, which is what is drawn. For tests that look at what is on screen.</summary>
    internal ReadOnlySpan<SpriteItem> Uploaded => items.AsSpan(0, itemCount);

    /// <summary>Whether anything of what was last uploaded is going anywhere, see <see cref="UIDrawList.Moving"/>.</summary>
    public bool Moving { get; private set; }

    /// <summary>Whether there is nothing to draw at all, a UI whose modules are all off, or one that hasn't been painted yet.</summary>
    public bool IsEmpty => itemCount == 0;

    public void Initialize()
    {
        // Not part of any scene. It only ever draws what it is handed here.
        batch = new SpriteBatch();
        batch.Initialize();
        batch.InitializeAll();

        // Everything the UI has on the GPU exists from here on, rather than from whenever it first has something to show.
        batch.PrepareItems();

        if (fontSampler != 0)
            return;

        // Text is nearly always drawn smaller than the atlas, which a nearest filter turns to SHIT, so
        // the font gets as many mipmaps as can be and is sampled through a smooth sampler. The texture's own filter is
        // left alone for anything else drawing with the same font image...
        fontSampler = GameEngine.Instance.Graphics.CreateSampler(new Horizon.Graphics.SamplerSettings(Smooth: true, Mipmaps: true));
    }

    /// <summary>
    /// Gets the font of a skin ready to be drawn small. Has to be done once for every skin.
    /// </summary>
    public static void PrepareFont(UIFont font)
    {
        if (font.Texture.Handle == 0)
            return;

        GameEngine.Instance.Graphics.GenerateMipmaps(font.Texture);
    }

    /// <summary>
    /// Takes a copy of a finished draw list. The list is free to be reused afterwards.
    /// </summary>
    public void Upload(UIDrawList list)
    {
        var source = list.Items;
        if (items.Length < source.Length)
            items = new SpriteItem[(int)BitOperations.RoundUpToPowerOf2((uint)source.Length)];

        source.CopyTo(items);
        itemCount = source.Length;

        runs.Clear();
        runs.AddRange(list.Runs);

        atlases.Clear();
        atlases.AddRange(list.Atlases);

        // What the items show is in this skin's atlas, not in whichever skin is current by the time they are drawn.
        skin = list.Skin;
        Moving = list.Moving;
    }

    /// <summary>
    /// Takes a copy of quads and their runs that weren't painted into a <see cref="UIDrawList"/> of their own: a list as
    /// it was captured at the end of a tick, or two of those blended. They are free to be reused afterwards.
    /// </summary>
    public void Upload(ReadOnlySpan<SpriteItem> source, ReadOnlySpan<UIDrawList.Run> sourceRuns, UISkin listSkin, bool moving, ReadOnlySpan<TextureAtlas> listAtlases = default)
    {
        if (items.Length < source.Length)
            items = new SpriteItem[(int)BitOperations.RoundUpToPowerOf2((uint)source.Length)];

        source.CopyTo(items);
        itemCount = source.Length;

        runs.Clear();
        foreach (var run in sourceRuns)
            runs.Add(run);

        atlases.Clear();
        foreach (var atlas in listAtlases)
            atlases.Add(atlas);

        skin = listSkin;
        Moving = moving;
    }

    /// <summary>
    /// Forgets what was uploaded, for when the skin it was painted with is no more.
    /// </summary>
    public void Clear()
    {
        itemCount = 0;
        runs.Clear();
        skin = null;
        Moving = false;
    }

    /// <summary>
    /// Draws what was last uploaded, on top of whatever is already there.
    /// </summary>
    public void Draw(Camera camera)
    {
        // Art the UI asked other people's atlases for turns up in them here, on the thread that can upload it
        foreach (var atlas in atlases)
            atlas.Update();

        if (batch is null || skin is null || itemCount == 0)
            return;

        // The UI is painted back to front with alpha blending. The rest of the engine doesn't expect
        // either, so everything touched here is put back afterwards.
        var before = RenderState.Save();

        RenderState.Blend = true;
        RenderState.BlendMode = BlendMode.Alpha;
        RenderState.DepthTest = false;

        // Every run shows the atlas and the font, in the slots the draw list gave them.
        ReadOnlySpan<SpriteTexture> shared =
        [
            new SpriteTexture(skin.Atlas.Texture),
            new SpriteTexture(skin.Font.Texture, fontSampler)
        ];

        spriteRuns.Clear();
        foreach (var run in runs)
        {
            spriteRuns.Add(new SpriteRun(
                run.First,
                run.Count,
                run.Image0 is { } image0 ? new SpriteTexture(image0) : default,
                run.Image1 is { } image1 ? new SpriteTexture(image1) : default));
        }

        batch.Draw(
            items.AsSpan(0, itemCount),
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(spriteRuns),
            camera,
            shared);

        RenderState.Restore(before);
    }
}
