using System.Numerics;
using System.Runtime.CompilerServices;

using Bogz.Logging.Loggers;
using Horizon.Engine;
using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;

namespace Horizon.Rendering.Spriting;

/// <summary>
/// A specialized <see cref="Texture"/> with specific additions such as sprite definitions, animation management and soon to be refactored backend rendering code.
/// </summary>
/// <seealso cref="Horizon.OpenGL.Texture" />
public class SpriteSheet : Texture
{
    public int ID { get; private set; }

    public Dictionary<string, SpriteDefinition> Sprites { get; init; }
    public Vector2 SpriteSize { get; init; }
    public Vector2 SingleSpriteSize { get; init; }

    public SpriteSheet()
    {
        this.Sprites = new();
    }

    public static (bool success, SpriteSheet result, SpriteSheetAnimationManager manager) LoadSpriteSheetFromDirectory(in string dir, in string defFileName = "definition.hor")
    {

        if (!Directory.Exists(dir))
        {
            ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, $"Failed to find directory '{dir}' to load sprite!");
            return (false, null, null);
        }


        HIDLRuntime runtime = new();
        var (success, msg) = runtime.Evaluate(File.ReadAllText(dir + "/" + defFileName));
        if (!success) { ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, $"Malformed sprite definition!\r\b{msg}"); 
            return (false, null, null); }

        string spriteFilePath = "spritesheet.png";
        float spriteSizeX = 0, spriteSizeY = 0, gridSizeX = 0, gridSizeY = 0;

        if (runtime.UserScope.Lookup("sprite") is ObjectValue def)
        {
            if (def.Properties.ContainsKey("sprite_file") && def.Properties["sprite_file"] is StringValue sprite_file)
            {
                if (!File.Exists(dir + "/" + sprite_file.Value))
                {
                    ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Failed to load spritesheet or definition!");
                    return (false, null, null);
                }

                spriteFilePath = sprite_file.Value;
            }

            if (def.Properties["sprite_size"] is ObjectValue sprite_size)
            {
                if (sprite_size.Properties["w"] is NumberValue sprite_width) spriteSizeX = sprite_width.Value;
                else { ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Invalid sprite width!"); return (false, null, null); }

                if (sprite_size.Properties["h"] is NumberValue sprite_height) spriteSizeY = sprite_height.Value;
                else { ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Invalid sprite height!"); return (false, null, null); }
            }
            else
            {
                ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Malformed sprite size def!");
                return (false, null, null);
            }

            if (def.Properties["grid_size"] is ObjectValue grid_size)
            {
                if (grid_size.Properties["w"] is NumberValue grid_width) gridSizeX = grid_width.Value;
                else { ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Invalid sprite grid width!"); return (false, null, null); }

                if (grid_size.Properties["h"] is NumberValue grid_height) gridSizeY = grid_height.Value;
                else { ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Invalid sprite grid width!"); return (false, null, null); }
            }

            SpriteSheet sheet;
            SpriteSheetAnimationManager animationManager;
            if (GameEngine.Instance.ObjectManager.Textures.TryCreate(new TextureDescription
                {
                    Paths = [dir + "/" + spriteFilePath],
                    Definition = TextureDefinition.RgbaUnsignedByteNearest
                }, out var result))
            {
                sheet = FromTexture(result.Asset, new Vector2(spriteSizeX, spriteSizeY));
                animationManager = new(sheet);
            }
            else
            {
                return (false, null, null);
            }

            if (def.Properties["animations"] is ObjectValue animations)
            {
                foreach (var (name, anim_raw) in animations.Properties)
                {
                    if (anim_raw is ObjectValue anim)
                    {
                        float posX = 0, posY = 0, time = 0.1f;
                        uint length = 1, span = 0;

                        if (anim.Properties["x"] is NumberValue anim_x) posX = anim_x.Value;
                        else { ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Invalid sprite anim offset!"); return (false, null, null); }

                        if (anim.Properties["y"] is NumberValue anim_y) posY = anim_y.Value;
                        else { ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Invalid sprite anim offset"); return (false, null, null); }

                        if (anim.Properties.TryGetValue("length", out var animProp))
                        {
                            if (animProp is NumberValue anim_l) length = (uint)anim_l.Value;
                            else { ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Invalid sprite anim length!"); return (false, null, null); }
                        }

                        if (anim.Properties.ContainsKey("time") && anim.Properties["time"] is NumberValue anim_t)
                            time = anim_t.Value;

                        if (anim.Properties.ContainsKey("span") && anim.Properties["span"] is NumberValue anim_s)
                            span = (uint)anim_s.Value;


                        animationManager.AddAnimation(name, new Vector2(posX, posY), length, time, new Vector2(spriteSizeX, spriteSizeY), span);
                    }
                }
            }

            return (true, sheet, animationManager);
        }
        return (false, null, null);
    }

    public static SpriteSheet FromTexture(in Texture texture, in Vector2 spriteSize)
    {
        return new SpriteSheet()
        {
            Handle = texture.Handle,
            Width = texture.Width,
            Height = texture.Height,
            SpriteSize = spriteSize,
            SingleSpriteSize = spriteSize / new Vector2(texture.Width, texture.Height)
        };
    }

    /// <summary>
    /// Defines a sprite by name.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="pos">The position.</param>
    /// <param name="size">The size.</param>
    //public void AddSprite(string name, Vector2 pos, Vector2? size = null)
    //{
    //    if (Sprites.ContainsKey(name))
    //    {
    //        //Entity.ConcurrentLogger.Instance.Log(
    //        //    Logging.LogLevel.Error,
    //        //    $"Attempt to add sprite '{name}' which already exists!"
    //        //); TODO: FIX
    //        return;
    //    }

    //    this.Sprites.Add(name, new SpriteDefinition { Position = pos, Size = size ?? SpriteSize });
    //}

    /// <summary>
    /// Gets the static texture coordinates.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Vector2[] GetTextureCoordinates(string name)
    {
        if (!Sprites.TryGetValue(name, out var sprite))
        {
            //Entity.ConcurrentLogger.Instance.Log(
            //    Logging.LogLevel.Error,
            //    $"Attempt to get sprite '{name}' which doesn't exist!"
            //); TODO: FIX
            return Array.Empty<Vector2>();
        }

        // Calculate texture coordinates for the sprite
        Vector2 topLeftTexCoord = sprite.Position / new Vector2(Width, Height); // todo
        Vector2 bottomRightTexCoord = (sprite.Position + sprite.Size) / new Vector2(Width, Height);

        return new Vector2[]
        {
            topLeftTexCoord,
            new Vector2(bottomRightTexCoord.X, topLeftTexCoord.Y),
            bottomRightTexCoord,
            new Vector2(topLeftTexCoord.X, bottomRightTexCoord.Y)
        };
    }
}