using Horizon.Logging;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Horizon.Core.Components;
using Horizon.Core.Threading;
using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Rendering.Spriting.Components;

namespace Horizon.Rendering.Spriting;

/// <summary>
/// The sprite renderer. Besides the sprites added to it, it draws anything that can be described as a list of
/// <see cref="SpriteItem"/>s through the very same shader and buffers, see <see cref="Draw(ReadOnlySpan{SpriteItem}, ReadOnlySpan{SpriteRun}, Camera?, ReadOnlySpan{SpriteTexture})"/>.
/// Every texture is in the bindless table, so however many of them a list of items shows it goes to the GPU as one draw.
/// <para>
/// Drawn alongside the simulation (see <see cref="RenderFrame.IsDecoupled"/>), the sprites are turned into quads at the
/// end of every tick (<see cref="Capture"/>) and every frame draws them between the last two ticks. Each quad goes the
/// same distance for the same time from frame to frame, however many frames there are. A sprite that is flipped over or
/// put somewhere else (see <see cref="TransformComponent2D.Snap"/>) is not shown on its way.
/// </para>
/// </summary>
public class SpriteBatch : GameObject
{
    /// <summary>A Camera used to render all sprite meshes against, if null this defaults to the scene camera.</summary>
    public Camera? CustomCamera { get; set; }

    /// <summary>The global transform for all sprite meshes.</summary>
    public TransformComponent2D Transform { get; private set; }

    /// <summary>
    /// The sprites that are drawn out of one texture (a sprite sheet, or an atlas), which go in one draw call. They belong
    /// to the simulation, sprites are added, removed and turned into quads there.
    /// </summary>
    private sealed class SpriteGroup(SpriteSheet? sheet, TextureAtlas? atlas)
    {
        public readonly SpriteSheet? Sheet = sheet;
        public readonly TextureAtlas? Atlas = atlas;
        public readonly List<Sprite> Sprites = [];

        // The same sprites as a set, for asking whether one is in here already without walking the list
        public readonly HashSet<Sprite> Members = [];

        public SpriteTexture Texture => Atlas is { } a ? new SpriteTexture(a.Texture) : new SpriteTexture(Sheet!);
    }

    /// <summary>
    /// What a group drew as of one tick, a quad for every sprite (and one for the mask of every sprite, if any of them is
    /// cut out with one), and which sprite each quad is, which is how quads are matched up from one tick to the next.
    /// </summary>
    private sealed class CapturedGroup
    {
        public SpriteGroup Group = null!;
        public SpriteItem[] Items = new SpriteItem[64];
        public Sprite[] Sprites = new Sprite[32];
        public int[] Epochs = new int[32];
        public int Masks, Colors;
    }

    /// <summary>What the whole batch drew as of one tick.</summary>
    private sealed class CapturedBatch
    {
        public readonly List<CapturedGroup> Groups = [];
        public int GroupCount;
        public Matrix4x4 Model;
        public Camera? Camera;
    }

    private Technique _shader = null!;

    /// <summary>Gets or sets the shader used to draw queued/direct items.</summary>
    public Technique Shader
    {
        get => _shader;
        set
        {
            _shader = value;
            if (_itemMesh is not null)
                _itemMesh.Shader = value;
        }
    }

    // The groups by what they are drawn out of, and in the order they came, which never changes for a group
    private readonly Dictionary<uint, SpriteGroup> _sheetGroups = new();
    private readonly Dictionary<TextureAtlas, SpriteGroup> _atlasGroups = new();
    private readonly List<SpriteGroup> _groups = [];

    // What the GPU draws a group with. The render thread's, made the first time the group is drawn
    private readonly Dictionary<SpriteGroup, SpriteBatchMesh> _meshes = new();

    private readonly SnapshotBuffer<CapturedBatch> _captured = new(static () => new CapturedBatch());

    public int Count { get; private set; }

    private ConcurrentStack<Sprite> _queuedSprites = new();

    // Where the queued sprites are popped to on their way into their groups, kept from one time to the next
    private Sprite[] _taken = [];

    // The mesh for the items that are handed to us directly, made the first time there are any
    private SpriteBatchMesh? _itemMesh;

