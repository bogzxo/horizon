using Horizon.Logging;
using System.Numerics;

using Horizon.Core.Components;
using Horizon.Core.Tweening;
using Horizon.Engine;
using Horizon.Graphics;

namespace Horizon.Rendering.Spriting;

/// <summary>
/// Something a <see cref="SpriteBatch"/> draws, a picture out of a sprite sheet or an atlas with a place, a size, a
/// tint and whatever animation it is playing. Make one, tell it what to show (<see cref="ConfigureSpriteSheet"/> or
/// <see cref="ConfigureAtlas"/>) and add it to a batch, it draws nothing by itself.
/// </summary>
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

    // The same frames as the atlas keeps them, with where each one ended up once that is known, see AtlasFrames
    private AtlasFrames? _atlasCache;
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
    /// Adds an animation, a run of frames next to each other on the sprite sheet.
    /// </summary>
    /// <param name="name">What it is called, which is what <see cref="SetAnimation"/> asks for.</param>
    /// <param name="position">Where its first frame is on the sheet, counted in sprites from the top left.</param>
    /// <param name="length">How many frames it has.</param>
    /// <param name="frameTime">How long every frame is shown for, in seconds.</param>
    /// <param name="inSize">How big a frame is in texels, the size of the sprite if nobody says.</param>
    public void AddAnimation(
        string name,
        Vector2 position,
        uint length,
        float frameTime = 0.1f,
        Vector2? inSize = null
    )
    {
        // Without a size the frames are as big as the sprite is drawn, which blew up with a null before
        AnimationManager ??= AddComponent(new SpriteSheetAnimationManager(inSize ?? Vector2.Abs(Transform.Size)));
        AnimationManager.AddAnimation(name, position, length, frameTime, inSize);
    }

    /// <summary>
    /// Adds a whole lot of animations in one go.
    /// </summary>
    public void AddAnimationRange(
        (string name, Vector2 position, uint length, float frameTime, Vector2? inSize)[] animations
    )
    {
        foreach (var (name, position, length, frameTime, inSize) in animations)
            AddAnimation(name, position, length, frameTime, inSize);
    }

    public void ConfigureSpriteSheet(SpriteSheet spriteSheet, string name)
    {
        this.Spritesheet = (spriteSheet);
        this.AnimationManager ??= AddComponent(new SpriteSheetAnimationManager(spriteSheet));

        this.FrameName = name;
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

        // The atlas hands back the same frames for the same sprite every time, so changing animation is a look up
        // and not a fresh pile of strings
        AtlasFrames frames = atlas.Frames(source);
        _atlasCache = frames;
        _atlasFrames = frames.Keys;
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
        Matrix4x4 model = UseStencilBuffer && mask ? StencilTransform.ModelMatrix : Transform.ModelMatrix;

        if (Atlas is { } atlas)
        {
            // The frame can change under us (it is advanced on the simulation thread), what it indexes can't
            AtlasFrames? frames = _atlasCache;
            int frame = _atlasFrame;

            if (frames is null || frames.Keys.Length == 0 || !frames.TryGet(atlas, frame < frames.Keys.Length ? frame : 0, out AtlasRegion region))
            {
                item = default;
                return false;
            }

            texMin = region.Position;
            texMax = region.Position + region.Size;

            // An atlas that trims kept only part of the frame, the quad shrinks to where that part was. The quad runs
            // from -0.5 to 0.5 with the top of the texture at the top, so a frame's rows count down from there
            if (region.Trimmed)
            {
                Vector2 scale = region.Size / region.FrameSize;
                Vector2 centre = new(
                    (region.Offset.X + region.Size.X * 0.5f) / region.FrameSize.X - 0.5f,
                    0.5f - (region.Offset.Y + region.Size.Y * 0.5f) / region.FrameSize.Y);

                // Scale, then move, then the model, which is two whole matrix multiplications to change six
                // numbers. Written out it is this, and every fighter on screen comes through here every tick
                float ax = model.M11, ay = model.M12, bx = model.M21, by = model.M22;
                model.M41 += centre.X * ax + centre.Y * bx;
                model.M42 += centre.X * ay + centre.Y * by;
                model.M11 = ax * scale.X;
                model.M12 = ay * scale.X;
                model.M21 = bx * scale.Y;
                model.M22 = by * scale.Y;
            }
        }
        else
        {
            // One look at the animation, it used to be three, each of them hashing the name all over again.
            // A frame can span several cells of the sheet, the frames of an animation follow each other to the right
            var (definition, index) = AnimationManager[FrameName];
            Vector2 size = Spritesheet.SpriteSize * new Vector2(1 + definition.Span, 1);
            texMin = definition.Position * Spritesheet.SingleSpriteSize * new Vector2(Spritesheet.Width, Spritesheet.Height) + new Vector2(size.X * index, 0);
            texMax = texMin + size;
        }

        bool flashed = FlashAmount > 0.0f;

        item = SpriteItem.FromModel(
            model,
            texMin,
            texMax,
            SpriteItem.PackColor(flashed ? FlashColor with { W = Tint.W } : Tint),
            (Smooth ? SpriteItem.SmoothFlag : 0) | (flashed ? SpriteItem.FlashFlag : 0) | (flashed && FlashLights ? SpriteItem.LampFlag : 0) | (CastsShadows && !mask ? SpriteItem.ShadowFlag : 0));
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
    /// How many more cells of the sheet to the right a frame of the animation takes up, 0 for a frame of one cell.
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