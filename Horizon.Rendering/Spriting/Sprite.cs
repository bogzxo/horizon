using Bogz.Logging;
using System.Numerics;
using System.Runtime.CompilerServices;

using Bogz.Logging.Loggers;

using Horizon.Core.Components;
using Horizon.Core.Tweening;
using Horizon.Engine;
using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.OpenGL.Descriptions;

namespace Horizon.Rendering.Spriting;

public class Sprite : GameObject
{
    private bool _hasBeenSetup = false;

    public SpriteSheet Spritesheet { get; protected set; }
    public SpriteSheetAnimationManager AnimationManager { get; protected set; }
    public SpriteBatch Batch { get; internal set; }

    /// <summary>
    /// The atlas the sprite is drawn out of, null for a sprite that shows a cell of a <see cref="SpriteSheet"/> instead.
    /// </summary>
    public TextureAtlas? Atlas { get; private set; }

    /// <summary>
    /// One animation of a sprite out of an atlas: what its frames go by in the atlas and how it plays.
    /// </summary>
    private sealed record AtlasAnimation(string Name, string[] Frames, float FrameTime, float[]? FrameTimes, bool Loops)
    {
        public float TimeOf(int frame) => FrameTimes is { } times && frame < times.Length ? times[frame] : FrameTime;
    }

    private static readonly AtlasAnimation NoAnimation = new(string.Empty, [], 0.0f, null, true);

    // Where the art of a sprite out of an atlas is written down, and the animations of it that have been asked for so far
    private SpriteSheetDefinition? _definition;
    private string? _theme;
    private readonly Dictionary<string, AtlasAnimation> _animations = [];

    // The animation that is showing and which of its frames. Swapped whole, the render thread reads it while it plays
    private volatile AtlasAnimation _animation = NoAnimation;
    private float _atlasFrameTimer;
    private volatile int _atlasFrame;

    /// <summary>
    /// Whether a sprite out of an atlas plays through its frames, off it stays on the one it is on. Off is for whoever
    /// decides the frame themselves, see <see cref="Frame"/>.
    /// </summary>
    public bool Animated { get; set; } = true;

    /// <summary>
    /// Which frame of its animation a sprite out of an atlas is showing, counted from 0. Setting one past the end shows the last.
    /// </summary>
    public int Frame
    {
        get => _atlasFrame;
        set
        {
            _atlasFrame = Math.Clamp(value, 0, Math.Max(0, _animation.Frames.Length - 1));
            _atlasFrameTimer = 0.0f;
        }
    }

    /// <summary>
    /// How many frames the animation that is showing has.
    /// </summary>
    public int FrameCount => _animation.Frames.Length;

    /// <summary>
    /// Whether an animation that doesn't go round and round has got to its last frame. Always false for one that does.
    /// </summary>
    public bool IsFinished => _animation is { Loops: false } animation && _atlasFrame >= animation.Frames.Length - 1;

    /// <summary>
    /// Multiplied into the colours of the sprite.
    /// </summary>
    public Vector4 Tint { get; set; } = Vector4.One;

    /// <summary>
    /// The colour the sprite is flashed with and how much of it there is, from 0 for none to 1 for nothing but the colour
    /// in the shape of the sprite. Unlike a <see cref="Tint"/> this can make a sprite brighter, and it glows in the dark.
    /// While there is any of it the tint is left out except for its alpha. See <see cref="SpriteTweens.Flash"/>.
    /// </summary>
    public Vector4 FlashColor { get; set; } = Vector4.One;
    public float FlashAmount { get; set; }

    /// <summary>
    /// Whether the pixels of the sprite are blended where they meet, for pixel art that is drawn at a size that isn't
    /// a whole multiple of itself (see <see cref="SpriteItem.SmoothFlag"/>).
    /// </summary>
    public bool Smooth { get; set; }

    public bool UseStencilBuffer { get; set; } = false;

    // How fast the sprite is going, for a renderer that blurs motion
    private readonly Horizon.Core.MotionEstimator _motion = new();