    public SpriteBatch()
    {
        Transform = AddComponent<TransformComponent2D>();
    }

    public override void Initialize()
    {
        Shader = Technique.Load("shaders/spritebatch", "sprites");

        base.Initialize();
    }

    /// <summary>
    /// Draws items right now, in the order they are in, each showing one of the textures by its slot.
    /// This is the way to draw things that aren't a <see cref="Sprite"/> (text, the regions of a texture atlas,
    /// flat colours) with the sprite renderer. Has to be called on the render thread.
    /// </summary>
    /// <param name="textures">The textures the items refer to by slot.</param>
    /// <param name="camera">The camera to draw with, if null this defaults to the custom camera and then the scene camera.</param>
    public void Draw(ReadOnlySpan<SpriteItem> items, ReadOnlySpan<SpriteTexture> textures, Camera? camera = null)
    {
        ReadOnlySpan<SpriteRun> runs = [new SpriteRun(0, items.Length)];
        Draw(items, runs, camera, textures);
    }

    /// <summary>
    /// Draws items right now, split up into runs that show textures of their own besides the shared ones. The slots
    /// the items name are swapped for the textures' places in the bindless table on the way in, and the lot is one draw.
    /// </summary>
    /// <param name="shared">The textures in the first slots of every run, the ones of the run itself come after them.</param>
    public void Draw(ReadOnlySpan<SpriteItem> items, ReadOnlySpan<SpriteRun> runs, Camera? camera = null, ReadOnlySpan<SpriteTexture> shared = default)
    {
        if (BeginItems(items, runs, shared))
            DrawItems(0, items.Length, camera);

        EndItems();
    }

    /// <summary>
    /// Sends items up the way <see cref="Draw(ReadOnlySpan{SpriteItem}, ReadOnlySpan{SpriteRun}, Camera?, ReadOnlySpan{SpriteTexture})"/>
    /// does but draws none of them, they're drawn a stretch at a time with <see cref="DrawItems"/> and finished with
    /// <see cref="EndItems"/>. For whoever has something else to draw in between (a UI with glass in it, which blurs
    /// what is under a panel before the panel goes on). The items are sent once whatever is drawn of them, sending
    /// every stretch on its own used up the regions of the buffer inside one frame and waited on the GPU for a second.
    /// </summary>
    /// <returns>Whether there is anything to draw, <see cref="EndItems"/> is called either way.</returns>
    public bool BeginItems(ReadOnlySpan<SpriteItem> items, ReadOnlySpan<SpriteRun> runs, ReadOnlySpan<SpriteTexture> shared = default) =>
        BeginItems(0UL, items, runs, shared);

