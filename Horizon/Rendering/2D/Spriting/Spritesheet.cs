using Horizon.Logging;
using System.Numerics;

using Horizon.Engine;
using Horizon.Graphics;
using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Horizon.Rendering.Spriting;

/// <summary>
/// A texture cut into sprites of one size, with the animations that run across them. The texture is the
/// <see cref="Texture"/>, this is what the sprites know about it.
/// </summary>
public class SpriteSheet
{
    public int ID { get; private set; }

    /// <summary>The picture the sprites are cut out of.</summary>
    public Texture Texture { get; init; } = Texture.Invalid;

    public Dictionary<string, SpriteDefinition> Sprites { get; init; }
    public Vector2 SpriteSize { get; init; }
    public Vector2 SingleSpriteSize { get; init; }

    /// <summary>The texture's number, which is what tells one sheet from another.</summary>
    public uint Handle => Texture.Handle;

    public uint Width => Texture.Width;
    public uint Height => Texture.Height;

    /// <summary>Whether there is a picture behind the sheet at all.</summary>
    public bool IsValid => Texture.IsValid;

    public SpriteSheet()
    {
        this.Sprites = new();
    }

    public static (bool success, SpriteSheet result, SpriteSheetAnimationManager manager) LoadSpriteSheetFromDirectory(in string dir, in string defFileName = "definition.hor")
    {
        if (!Directory.Exists(dir))
        {
            Log.Error($"Failed to find directory '{dir}' to load sprite!");
            return (false, null!, null!);
        }

        HIDLRuntime runtime = new();
        var (success, msg) = runtime.Evaluate(File.ReadAllText(dir + "/" + defFileName));
        if (!success)
        {
            Log.Error($"Malformed sprite definition!\n{msg}");
            return (false, null!, null!);
        }

        string spriteFilePath = "spritesheet.png";
        float spriteSizeX = 0, spriteSizeY = 0, gridSizeX = 0, gridSizeY = 0;

        if (runtime.UserScope.Lookup("sprite") is ObjectValue def)
        {
            if (def.Properties.ContainsKey("sprite_file") && def.Properties["sprite_file"] is StringValue sprite_file)
            {
                if (!File.Exists(dir + "/" + sprite_file.Value))
                {
                    Log.Error("Failed to load spritesheet or definition!");
                    return (false, null!, null!);
                }

                spriteFilePath = sprite_file.Value;
            }

            if (def.Properties["sprite_size"] is ObjectValue sprite_size)
            {
                if (sprite_size.Properties["w"] is NumberValue sprite_width) spriteSizeX = sprite_width.Value;
                else { Log.Error("Invalid sprite width!"); return (false, null!, null!); }

                if (sprite_size.Properties["h"] is NumberValue sprite_height) spriteSizeY = sprite_height.Value;
                else { Log.Error("Invalid sprite height!"); return (false, null!, null!); }
            }
            else
            {
                Log.Error("Malformed sprite size def!");
                return (false, null!, null!);
            }

            if (def.Properties["grid_size"] is ObjectValue grid_size)
            {
                if (grid_size.Properties["w"] is NumberValue grid_width) gridSizeX = grid_width.Value;
                else { Log.Error("Invalid sprite grid width!"); return (false, null!, null!); }

                if (grid_size.Properties["h"] is NumberValue grid_height) gridSizeY = grid_height.Value;
                else { Log.Error("Invalid sprite grid height!"); return (false, null!, null!); }
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
                return (false, null!, null!);
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
                        else { Log.Error("Invalid sprite anim offset!"); return (false, null!, null!); }

                        if (anim.Properties["y"] is NumberValue anim_y) posY = anim_y.Value;
                        else { Log.Error("Invalid sprite anim offset"); return (false, null!, null!); }

                        if (anim.Properties.TryGetValue("length", out var animProp))
                        {
                            if (animProp is NumberValue anim_l) length = (uint)anim_l.Value;
                            else { Log.Error("Invalid sprite anim length!"); return (false, null!, null!); }
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
        return (false, null!, null!);
    }

    /// <summary>A sheet over a texture that is already there, cut into sprites of a size.</summary>
    public static SpriteSheet FromTexture(in Texture texture, in Vector2 spriteSize)
    {
        return new SpriteSheet()
        {
            Texture = texture,
            SpriteSize = spriteSize,
            SingleSpriteSize = spriteSize / new Vector2(Math.Max(1, texture.Width), Math.Max(1, texture.Height))
        };
    }
}
