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
    private static int _idCounter = 0;
    private bool _hasBeenSetup = false;

    public SpriteSheet Spritesheet { get; private set; }
    public SpriteSheetAnimationManager AnimationManager { get; private set; }
    public SpriteBatch Batch { get; internal set; }

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

    internal bool ShouldUpdateVbo { get; private set; }

    public bool IsAnimated { get; set; }
    public string FrameName { get; private set; }

    public virtual TransformComponent2D Transform { get; init; }


    public Sprite(in Vector2 size)
    {
        this.Transform = AddComponent<TransformComponent2D>();
        this.Transform.Size = size;
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

    public bool LoadSpriteSheetFromDirectory(in string dir, in string defFileName= "definition.hor")
    {
        if (!Directory.Exists(dir))
        {
            ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, $"Failed to find directory '{dir}' to load sprite!");
            return false;
        }


        HIDLRuntime runtime = new();
        var (success, msg) = runtime.Evaluate(File.ReadAllText(dir + "/" + defFileName));
        if (!success) { ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, $"Malformed sprite definition!\r\b{msg}");  return false; }

        string spriteFilePath = "spritesheet.png";
        float spriteSizeX = 0, spriteSizeY = 0, gridSizeX = 0, gridSizeY = 0;

        if (runtime.UserScope.Lookup("sprite") is ObjectValue def)
        {
            if (def.Properties.ContainsKey("sprite_file") && def.Properties["sprite_file"] is StringValue sprite_file)
            {
                if (!File.Exists(dir + "/" + sprite_file.Value))
                {
                    ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Failed to load spritesheet or definition!");
                    return false;
                }

                spriteFilePath = sprite_file.Value;
            }

            if (def.Properties["sprite_size"] is ObjectValue sprite_size)
            {
                if (sprite_size.Properties["w"] is NumberValue sprite_width) spriteSizeX = sprite_width.Value;
                else { ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Invalid sprite width!"); return false; }

                if (sprite_size.Properties["h"] is NumberValue sprite_height) spriteSizeY = sprite_height.Value;
                else { ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Invalid sprite height!"); return false; }
            }
            else
            {
                ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Malformed sprite size def!");
                return false;
            }

            if (def.Properties["grid_size"] is ObjectValue grid_size)
            {
                if (grid_size.Properties["w"] is NumberValue grid_width) gridSizeX = grid_width.Value;
                else { ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Invalid sprite grid width!"); return false; }

                if (grid_size.Properties["h"] is NumberValue grid_height) gridSizeY = grid_height.Value;
                else { ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Invalid sprite grid width!"); return false; }
            }

            if (def.Properties["animations"] is ObjectValue animations)
            {
                float posX = 0, posY = 0, time = 0.1f;
                uint length = 0;

                foreach (var (name, anim_raw) in animations.Properties)
                {
                    if (anim_raw is ObjectValue anim)
                    {
                        if (anim.Properties["x"] is NumberValue anim_x) posX = anim_x.Value;
                        else { ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Invalid sprite anim offset!"); return false; }

                        if (anim.Properties["y"] is NumberValue anim_y) posY = anim_y.Value;
                        else { ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Invalid sprite anim offset"); return false; }

                        if (anim.Properties.TryGetValue("l", out var animProp))
                        {
                            if (animProp is NumberValue anim_l) length = (uint)anim_l.Value;
                            else { ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Invalid sprite anim length!"); return false; }
                        }
                        else
                        {
                            length = 0;
                        }

                        if (anim.Properties.ContainsKey("t") && anim.Properties["t"] is NumberValue anim_t) 
                            time = anim_t.Value;

                        AddAnimation(name, new Vector2(posX, posY), length, time, new Vector2(spriteSizeX, spriteSizeY));
                    }
                }
            }
        }
        if (Engine.ObjectManager.Textures.TryCreate(new TextureDescription
        {
            Paths = [dir + "/" + spriteFilePath],
            Definition = TextureDefinition.RgbaUnsignedByteNearest
        }, out var result))
        {
            ConfigureSpriteSheet(SpriteSheet.FromTexture(result.Asset, new Vector2(spriteSizeX, spriteSizeY)), "player");
        }
        else
        {
            throw new Exception(result.Message);
        }


        return true;
    }
    public void ConfigureSpriteSheet(SpriteSheet spriteSheet, string name)
    {
        this.Spritesheet = (spriteSheet);
        this.AnimationManager ??= AddComponent(new SpriteSheetAnimationManager(spriteSheet));

        this.FrameName = name;

        this.IsAnimated = AnimationManager.Animations.Any();

        _hasBeenSetup = true;
    }

    public void SetAnimation(string name)
    {
        this.FrameName = name;
    }

    public Vector2 GetFrameOffset()
    {
        if (IsAnimated)
        {
            var (definition, _) = AnimationManager[FrameName];

            return (definition.Position * Spritesheet.SingleSpriteSize);
        }
        return Vector2.Zero;
    }

    public uint GetFrameIndex()
    {
        if (IsAnimated)
        {
            var (_, index) = AnimationManager[FrameName];
            return index;
        }
        return 0;
    }
}