    /// <summary>
    /// Revision-aware overload of <see cref="BeginItems(ReadOnlySpan{SpriteItem}, ReadOnlySpan{SpriteRun}, ReadOnlySpan{SpriteTexture})"/>.
    /// When <paramref name="revision"/> is non-zero and matches the last uploaded revision with identical resolved bindless
    /// texture slots, reuses the previously uploaded GPU buffer region without re-copying or re-uploading items.
    /// </summary>
    [SkipLocalsInit]
    public bool BeginItems(ulong revision, ReadOnlySpan<SpriteItem> items, ReadOnlySpan<SpriteRun> runs, ReadOnlySpan<SpriteTexture> shared = default)
    {
        sentItems = 0;
        if (!Enabled || items.IsEmpty || Shader is null)
            return false;

        PrepareItems();

        Span<uint> table = stackalloc uint[shared.Length + 2];
        ulong textureHash = 14695981039346656037UL;
        if (revision != 0)
        {
            textureHash = (textureHash ^ (uint)shared.Length) * 1099511628211UL;
            for (int i = 0; i < shared.Length; i++)
            {
                uint idx = shared[i].Index;
                table[i] = idx;
                textureHash = (textureHash ^ idx) * 1099511628211UL;
            }

            textureHash = (textureHash ^ (uint)runs.Length) * 1099511628211UL;
            foreach (ref readonly SpriteRun run in runs)
            {
                textureHash = (textureHash ^ (uint)run.First) * 1099511628211UL;
                textureHash = (textureHash ^ (uint)run.Count) * 1099511628211UL;
                textureHash = (textureHash ^ (run.Texture0.IsValid ? run.Texture0.Index : SpriteItem.NoTexture)) * 1099511628211UL;
                textureHash = (textureHash ^ (run.Texture1.IsValid ? run.Texture1.Index : SpriteItem.NoTexture)) * 1099511628211UL;
            }

            if (_itemMesh!.TryReuseItems(revision, items.Length, textureHash, out bool cachedCasters))
            {
                itemsBegun = true;
                if (cachedCasters) NoteCasters();
                sentItems = items.Length;
                return true;
            }
        }
        else
        {
            for (int i = 0; i < shared.Length; i++) table[i] = shared[i].Index;
        }

        Span<SpriteItem> buffer = _itemMesh!.BeginItems(items.Length);
        itemsBegun = true;
        if (!buffer.IsEmpty)
        {
            ref SpriteItem srcBase = ref MemoryMarshal.GetReference(items);
            ref SpriteItem dstBase = ref MemoryMarshal.GetReference(buffer);
            ref uint tableBase = ref MemoryMarshal.GetReference(table);

            uint casters = 0;
            foreach (ref readonly SpriteRun run in runs)
            {
                int count = shared.Length;
                if (run.Texture0.IsValid) Unsafe.Add(ref tableBase, count++) = run.Texture0.Index;
                if (run.Texture1.IsValid) Unsafe.Add(ref tableBase, count++) = run.Texture1.Index;

                int start = Math.Max(0, run.First);
                int end = Math.Min(run.First + run.Count, items.Length);
                uint ucount = (uint)count;
                for (int i = start; i < end; i++)
                {
                    SpriteItem item = Unsafe.Add(ref srcBase, i);
                    uint slot = SpriteItem.TextureOf(item.Flags);
                    uint resolved = slot < ucount ? Unsafe.Add(ref tableBase, (int)slot) : SpriteItem.NoTexture;
                    item.Flags = SpriteItem.WithTexture(item.Flags, resolved);
                    Unsafe.Add(ref dstBase, i) = item;
                    casters |= item.Flags & SpriteItem.ShadowFlag;
                }
            }

            bool hasCasters = casters != 0;
            if (hasCasters) NoteCasters();
            if (revision != 0) _itemMesh.RememberItems(revision, items.Length, textureHash, hasCasters);

            sentItems = items.Length;
        }

        return sentItems > 0;
    }

    // How many items BeginItems sent up, 0 while there are none to draw, and whether the mesh has a frame of them begun
    private int sentItems;
    private bool itemsBegun;

    /// <summary>Draws a stretch of what <see cref="BeginItems"/> sent, from an item for so many, in the order they are in.</summary>
    public void DrawItems(int first, int count, Camera? camera = null)
    {
        first = Math.Max(0, first);
        count = Math.Min(count, sentItems - first);
        if (count < 1 || _itemMesh is null)
            return;

        _itemMesh.DrawItems(first, count, Transform.ModelMatrix, camera ?? CustomCamera ?? Engine.ActiveCamera);
    }

    /// <summary>Finishes what <see cref="BeginItems"/> started.</summary>
    public void EndItems()
    {
        if (itemsBegun)
            _itemMesh?.EndItems();

        itemsBegun = false;
        sentItems = 0;
    }

    /// <summary>
    /// Makes the buffers that drawing items needs. They are otherwise made the first time there are items to draw,
    /// this is for whoever would rather have that over with (it has to be called on the render thread, after Initialize).
    /// </summary>
    public void PrepareItems()
    {
        if (Shader is not null)
        {
            _itemMesh ??= new SpriteBatchMesh(Shader);
        }
    }

    /// <summary>Commits an object to be rendered.</summary>
    public void Add(in Sprite sprite) => _queuedSprites.Push(sprite);

    /// <summary>Commits an object to be rendered.</summary>
    public void AddRange(in Sprite[] sprites) => _queuedSprites.PushRange(sprites);

