using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Descriptions;
using Horizon.Rendering.Spriting.Components;

namespace Horizon.Rendering.Spriting;

/// <summary>
/// An alternative (high performance) rendering back end for rendering a collection of dynamic sprites.
/// Besides the sprites added to it, it draws anything that can be described as a list of <see cref="SpriteItem"/>s
/// through the very same shader and buffers, see <see cref="Draw(ReadOnlySpan{SpriteItem}, ReadOnlySpan{SpriteRun}, Camera?)"/>.
/// </summary>
/// <seealso cref="Horizon.GameEntity.Entity" />
/// <seealso cref="Horizon.Rendering.Spriting.I2DBatchedRenderer&lt;Horizon.Rendering.Spriting.Sprite&gt;" />
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
    /// Helper struct to aggregate data related to rendering a series of sprites with a common sprite sheet.
    /// </summary>
    private class SpriteSheetRenderObject
    {
        public SpriteBatchMesh Mesh;

        //public ushort Index;
        public List<Sprite> Sprites;

        public SpriteSheetRenderObject(in SpriteBatchMesh mesh)
        {
            this.Mesh = mesh;
            this.Sprites = new();
        }

        /// <summary>
        /// Adds the specified sprite, performing an additional check to ensure we don't already contain the specified sprite.
        /// </summary>
        /// <param name="sprite">The sprite.</param>
        public void Add(in Sprite sprite)
        {
            // Ensure we don't already contain this sprite.
            if (Sprites.Contains(sprite))
                return;

            Sprites.Add(sprite);
        }

        public void AddRange(in Sprite[] sprites)
        {
            Sprites.AddRange(sprites);
        }

        public void AddRange(in List<Sprite> sprites)
        {
            Sprites.AddRange(sprites);
        }
    }
    /// <summary>
    /// Gets the shader.
    /// </summary>
    public Technique Shader { get; set; }

    /// <summary>
    /// TODO please remind me to make a custom datastruct for this shit
    /// </summary>
    /// <value>
    private Dictionary<uint, SpriteSheetRenderObject> SpritesheetSprites { get; } = new();

    /// <summary>
    /// The sprites that are drawn out of an atlas rather than a sprite sheet of their own, by atlas.
    /// </summary>
    private Dictionary<TextureAtlas, SpriteSheetRenderObject> AtlasSprites { get; } = new();

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
        if (Engine
            .ObjectManager
            .Shaders
            .TryCreateOrGet(
                "sprite",
                ShaderDescription.FromPath("shaders/spritebatch", "sprites"),
                out var result))
        {
            this.Shader = new Technique(result.Asset);
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
        }

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

    public void Remove(in Sprite sprite)
    {
        if (sprite.Atlas is not null)
        {
            if (AtlasSprites.TryGetValue(sprite.Atlas, out var atlasSprites))
                atlasSprites.Sprites.Remove(sprite);
            return;
        }

        if (!SpritesheetSprites.ContainsKey(sprite.Spritesheet.Handle))
            return;
        if (!SpritesheetSprites[sprite.Spritesheet.Handle].Sprites.Contains(sprite))
            return;

        SpritesheetSprites[sprite.Spritesheet.Handle].Sprites.Remove(sprite);
    }

    /// <summary>
    /// Draws all the sprites commited to this instance.
    /// </summary>
    /// <param name="dt">Delta time.</param>
    /// <param name="options">Render options (optional).</param>
    public override void Render(float dt, object? obj = null)
    {
        base.Render(dt);

        if (!Enabled)
            return;

        if (!_queuedSprites.IsEmpty)
        {
            int length = _queuedSprites.Count;
            Sprite[] sprites = new Sprite[length];
            if (_queuedSprites.TryPopRange(sprites) == length)
            {
                // Sort sprites into groups via their sprite sheet and setup the SpritesheetSprites key/value pair.
                Dictionary<uint, List<Sprite>> spriteSpriteSheetPairs = new();
                for (int i = 0; i < sprites.Length; i++)
                {
                    sprites[i].Batch = this;

                    if (!sprites[i].IsConfigured)
                    {
                        // Sprite not yet initialized
                        _queuedSprites.Push(sprites[i]);
                    }
                    else if (sprites[i].Atlas is { } atlas)
                    {
                        // Sprites out of the same atlas are drawn together, whichever image their art came from
                        if (!AtlasSprites.TryGetValue(atlas, out var atlasSprites))
                        {
                            AtlasSprites.Add(atlas, atlasSprites = new SpriteSheetRenderObject(new SpriteBatchMesh(Shader)));
                        }

                        atlasSprites.Add(sprites[i]);
                        Count++;
                    }
                    else
                    {
                        spriteSpriteSheetPairs.TryAdd(sprites[i].Spritesheet.Handle, new());
                        spriteSpriteSheetPairs[sprites[i].Spritesheet.Handle].Add(sprites[i]);

                        if (!SpritesheetSprites.ContainsKey(sprites[i].Spritesheet.Handle))
                        {
                            SpritesheetSprites.TryAdd(
                                sprites[i].Spritesheet.Handle,
                                new SpriteSheetRenderObject(new(sprites[i].Spritesheet, Shader))
                            );
                        }
                    }
                }

                // Ensure the spritebatch is configured to render all the sprite sheets.
                foreach ((var sheet, var storedSprites) in spriteSpriteSheetPairs)
                {
                    SpritesheetSprites[sheet].AddRange(storedSprites);
                    Count += storedSprites.Count;
                }
            }
        }

        foreach (var (_, renderData) in SpritesheetSprites)
        {
            renderData.Mesh.Nearness = Nearness;
            renderData
                .Mesh
                .Draw( Transform.ModelMatrix,
                    CollectionsMarshal.AsSpan(renderData.Sprites),
                    CustomCamera ?? Engine.ActiveCamera
                );
        }

        foreach (var (atlas, renderData) in AtlasSprites)
        {
            renderData.Mesh.Nearness = Nearness;

            // Whatever the sprites asked for since the last frame is put into the atlas before they are drawn
            atlas.Update();

            renderData
                .Mesh
                .Draw( Transform.ModelMatrix,
                    CollectionsMarshal.AsSpan(renderData.Sprites),
                    CustomCamera ?? Engine.ActiveCamera,
                    new SpriteTexture(atlas.Texture)
                );
        }
    }
}