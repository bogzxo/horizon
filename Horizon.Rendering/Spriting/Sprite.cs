using System.Numerics;
using System.Runtime.CompilerServices;

using Bogz.Logging.Loggers;

using Horizon.Core.Components;
using Horizon.Core.Components.Physics2D;
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

    public bool UseStencilBuffer { get; set; } = false;

    public bool ShouldDraw { get; set; } = true;

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

    public void SetAnimation(string name)
    {
        this.FrameName = name;
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