    /// <summary>Stops drawing a sprite. From the updates.</summary>
    public void Remove(in Sprite sprite)
    {
        if (GroupOf(sprite, create: false) is not { } group)
            return;

        if (group.Members.Remove(sprite) && group.Sprites.Remove(sprite))
            Count--;
    }

    /// <summary>Helper method to find the group a sprite is drawn in by what it is drawn out of, made if it isn't there yet and asked to.</summary>
    private SpriteGroup? GroupOf(Sprite sprite, bool create)
    {
        if (sprite.Atlas is { } atlas)
        {
            if (_atlasGroups.TryGetValue(atlas, out var group) || !create)
                return group;

            _atlasGroups.Add(atlas, group = new SpriteGroup(null, atlas));
            _groups.Add(group);
            return group;
        }

        if (sprite.Spritesheet is not { } sheet)
            return null;

        if (_sheetGroups.TryGetValue(sheet.Handle, out var sheetGroup) || !create)
            return sheetGroup;

        _sheetGroups.Add(sheet.Handle, sheetGroup = new SpriteGroup(sheet, null));
        _groups.Add(sheetGroup);
        return sheetGroup;
    }

    /// <summary>
    /// Helper method to put the sprites that were added since the last time into the groups they are drawn in. The ones
    /// that don't know yet what they show wait for the next time. Simulation thread (or the render thread taking its
    /// turn with it).
    /// </summary>
    private void TakeQueued()
    {
        if (_queuedSprites.IsEmpty)
            return;

        int length = _queuedSprites.Count;
        if (_taken.Length < length)
            _taken = new Sprite[Math.Max(length, _taken.Length * 2)];

        Sprite[] sprites = _taken;
        int taken = _queuedSprites.TryPopRange(sprites, 0, length);

        // In the order they come off the stack, which is the order they have always been drawn in
        for (int i = 0; i < taken; i++)
        {
            Sprite sprite = sprites[i];
            sprite.Batch = this;

            if (!sprite.IsConfigured || GroupOf(sprite, create: true) is not { } group)
            {
                // Doesn't know what it shows yet, back in the queue with it
                _queuedSprites.Push(sprite);
                continue;
            }

            // A set says whether it is in there already. Asking the list meant walking it for every sprite added,
            // which for ten thousand of them at once was fifty million comparisons to say no fifty million times
            if (!group.Members.Add(sprite))
                continue;

            group.Sprites.Add(sprite);
            Count++;
        }

        Array.Clear(sprites, 0, taken);
    }

    /// <summary>
    /// Turns every sprite into the quads it is drawn as, for the frames that are drawn alongside the simulation. At the
    /// end of every tick, on the simulation thread.
    /// </summary>
    public override void Capture()
    {
        TakeQueued();

        if (_captured.BeginPublish() is { } batch)
        {
            batch.Model = Transform.ModelMatrix;
            batch.Camera = CustomCamera;
            batch.GroupCount = _groups.Count;

            for (int g = 0; g < _groups.Count; g++)
            {
                if (g == batch.Groups.Count)
                    batch.Groups.Add(new CapturedGroup());

                CaptureGroup(_groups[g], batch.Groups[g]);
            }
        }

        base.Capture();
    }

    /// <summary>
    /// Helper method to turn the sprites of a group into quads, the masks first, if any sprite is cut out with one, then
    /// the sprites themselves, each with which sprite it is.
    /// </summary>
    private static void CaptureGroup(SpriteGroup group, CapturedGroup into)
    {
        List<Sprite> sprites = group.Sprites;
        into.Group = group;

        bool masked = false;
        foreach (Sprite sprite in sprites)
        {
            if (sprite is { Enabled: true, UseStencilBuffer: true })
            {
                masked = true;
                break;
            }
        }

        int most = sprites.Count * (masked ? 2 : 1);
        if (into.Items.Length < most) into.Items = new SpriteItem[Math.Max(most, into.Items.Length * 2)];
        if (into.Sprites.Length < sprites.Count)
        {
            into.Sprites = new Sprite[Math.Max(sprites.Count, into.Sprites.Length * 2)];
            into.Epochs = new int[into.Sprites.Length];
        }

        int masks = 0;
        if (masked)
        {
            foreach (Sprite sprite in sprites)
            {
                if (sprite is { Enabled: true } && sprite.TryCreateItem(true, out SpriteItem item))
                    into.Items[masks++] = item;
            }
        }

        int colors = 0;
        foreach (Sprite sprite in sprites)
        {
            if (sprite is not { Enabled: true } || !sprite.TryCreateItem(false, out SpriteItem item))
                continue;

            into.Items[masks + colors] = item;
            into.Sprites[colors] = sprite;
            into.Epochs[colors] = sprite.Transform.Epoch;
            colors++;
        }

        // Whatever is left over from a tick that had more sprites is not drawn, but it is let go of
        Array.Clear(into.Sprites, colors, into.Sprites.Length - colors);

        into.Masks = masks;
        into.Colors = colors;
    }