    /// <summary>
    /// How fast the sprite is moving across the world, in units a second. Worked out from where it is every update,
    /// so only a sprite that is updated (one that was added to something as an entity) ever has any.
    /// </summary>
    public Vector2 Velocity => _motion.Velocity;

    /// <summary>
    /// Whether the sprite has been told what to show, by either <see cref="ConfigureSpriteSheet"/> or <see cref="ConfigureAtlas"/>.
    /// </summary>
    internal bool IsConfigured => Atlas is not null || AnimationManager is not null;

    public bool Flipped
    {
        set
        {
            if (Transform.Size.X < 0 && value)
                return;
            if (Transform.Size.X > 0 && !value)
                return;

            Transform.Size = new Vector2(-Transform.Size.X, Transform.Size.Y);
        }
        get => Transform.Size.X < 0;
    }

    //public bool IsAnimated { get; set; }
    public string FrameName { get; private set; }

    public virtual TransformComponent2D Transform { get; init; }
    public virtual TransformComponent2D StencilTransform { get; init; }


    public Sprite(in Vector2 size)
    {
        this.Transform = AddComponent<TransformComponent2D>();
        this.Transform.Size = size;

        this.StencilTransform = new();
        this.StencilTransform.Size = size;
    }

    /// <summary>
    /// Adds the animation.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="position">The position in normalized coordinates.</param>
    /// <param name="length">The animation length in frames.</param>
    /// <param name="frameTime">The frame time.</param>
    /// <param name="inSize">Custom frame size.</param>
    public void AddAnimation(
        string name,
        Vector2 position,
        uint length,
        float frameTime = 0.1f,
        Vector2? inSize = null
    )
    {
        AnimationManager ??= AddComponent(new SpriteSheetAnimationManager(inSize!.Value));
        AnimationManager.AddAnimation(name, position, length, frameTime, inSize);
    }

    /// <summary>
    /// Adds a range of animations.
    /// </summary>
    public void AddAnimationRange(
        (string name, Vector2 position, uint length, float frameTime, Vector2? inSize)[] animations
    )
    {
        foreach (var (name, position, length, frameTime, inSize) in animations)
            AddAnimation(name, position, length, frameTime, inSize);
    }

    /// <summary>
    /// Gets the texture coordinates with respect to the configured sprite sheet.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Vector2[] GetAnimatedTextureCoordinates(string name)
    {
        if (!AnimationManager.Animations.TryGetValue(name, out var sprite))
        {
            //ConcurrentLogger.Instance.Log(
            //    Logging.LogLevel.Error,
            //    $"Attempt to get sprite '{name}' which doesn't exist!"
            //); TODO: FIX
            return Array.Empty<Vector2>();
        }

        // Calculate texture coordinates for the sprite
        Vector2 topLeftTexCoord = Vector2.Zero;
        Vector2 bottomRightTexCoord = topLeftTexCoord + Spritesheet.SingleSpriteSize;

        return new Vector2[]
        {
            topLeftTexCoord,
            new Vector2(bottomRightTexCoord.X, topLeftTexCoord.Y),
            bottomRightTexCoord,
            new Vector2(topLeftTexCoord.X, bottomRightTexCoord.Y)
        };
    }


    public void ConfigureSpriteSheet(SpriteSheet spriteSheet, string name)
    {
        this.Spritesheet = (spriteSheet);
        this.AnimationManager ??= AddComponent(new SpriteSheetAnimationManager(spriteSheet));

        this.FrameName = name;

        //this.IsAnimated = AnimationManager.Animations.Any();

        _hasBeenSetup = true;
    }

