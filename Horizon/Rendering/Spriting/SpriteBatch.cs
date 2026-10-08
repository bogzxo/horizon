using Horizon.Logging;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using Horizon.Core.Components;
using Horizon.Core.Threading;
using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Descriptions;
using Horizon.Rendering.Spriting.Components;

namespace Horizon.Rendering.Spriting;

/// <summary>
/// An alternative (high performance) rendering back end for rendering a collection of dynamic sprites.
/// Besides the sprites added to it, it draws anything that can be described as a list of <see cref="SpriteItem"/>s
/// through the very same shader and buffers, see <see cref="Draw(ReadOnlySpan{SpriteItem}, ReadOnlySpan{SpriteRun}, Camera?)"/>.
/// <para>
/// Drawn alongside the simulation (see <see cref="RenderFrame.IsDecoupled"/>), the sprites are turned into quads at the
/// end of every tick (<see cref="Capture"/>) and every frame draws them between the last two ticks: each quad goes the
/// same distance for the same time from frame to frame, however many frames there are. A sprite that is flipped over or
/// put somewhere else (see <see cref="TransformComponent2D.Snap"/>) is not shown on its way.
/// </para>
/// </summary>
public class SpriteBatch : GameObject
{
    /// <summary>
    /// A Camera used to render all sprite meshes against, if null this defaults to the scene camera.
    /// </summary>
    public Camera? CustomCamera { get; set; }

    /// <summary>
    /// The global transform for all sprite meshes.
    /// </summary>
    public TransformComponent2D Transform { get; private set; }

    /// <summary>How near sprites are unless their batch says otherwise: in front of a map, behind its foreground.</summary>
    public const float DEFAULT_NEARNESS = 0.6f;

    /// <summary>
    /// How near everything this batch draws is, from 0 (the backdrop) to 1 (right in front). Only a renderer that
    /// blurs motion goes by it (see <see cref="DeferredRenderer2D"/>): what is nearer blurs over what is further away
    /// when it moves, and stays sharp when what is behind it does.
    /// </summary>
    public float Nearness { get; set; } = DEFAULT_NEARNESS;

    /// <summary>
    /// The sprites that are drawn out of one texture (a sprite sheet, or an atlas), which go in one draw call. They belong
    /// to the simulation: sprites are added, removed and turned into quads there.
    /// </summary>
    private sealed class SpriteGroup(SpriteSheet? sheet, TextureAtlas? atlas)
    {
        public readonly SpriteSheet? Sheet = sheet;
        public readonly TextureAtlas? Atlas = atlas;
        public readonly List<Sprite> Sprites = [];

        public SpriteTexture Texture => Atlas is { } a ? new SpriteTexture(a.Texture) : new SpriteTexture(Sheet!);
    }

    /// <summary>
    /// What a group drew as of one tick: a quad for every sprite (and one for the mask of every sprite, if any of them is
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

    /// <summary>
    /// What the whole batch drew as of one tick.
    /// </summary>
    private sealed class CapturedBatch
    {
        public readonly List<CapturedGroup> Groups = [];
        public int GroupCount;
        public Matrix4x4 Model;
        public float Nearness;
        public Camera? Camera;
    }

    /// <summary>
    /// Gets the shader.
    /// </summary>
    public Technique Shader { get; set; }

    // The groups by what they are drawn out of, and in the order they came, which never changes for a group
    // TODO please remind me to make a custom datastruct for this shit
    private readonly Dictionary<uint, SpriteGroup> _sheetGroups = new();
    private readonly Dictionary<TextureAtlas, SpriteGroup> _atlasGroups = new();
    private readonly List<SpriteGroup> _groups = [];

    // What the GPU draws a group with. The render thread's, made the first time the group is drawn
    private readonly Dictionary<SpriteGroup, SpriteBatchMesh> _meshes = new();

    private readonly SnapshotBuffer<CapturedBatch> _captured = new(static () => new CapturedBatch());

    public int Count { get; private set; }

    private ConcurrentStack<Sprite> _queuedSprites = new();

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
    /// <param name="textures">The textures the items refer to, <see cref="SpriteBatchMesh.MaxTextures"/> at the most.</param>
    /// <param name="camera">The camera to draw with, if null this defaults to the custom camera and then the scene camera.</param>
    public void Draw(ReadOnlySpan<SpriteItem> items, ReadOnlySpan<SpriteTexture> textures, Camera? camera = null)
    {
        ReadOnlySpan<SpriteRun> runs = [new SpriteRun(0, items.Length)];
        Draw(items, runs, camera, textures);
    }