    /// <summary>Draws all the sprites commited to this instance.</summary>
    public override void Render(float dt)
    {
        base.Render(dt);

        if (!Enabled)
            return;

        using var scope = GraphicsDevice.Current.BeginGpuScope("sprites");

        RenderFrame frame = RenderFrame.Active;
        if (frame.IsDecoupled)
        {
            RenderCaptured(frame);
            return;
        }

        // With the simulation standing still (a scene being set up) the sprites are there to be read as they are
        TakeQueued();

        Camera camera = CustomCamera ?? Engine.ActiveCamera;
        foreach (SpriteGroup group in _groups)
        {
            // Whatever the sprites asked for since the last frame is put into the atlas before they are drawn
            group.Atlas?.Update();

            foreach (Sprite sprite in group.Sprites)
            {
                if (!sprite.CastsShadows) continue;
                NoteCasters();
                break;
            }

            SpriteBatchMesh mesh = MeshOf(group);
            mesh.Draw(Transform.ModelMatrix, CollectionsMarshal.AsSpan(group.Sprites), camera, group.Texture);
        }
    }

    /// <summary>
    /// Helper method to tell the renderer we are drawn into that a sprite that blocks light was drawn this frame, so
    /// it knows whether there is a field of sprite shadows to build at all. Render thread.
    /// </summary>
    private static void NoteCasters()
    {
        if (Renderer2D.Current is DeferredRenderer2D lit) lit.NoteSpriteCasters();
    }

    /// <summary>Helper method to draw the quads of the last two ticks, blended to the moment the frame shows.</summary>
    private void RenderCaptured(in RenderFrame frame)
    {
        if (!_captured.TryGet(frame, out CapturedBatch previous, out CapturedBatch current, out bool continuous))
            return;

        Camera camera = current.Camera ?? Engine.ActiveCamera;
        float alpha = frame.Alpha;

        for (int g = 0; g < current.GroupCount; g++)
        {
            CapturedGroup now = current.Groups[g];
            if (now.Colors == 0)
                continue;

            // Groups only ever come after the ones there are, so the same group is in the same place in both
            CapturedGroup? before = continuous && g < previous.GroupCount && previous.Groups[g].Group == now.Group ? previous.Groups[g] : null;

            now.Group.Atlas?.Update();

            SpriteBatchMesh mesh = MeshOf(now.Group);
            Span<SpriteItem> items = mesh.BeginItems(now.Masks + now.Colors);
            if (items.IsEmpty)
            {
                mesh.EndItems();
                continue;
            }

            uint texture = now.Group.Texture.Index;
            int baseIndex = now.Masks;

            // The masks are drawn as they are now, they only say where the sprites may show
            for (int i = 0; i < baseIndex; i++)
            {
                SpriteItem mask = now.Items[i];
                mask.Flags = SpriteItem.WithTexture(mask.Flags, texture);
                items[i] = mask;
            }

            bool casters;
            if (before is null || alpha >= 1.0f)
            {
                casters = WriteCapturedUninterpolated(now.Items.AsSpan(baseIndex, now.Colors), items.Slice(baseIndex, now.Colors), texture);
            }
            else
            {
                casters = WriteCapturedBlended(
                    before.Items.AsSpan(before.Masks, before.Colors),
                    before.Sprites.AsSpan(0, before.Colors),
                    before.Epochs.AsSpan(0, before.Colors),
                    now.Items.AsSpan(baseIndex, now.Colors),
                    now.Sprites.AsSpan(0, now.Colors),
                    now.Epochs.AsSpan(0, now.Colors),
                    items.Slice(baseIndex, now.Colors),
                    alpha,
                    texture);
            }

            // Said once for the group rather than once for every sprite in it that throws a shadow
            if (casters) NoteCasters();

            mesh.DrawPasses(now.Masks, now.Colors, current.Model, camera);
            mesh.EndItems();
        }
    }

