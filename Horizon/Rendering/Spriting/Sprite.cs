using Horizon.Logging;
using System.Numerics;
using System.Runtime.CompilerServices;

using Horizon.Logging.Loggers;

using Horizon.Core.Components;
using Horizon.Core.Tweening;
using Horizon.Engine;
using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Graphics;

namespace Horizon.Rendering.Spriting;

public class Sprite : GameObject
{

    public SpriteSheet Spritesheet { get; protected set; }
    public SpriteSheetAnimationManager AnimationManager { get; protected set; }
    public SpriteBatch Batch { get; internal set; }

    /// <summary>
    /// The atlas the sprite is drawn out of, null for a sprite that shows a cell of a <see cref="SpriteSheet"/> instead.
    /// </summary>
    public TextureAtlas? Atlas { get; private set; }

    // What the frames of the sprite go by in the atlas, and which of them is showing
    private string[] _atlasFrames = [];
    private float _atlasFrameTime, _atlasFrameTimer;
    private int _atlasFrame;

    // Where the animations of a sprite out of an atlas are written down, for asking how long one is
    private SpriteSheetDefinition? _atlasDefinition;
    private string? _atlasTheme;

    /// <summary>
    /// Which frame of the animation is showing, for a sprite out of an atlas. Set it to pick the frame yourself
    /// (with <see cref="Animated"/> off, or it moves on from there), it's kept inside the animation.
    /// </summary>
    public int Frame
    {
        get => _atlasFrame;
        set => _atlasFrame = _atlasFrames.Length == 0 ? 0 : Math.Clamp(value, 0, _atlasFrames.Length - 1);
    }

    /// <summary>
    /// How many frames an animation has, for a sprite out of an atlas. 0 for one it hasn't got.
    /// </summary>
    public int GetFrameCount(string name)
    {
        if (_atlasDefinition is { } definition)
            return definition.TryGetSprite(name, _atlasTheme, out var source) ? source.Frames : 0;

        return AnimationManager is not null && AnimationManager.Animations.TryGetValue(name, out var found) ? (int)found.Length : 0;
    }

    /// <summary>
    /// Whether a sprite out of an atlas plays through its frames, off it stays on the one it is on.
    /// </summary>
    public bool Animated { get; set; } = true;

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
    /// Whether a flash makes the sprite a lamp, lighting what is round it in the path traced lighting (a glowing
    /// blob, a fire sprite), rather than only showing bright whatever the light is (a fighter hit). Off, because a
    /// fighter flashing white on every hit would light the whole arena with every punch.
    /// </summary>
    public bool FlashLights { get; set; }

    /// <summary>
    /// Whether the pixels of the sprite are blended where they meet, for pixel art that is drawn at a size that isn't
    /// a whole multiple of itself (see <see cref="SpriteItem.SmoothFlag"/>).
    /// </summary>
    public bool Smooth { get; set; }

    public bool UseStencilBuffer { get; set; } = false;

    /// <summary>
    /// Whether the sprite blocks light, for a <see cref="DeferredRenderer2D"/> whose lights cast shadows. Its shape
    /// as drawn (every pixel the texture covers) throws a shadow and bounces light, see <see cref="DeferredRenderer2D.SpriteShadows"/>.
    /// </summary>
    public bool CastsShadows { get; set; }

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

        _atlasFrames = atlas.Request(source);
        _atlasFrameTime = source.FrameTime;
        _atlasFrameTimer = 0.0f;
        _atlasFrame = 0;
        _atlasDefinition = definition;
        _atlasTheme = theme;

        this.Atlas = atlas;
        this.FrameName = name;
        return true;
    }

    public override void UpdateState(float dt)
    {
        // The tweens of the sprite move along in here, see SpriteTweens for the ones that come ready made
        base.UpdateState(dt);

        _motion.Update(Transform.Position, dt);

        // Sprites out of an atlas keep their own time, the ones of a sprite sheet leave it to their animation manager
        if (Atlas is null || !Animated || _atlasFrames.Length < 2 || _atlasFrameTime <= 0.0f) return;

        _atlasFrameTimer += dt;
        while (_atlasFrameTimer >= _atlasFrameTime)
        {
            _atlasFrameTimer -= _atlasFrameTime;
            _atlasFrame = (_atlasFrame + 1) % _atlasFrames.Length;
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
        AtlasRegion? trimmed = null;

        if (Atlas is { } atlas)
        {
            // The frame can change under us (it is advanced on the simulation thread), the array it indexes can't
            string[] frames = _atlasFrames;
            int frame = _atlasFrame;

            if (frames.Length == 0 || !atlas.TryGet(frames[frame < frames.Length ? frame : 0], out var region))
            {
                item = default;
                return false;
            }

            texMin = region.Position;
            texMax = region.Position + region.Size;
            trimmed = region.Trimmed ? region : null;
        }
        else
        {
            // a frame can span several cells of the sheet, the frames of an animation follow each other to the right
            Vector2 size = Spritesheet.SpriteSize * new Vector2(1 + GetFrameSpan(), 1);

            texMin = GetFrameOffset() * new Vector2(Spritesheet.Width, Spritesheet.Height) + new Vector2(size.X * GetFrameIndex(), 0);
            texMax = texMin + size;
        }

        bool flashed = FlashAmount > 0.0f;

        Matrix4x4 model = UseStencilBuffer && mask ? StencilTransform.ModelMatrix : Transform.ModelMatrix;

        // An atlas that trims kept only part of the frame, the quad shrinks to where that part was. The quad runs
        // from -0.5 to 0.5 with the top of the texture at the top, so a frame's rows count down from there
        if (trimmed is { } cut)
        {
            Vector2 scale = cut.Size / cut.FrameSize;
            Vector2 centre = new(
                (cut.Offset.X + cut.Size.X * 0.5f) / cut.FrameSize.X - 0.5f,
                0.5f - (cut.Offset.Y + cut.Size.Y * 0.5f) / cut.FrameSize.Y);
            model = Matrix4x4.CreateScale(scale.X, scale.Y, 1.0f) * Matrix4x4.CreateTranslation(centre.X, centre.Y, 0.0f) * model;
        }

        item = SpriteItem.FromModel(
            model,
            texMin,
            texMax,
            SpriteItem.PackColor(flashed ? FlashColor with { W = Tint.W } : Tint),
            (Smooth ? SpriteItem.SmoothFlag : 0) | (flashed ? SpriteItem.FlashFlag : 0) | (flashed && FlashLights ? SpriteItem.LampFlag : 0) | (CastsShadows && !mask ? SpriteItem.ShadowFlag : 0));
        item.Motion = _motion.Velocity;
        item.Ring = flashed ? MathF.Min(FlashAmount, 1.0f) : 0.0f;
        return true;
    }

    /// <summary>
    /// Switches to another animation, from its first frame. A sprite out of an atlas says whether it has it (false
    /// leaves it on the one it's on), and nothing is asked of the atlas again for the one it's on already.
    /// </summary>
    public bool SetAnimation(string name)
    {
        if (Atlas is { } atlas && _atlasDefinition is { } definition)
        {
            if (FrameName == name) return true;
            return ConfigureAtlas(atlas, definition, name, _atlasTheme);
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