    /// <summary>
    /// Draws items right now, split up into runs for when they show more textures between them than fit in one call.
    /// The items are only copied to the GPU once however many runs there are.
    /// </summary>
    /// <param name="shared">The textures in the first slots of every run, the ones of the run itself come after them.</param>
    public void Draw(ReadOnlySpan<SpriteItem> items, ReadOnlySpan<SpriteRun> runs, Camera? camera = null, ReadOnlySpan<SpriteTexture> shared = default)
    {
        if (!Enabled || items.IsEmpty || Shader is null)
            return;

        PrepareItems();
        camera ??= CustomCamera ?? Engine.ActiveCamera;

        Span<SpriteItem> buffer = _itemMesh!.BeginItems(items.Length);
        if (!buffer.IsEmpty)
        {
            items.CopyTo(buffer);

            Span<SpriteTexture> textures = stackalloc SpriteTexture[SpriteBatchMesh.MaxTextures];
            foreach (var run in runs)
            {
                int count = 0;
                foreach (var texture in shared)
                    if (count < textures.Length) textures[count++] = texture;
                if (run.Texture0.Handle != 0 && count < textures.Length) textures[count++] = run.Texture0;
                if (run.Texture1.Handle != 0 && count < textures.Length) textures[count++] = run.Texture1;

                _itemMesh!.Nearness = Nearness;
                _itemMesh!.DrawItems(run.First, run.Count, textures[..count], Transform.ModelMatrix, camera);
            }
        }

        _itemMesh!.EndItems();
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

    /// <summary>
    /// Commits an object to be rendered.
    /// </summary>
    /// <param name="sprite"></param>
    public void Add(in Sprite sprite) => _queuedSprites.Push(sprite);

    /// <summary>
    /// Commits an object to be rendered.
    /// </summary>
    /// <param name="sprite"></param>
    public void AddRange(in Sprite[] sprites) => _queuedSprites.PushRange(sprites);

    /// <summary>
    /// Stops drawing a sprite. From the updates.
    /// </summary>
    public void Remove(in Sprite sprite)
    {
        if (GroupOf(sprite, create: false) is not { } group)
            return;

        if (group.Sprites.Remove(sprite))
            Count--;
    }

    /// <summary>
    /// Helper method to find the group a sprite is drawn in by what it is drawn out of, made if it isn't there yet and asked to.
    /// </summary>
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
        Sprite[] sprites = new Sprite[length];
        int taken = _queuedSprites.TryPopRange(sprites);

        // In the order they come off the stack, which is the order they have always been drawn in
        for (int i = 0; i < taken; i++)
        {
            Sprite sprite = sprites[i];
            sprite.Batch = this;

            if (!sprite.IsConfigured || GroupOf(sprite, create: true) is not { } group)
            {
                // Sprite not yet initialized
                _queuedSprites.Push(sprite);
                continue;
            }

            if (group.Sprites.Contains(sprite))
                continue;

            group.Sprites.Add(sprite);
            Count++;
        }
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
            batch.Nearness = Nearness;
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
    /// Helper method to turn the sprites of a group into quads: the masks first, if any sprite is cut out with one, then
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

    /// <summary>
    /// Draws all the sprites commited to this instance.
    /// </summary>
    /// <param name="dt">Delta time.</param>
    public override void Render(float dt)
    {
        base.Render(dt);

        if (!Enabled)
            return;

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

            SpriteBatchMesh mesh = MeshOf(group);
            mesh.Nearness = Nearness;
            mesh.Draw(Transform.ModelMatrix, CollectionsMarshal.AsSpan(group.Sprites), camera, group.Texture);
        }
    }

    /// <summary>
    /// Helper method to draw the quads of the last two ticks, blended to the moment the frame shows.
    /// </summary>
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

            // The masks are drawn as they are now, they only say where the sprites may show
            now.Items.AsSpan(0, now.Masks).CopyTo(items);

            for (int i = 0; i < now.Colors; i++)
            {
                ref readonly SpriteItem to = ref now.Items[now.Masks + i];

                if (before is null || i >= before.Colors || before.Sprites[i] != now.Sprites[i])
                {
                    // A sprite that wasn't drawn the tick before (or isn't in the same place among the others): as it is
                    items[now.Masks + i] = to;
                    continue;
                }

                ref readonly SpriteItem from = ref before.Items[before.Masks + i];
                items[now.Masks + i] = before.Epochs[i] == now.Epochs[i] && SpriteItem.CanBlend(from, to)
                    ? SpriteItem.Blend(from, to, alpha)
                    : alpha >= 1.0f ? to : from;
            }

            mesh.Nearness = current.Nearness;
            ReadOnlySpan<SpriteTexture> textures = [now.Group.Texture];
            mesh.DrawPasses(now.Masks, now.Colors, textures, current.Model, camera);
            mesh.EndItems();
        }
    }

    /// <summary>
    /// Helper method to get the mesh a group is drawn with, made the first time. Render thread.
    /// </summary>
    private SpriteBatchMesh MeshOf(SpriteGroup group)
    {
        if (!_meshes.TryGetValue(group, out var mesh))
        {
            mesh = group.Sheet is { } sheet ? new SpriteBatchMesh(sheet, Shader) : new SpriteBatchMesh(Shader);
            _meshes.Add(group, mesh);
        }

        return mesh;
    }
}