    /// <summary>Helper method for the uninterpolated fast path: copies current sprite items and stamps their bindless texture slot.</summary>
    internal static bool WriteCapturedUninterpolated(ReadOnlySpan<SpriteItem> current, Span<SpriteItem> destination, uint texture)
    {
        uint casterBits = 0;
        for (int i = 0; i < current.Length; i++)
        {
            SpriteItem drawn = current[i];
            casterBits |= drawn.Flags;
            drawn.Flags = SpriteItem.WithTexture(drawn.Flags, texture);
            destination[i] = drawn;
        }

        return (casterBits & SpriteItem.ShadowFlag) != 0;
    }

    /// <summary>Helper method for the continuous-blend path: skips CanBlend/Blend math for static sprites whose transform, color and ring didn't change.</summary>
    internal static bool WriteCapturedBlended(
        ReadOnlySpan<SpriteItem> previousItems,
        ReadOnlySpan<Sprite> previousSprites,
        ReadOnlySpan<int> previousEpochs,
        ReadOnlySpan<SpriteItem> currentItems,
        ReadOnlySpan<Sprite> currentSprites,
        ReadOnlySpan<int> currentEpochs,
        Span<SpriteItem> destination,
        float alpha,
        uint texture)
    {
        uint casterBits = 0;
        int shared = Math.Min(previousItems.Length, currentItems.Length);

        for (int i = 0; i < shared; i++)
        {
            ref readonly SpriteItem to = ref currentItems[i];
            casterBits |= to.Flags;

            SpriteItem drawn;
            if (!ReferenceEquals(previousSprites[i], currentSprites[i]))
            {
                // A sprite that wasn't drawn the tick before (or isn't in the same place among the others), as it is
                drawn = to;
            }
            else
            {
                ref readonly SpriteItem from = ref previousItems[i];
                if (previousEpochs[i] != currentEpochs[i])
                {
                    drawn = from;
                }
                else if (from.Origin == to.Origin && from.AxisX == to.AxisX && from.AxisY == to.AxisY && from.Color == to.Color && from.Ring == to.Ring)
                {
                    // Static sprite: Blend(from, to, alpha) with alpha < 1.0f returns from unchanged
                    drawn = from;
                }
                else
                {
                    drawn = SpriteItem.CanBlend(from, to) ? SpriteItem.Blend(from, to, alpha) : from;
                }
            }

            drawn.Flags = SpriteItem.WithTexture(drawn.Flags, texture);
            destination[i] = drawn;
        }

        for (int i = shared; i < currentItems.Length; i++)
        {
            SpriteItem drawn = currentItems[i];
            casterBits |= drawn.Flags;
            drawn.Flags = SpriteItem.WithTexture(drawn.Flags, texture);
            destination[i] = drawn;
        }

        return (casterBits & SpriteItem.ShadowFlag) != 0;
    }

    /// <summary>Helper method to get the mesh a group is drawn with, made the first time. Render thread.</summary>
    private SpriteBatchMesh MeshOf(SpriteGroup group)
    {
        if (!_meshes.TryGetValue(group, out var mesh))
        {
            mesh = group.Sheet is { } sheet ? new SpriteBatchMesh(sheet, Shader) : new SpriteBatchMesh(Shader);
            _meshes.Add(group, mesh);
        }

        return mesh;
    }

    protected override void DisposeOther()
    {
        foreach (SpriteBatchMesh mesh in _meshes.Values)
            mesh.Dispose();
        _meshes.Clear();

        _itemMesh?.Dispose();
        _itemMesh = null;

        base.DisposeOther();
    }
}