    /// <summary>
    /// Makes the sprite show a named sprite of a <see cref="SpriteSheetDefinition"/>, drawn out of an atlas that only
    /// holds the sprites that are used. Sprites that share an atlas are drawn together whichever image their art is from.
    /// The art is put into the atlas the next time the batch draws, the sprite shows nothing until then.
    /// </summary>
    /// <param name="theme">The theme to find the sprite in, for definitions that hold their art in several colours.</param>
    /// <returns>False if the definition has no sprite by that name, the sprite is left as it was.</returns>
    public bool ConfigureAtlas(TextureAtlas atlas, SpriteSheetDefinition definition, string name, string? theme = null)
    {
        if (!definition.TryGetSprite(name, theme, out var source))
        {
            Log.Error($"[Sprite] '{definition.Path}' has no sprite called '{name}'!");
            return false;
        }

        // Another atlas or another definition, whatever was asked for before is no good any more
        if (!ReferenceEquals(Atlas, atlas) || !ReferenceEquals(_definition, definition) || _theme != theme)
            _animations.Clear();

        this.Atlas = atlas;
        _definition = definition;
        _theme = theme;

        Show(_animations[name] = new AtlasAnimation(name, atlas.Request(source), source.FrameTime, source.FrameTimes, source.Loops));

        _hasBeenSetup = true;
        return true;
    }

    /// <summary>
    /// Helper method to find an animation of a sprite out of an atlas by name, and ask the atlas for its frames the first
    /// time. Null for a name the definition doesn't have.
    /// </summary>
    private AtlasAnimation? AnimationOf(string name)
    {
        if (_animations.TryGetValue(name, out var known)) return known;
        if (Atlas is null || _definition is null || !_definition.TryGetSprite(name, _theme, out var source)) return null;

        return _animations[name] = new AtlasAnimation(name, Atlas.Request(source), source.FrameTime, source.FrameTimes, source.Loops);
    }

    private void Show(AtlasAnimation animation)
    {
        _atlasFrame = 0;
        _atlasFrameTimer = 0.0f;
        _animation = animation;
        this.FrameName = animation.Name;
    }

    /// <summary>
    /// Asks the atlas for the frames of these sprites of the definition now, rather than the first time each of them is
    /// shown. Art that is asked for isn't there until the atlas has been updated, which a sprite in the middle of a fight
    /// can't wait for. Only for a sprite out of an atlas, after <see cref="ConfigureAtlas"/>.
    /// </summary>
    public void Preload(IEnumerable<string> names)
    {
        foreach (string name in names) AnimationOf(name);
    }

    /// <summary>
    /// How many frames an animation of the sprite has, 0 for one it doesn't have.
    /// </summary>
    public int GetFrameCount(string name)
    {
        if (Atlas is not null) return AnimationOf(name)?.Frames.Length ?? 0;

        return AnimationManager is not null && AnimationManager.Animations.TryGetValue(name, out var animation) ? (int)animation.Length : 0;
    }

    /// <summary>
    /// Test if the sprite has an animation by this name.
    /// </summary>
    public bool HasAnimation(string name) =>
        Atlas is not null ? _definition?.Has(name) == true : AnimationManager?.Animations.ContainsKey(name) == true;

    public override void UpdateState(float dt)
    {
        // The tweens of the sprite move along in here, see SpriteTweens for the ones that come ready made
        base.UpdateState(dt);

        _motion.Update(Transform.Position, dt);

        // Sprites out of an atlas keep their own time, the ones of a sprite sheet leave it to their animation manager
        AtlasAnimation animation = _animation;
        if (Atlas is null || !Animated || animation.Frames.Length < 2) return;

        _atlasFrameTimer += dt;
        while (true)
        {
            int frame = _atlasFrame;
            float time = animation.TimeOf(frame);
            if (time <= 0.0f || _atlasFrameTimer < time) break;

            // One that plays once stays on its last frame
            if (!animation.Loops && frame >= animation.Frames.Length - 1)
            {
                _atlasFrameTimer = 0.0f;
                break;
            }

            _atlasFrameTimer -= time;
            _atlasFrame = (frame + 1) % animation.Frames.Length;
        }
    }

