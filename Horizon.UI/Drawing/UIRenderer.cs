using System.Numerics;

using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Rendering.Spriting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Drawing;

/// <summary>
/// Hands a <see cref="UIDrawList"/> to the sprite renderer. The UI has no shader or buffers of its own, its
/// quads are <see cref="SpriteItem"/>s and a <see cref="SpriteBatch"/> draws them. What is left to do here
/// is the state a UI needs around that (blending on, depth off) and saying which textures the quads show.
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
    private readonly List<UIGlassPane> glass = [];
    private UISkin? skin;

    /// <summary>The quads that were uploaded last, which is what is drawn. For tests that look at what is on screen.</summary>
    internal ReadOnlySpan<SpriteItem> Uploaded => items.AsSpan(0, itemCount);


    /// <summary>The panes of glass in what was uploaded last, see <see cref="UIGlass"/>.</summary>
    internal ReadOnlySpan<UIGlassPane> Glass => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(glass);

    /// <summary>How many quads were uploaded last.</summary>
    internal int Count => itemCount;

    /// <summary>Whether there is nothing to draw at all, a UI whose modules are all off, or one that hasn't been painted yet.</summary>
    public bool IsEmpty => itemCount == 0;

    public void Initialize()
    {
        // Not part of any scene. It only ever draws what it is handed here.
        batch = new SpriteBatch();
        batch.Initialize();
        batch.InitializeAll();

        // With the UI's own shader rather than the lit sprites', which draws its rounded boxes and their edges and
        // shadows as a quad each (see shaders/ui/ui.slang). Before the quads' mesh is made, which takes it from here
        batch.Shader = Technique.Load("shaders/ui", "ui");

        // Everything the UI has on the GPU exists from here on, rather than from whenever it first has something to show.
        batch.PrepareItems();

        // The letters made at the size they're drawn, there from the first paint so text isn't drawn out of its field
        // first and then left that way by a UI with nothing to repaint
        Horizon.Rendering.Text.GlyphAtlas.Shared.Upload();

        if (fontSampler != 0)
            return;

        // The font is a distance field, read smoothly and without mipmaps, the shader makes the edge out of it at
        // whatever size the text is drawn (see FieldFlag in sprites.slang). Mipmaps would only blunt the field
        fontSampler = GameEngine.Instance.Graphics.CreateSampler(new Horizon.Graphics.SamplerSettings(Smooth: true, Mipmaps: false));
    }

    /// <summary>
    /// There used to be mipmaps to make here, a distance field wants none. Kept for whoever still calls it.
    /// </summary>
    public static void PrepareFont(UIFont font)
    { }

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

        glass.Clear();
        foreach (var pane in list.GlassPanes)
            glass.Add(pane);

        // What the items show is in this skin's atlas, not in whichever skin is current by the time they are drawn.
        skin = list.Skin;
        GroupSubpixels();
        FindBlends();
    }

    /// <summary>
    /// Takes a copy of quads and their runs that weren't painted into a <see cref="UIDrawList"/> of their own, a list as
    /// it was captured at the end of a tick, or two of those blended. They are free to be reused afterwards.
    /// </summary>
    public void Upload(ReadOnlySpan<SpriteItem> source, ReadOnlySpan<UIDrawList.Run> sourceRuns, UISkin listSkin, ReadOnlySpan<TextureAtlas> listAtlases = default, ReadOnlySpan<UIGlassPane> listGlass = default)
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

        glass.Clear();
        foreach (var pane in listGlass)
            glass.Add(pane);

        skin = listSkin;
        GroupSubpixels();
        FindBlends();
    }

    /// <summary>
    /// Forgets what was uploaded, for when the skin it was painted with is no more.
    /// </summary>
    public void Clear()
    {
        itemCount = 0;
        runs.Clear();
        glass.Clear();
        blendChanges.Clear();
        skin = null;
    }

    /// <summary>
    /// Draws what was last uploaded, on top of whatever is already there.
    /// </summary>
    public void Draw(Camera camera)
    {
        if (Begin())
            DrawRange(0, itemCount, camera);

        End();
    }

    // Whether Begin sent anything that End has to finish
    private bool begun;

    /// <summary>
    /// Sends what was last uploaded to the GPU without drawing any of it, it's drawn a stretch at a time with
    /// <see cref="DrawRange"/> and finished with <see cref="End"/>, which has to be called whatever this says. For a UI
    /// with glass in it, which has the panes put down between the stretches.
    /// </summary>
    /// <returns>Whether there is anything to draw.</returns>
    public bool Begin()
    {
        // Art the UI asked other people's atlases for turns up in them here, on the thread that can upload it
        foreach (var atlas in atlases)
            atlas.Update();

        // And the letters made since the last draw
        var glyphs = Horizon.Rendering.Text.GlyphAtlas.Shared;
        glyphs.Upload();
        var subpixels = Horizon.Rendering.Text.GlyphAtlas.Subpixel;
        subpixels.Upload();

        if (batch is null || skin is null || itemCount == 0)
            return false;

        // Every run shows the atlas, the font and the letters, in the slots the draw list gave them. Nothing asks for the
        // letters' slot before they have a texture, the font stands in to keep the slots after it where they are
        ReadOnlySpan<SpriteTexture> shared =
        [
            new SpriteTexture(skin.Atlas.Texture),
            new SpriteTexture(skin.Font.Texture, fontSampler),
            // Read smoothly. At rest a letter is a texel to the pixel on whole pixels, where smooth and nearest read the same,
            // but a frame between two ticks (a window sliding or fading in) puts it between pixels, where nearest stepped
            // its edges, the text aliasing as it moved
            new SpriteTexture(glyphs.Ready ? glyphs.Texture : skin.Font.Texture, fontSampler),
            new SpriteTexture(subpixels.Ready ? subpixels.Texture : skin.Font.Texture, fontSampler)
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

        begun = true;
        return batch.BeginItems(items.AsSpan(0, itemCount), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(spriteRuns), shared);
    }

    /// <summary>Draws the quads from one up to (and not including) another of what <see cref="Begin"/> sent.</summary>
    public void DrawRange(int from, int to, Camera camera)
    {
        if (!begun || batch is null || to <= from)
            return;

        // The UI is painted back to front with alpha blending. The rest of the engine doesn't expect
        // either, so everything touched here is put back afterwards.
        var before = RenderState.Save();

        RenderState.Blend = true;
        RenderState.BlendMode = BlendMode.Alpha;
        RenderState.DepthTest = false;

        if (blendChanges.Count == 0)
            batch.DrawItems(from, to - from, camera);
        else
            DrawBlended(from, to, camera);

        RenderState.Restore(before);
    }

    // Subpixel letters' two halves, what's under them darkened by each stripe's coverage and then their colour added by as
    // much (see SpriteItem.SubpixelEraseFlag). Blends every GPU has, the alpha left as it was
    private static readonly BlendMode SubpixelErase = new(BlendFactor.Zero, BlendFactor.OneMinusSrcColor, BlendFactor.Zero, BlendFactor.One);
    private static readonly BlendMode SubpixelInk = new(BlendFactor.One, BlendFactor.One, BlendFactor.Zero, BlendFactor.One);

    // Where the blend the items want changes, the item and the blend from it on, found once as they're handed over.
    // Empty for a UI without subpixel letters, which is drawn in one go as it always was
    private readonly List<(int At, BlendMode Mode)> blendChanges = [];

    private static BlendMode BlendOf(uint flags) =>
        (flags & SpriteItem.SubpixelEraseFlag) != 0 ? SubpixelErase
        : (flags & SpriteItem.SubpixelInkFlag) != 0 ? SubpixelInk
        : BlendMode.Alpha;

    // Where subpixel letters are partitioned, kept from one upload to the next
    private SpriteItem[] grouping = [];

    /// <summary>
    /// Every subpixel letter is two quads drawn with two blends, and a text put each of its letters' halves down together,
    /// so a screen of labels was three blends a label, erase, ink and back, each a draw of its own. Where the quads of
    /// several texts follow each other with nothing else between them (labels painted one after another), all their
    /// erasing halves go first and then all their ink, two draws for the lot. It only differs where two letters' ink
    /// overlaps, which text doesn't. Never across a run (the textures it shows) or a pane of glass (drawn between them).
    /// </summary>
    private void GroupSubpixels()
    {
        const uint SUBPIXEL = SpriteItem.SubpixelEraseFlag | SpriteItem.SubpixelInkFlag;
        foreach (var run in runs)
        {
            int end = Math.Min(run.First + run.Count, itemCount);
            int at = Math.Max(0, run.First);
            while (at < end)
            {
                if ((items[at].Flags & SUBPIXEL) == 0)
                {
                    at++;
                    continue;
                }

                int start = at++;
                while (at < end && (items[at].Flags & SUBPIXEL) != 0 && !GlassStartsAt(at))
                    at++;
                if (at - start > 2)
                    Partition(start, at);
            }
        }
    }

    // Whether a pane of glass is put down before the item, which a block of letters mustn't be moved across
    private bool GlassStartsAt(int item)
    {
        foreach (UIGlassPane pane in glass)
        {
            if (pane.Start == item)
                return true;
        }
        return false;
    }

    // The erasing halves first and the ink after, each in the order they were painted
    private void Partition(int from, int to)
    {
        int count = to - from;
        if (grouping.Length < count)
            grouping = new SpriteItem[(int)BitOperations.RoundUpToPowerOf2((uint)count)];

        int written = 0;
        for (int i = from; i < to; i++)
        {
            if ((items[i].Flags & SpriteItem.SubpixelEraseFlag) != 0)
                grouping[written++] = items[i];
        }
        for (int i = from; i < to; i++)
        {
            if ((items[i].Flags & SpriteItem.SubpixelEraseFlag) == 0)
                grouping[written++] = items[i];
        }
        grouping.AsSpan(0, count).CopyTo(items.AsSpan(from, count));
    }

    // The places the blend changes in what was handed over
    private void FindBlends()
    {
        blendChanges.Clear();
        BlendMode current = BlendMode.Alpha;
        bool any = false;
        for (int i = 0; i < itemCount; i++)
        {
            BlendMode wanted = BlendOf(items[i].Flags);
            if (wanted == current)
                continue;
            blendChanges.Add((i, wanted));
            current = wanted;
            any |= wanted != BlendMode.Alpha;
        }
        if (!any)
            blendChanges.Clear();
    }

    // A stretch of items drawn a run of one blend at a time
    private void DrawBlended(int from, int to, Camera camera)
    {
        // The blend in force at the start of the stretch, then a draw up to each change after it
        BlendMode mode = BlendMode.Alpha;
        int next = 0;
        while (next < blendChanges.Count && blendChanges[next].At <= from)
            mode = blendChanges[next++].Mode;

        int start = from;
        while (start < to)
        {
            int end = next < blendChanges.Count ? Math.Min(blendChanges[next].At, to) : to;
            if (end > start)
            {
                RenderState.BlendMode = mode;
                batch!.DrawItems(start, end - start, camera);
            }
            if (next < blendChanges.Count && blendChanges[next].At <= end)
                mode = blendChanges[next++].Mode;
            start = end;
        }
    }

    /// <summary>Finishes what <see cref="Begin"/> started.</summary>
    public void End()
    {
        if (begun)
            batch?.EndItems();

        begun = false;
    }
}