    /// <summary>
    /// Helper method to describe the sprite to the renderer as it is right now.
    /// </summary>
    /// <param name="mask">Whether this is for the stencil pass, where a sprite that has a mask is drawn as that instead.</param>
    /// <returns>False while there is nothing to draw, the art of a sprite out of an atlas isn't there until the atlas has been updated.</returns>
    internal bool TryCreateItem(bool mask, out SpriteItem item)
    {
        Vector2 texMin, texMax;

        // For art that had its see-through edges left out of the atlas, the part of the sprite that is left of it
        AtlasRegion trimmed = default;

        if (Atlas is { } atlas)
        {
            // The frame can change under us (it is advanced on the simulation thread), the array it indexes can't
            string[] frames = _animation.Frames;
            int frame = _atlasFrame;

            if (frames.Length == 0 || !atlas.TryGet(frames[frame < frames.Length ? frame : 0], out var region) || region.IsEmpty)
            {
                item = default;
                return false;
            }

            texMin = region.Position;
            texMax = region.Position + region.Size;

            if (region.SourceSize != default) trimmed = region;
        }
        else
        {
            // a frame can span several cells of the sheet, the frames of an animation follow each other to the right
            Vector2 size = Spritesheet.SpriteSize * new Vector2(1 + GetFrameSpan(), 1);

            texMin = GetFrameOffset() * new Vector2(Spritesheet.Width, Spritesheet.Height) + new Vector2(size.X * GetFrameIndex(), 0);
            texMax = texMin + size;
        }

        bool flashed = FlashAmount > 0.0f;

        item = SpriteItem.FromModel(
            UseStencilBuffer && mask ? StencilTransform.ModelMatrix : Transform.ModelMatrix,
            texMin,
            texMax,
            SpriteItem.PackColor(flashed ? FlashColor with { W = Tint.W } : Tint),
            (Smooth ? SpriteItem.SmoothFlag : 0) | (flashed ? SpriteItem.FlashFlag : 0));
        item.Motion = _motion.Velocity;
        item.Ring = flashed ? MathF.Min(FlashAmount, 1.0f) : 0.0f;

        if (trimmed.SourceSize != default)
        {
            // The quad is the whole frame. What is drawn is the part of it the art takes up, which is counted from the
            // top left in the atlas and from the bottom left in the world
            Vector2 full = trimmed.SourceSize;
            float left = trimmed.Offset.X / full.X;
            float bottom = (full.Y - trimmed.Offset.Y - trimmed.Size.Y) / full.Y;

            item.Origin += item.AxisX * left + item.AxisY * bottom;
            item.AxisX *= trimmed.Size.X / full.X;
            item.AxisY *= trimmed.Size.Y / full.Y;
        }

        return true;
    }

    /// <summary>
    /// Changes the animation the sprite shows. For a sprite out of an atlas that is any sprite of the definition it was
    /// configured with, started from its first frame (asking for the one that is showing already changes nothing).
    /// For a sprite of a sprite sheet it is one of the animations of its animation manager.
    /// </summary>
    /// <returns>False if a sprite out of an atlas has no animation by that name, it carries on with the one it has.</returns>
    public bool SetAnimation(string name)
    {
        if (Atlas is not null && _definition is not null)
        {
            if (_animation.Name == name) return true;
            if (AnimationOf(name) is not { } animation) return false;

            Show(animation);
            return true;
        }

        this.FrameName = name;
        return true;
    }

    public Vector2 GetFrameOffset()
    {
        var (definition, _) = AnimationManager[FrameName];

        return (definition.Position * Spritesheet.SingleSpriteSize);
    }

    public Vector2 GetSize() => Spritesheet.SpriteSize * new Vector2(GetFrameSpan(), 1);

    /// <summary>
    /// This is the amount of tiles in the right direction (+X) that the sprite goes on in the spritesheet.
    /// </summary>
    public uint GetFrameSpan()
    {
        return AnimationManager[FrameName].definition.Span;
    }
    public uint GetFrameIndex()
    {
        var (_, index) = AnimationManager[FrameName];
        return index;
    }
}