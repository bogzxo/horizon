using System.Numerics;

using Bogz.Logging;
using Bogz.Logging.Loggers;

using Horizon.Engine;
using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;
using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Scripting;

namespace Horizon.Rendering.UIX.Skinning;

/// <summary>
/// The look of a UI: one texture holding the art, the named regions of it the components draw with,
/// a font, and the colours and sizes components use unless told otherwise.
/// Components ask for regions by name and fall back to flat colours when a skin doesn't have one, so
/// a skin only needs the art it actually has.
/// </summary>
public sealed class UISkin
{
    private const string DEFAULT_FONT_DIRECTORY = "fonts/vcr_mono/";
    private const string DEFAULT_FONT_FILE = "vcr_mono.fnt";

    private readonly Dictionary<string, UIRegion> regions = [];

    public Texture Texture { get; }
    public UIFont Font { get; }

    /// <summary>The scale text is drawn at unless a component asks for its own.</summary>
    public float TextScale { get; set; } = 0.4f;

    /// <summary>The colour of text that stands on its own: labels and captions.</summary>
    public Vector4 TextColor { get; set; } = new(0.08f, 0.09f, 0.13f, 1.0f);

    /// <summary>The colour of text drawn on top of a control's art, like the label of a button.</summary>
    public Vector4 ControlTextColor { get; set; } = new(0.08f, 0.09f, 0.13f, 1.0f);

    /// <summary>What a control is filled with when the skin has no art for it.</summary>
    public Vector4 ControlColor { get; set; } = new(0.78f, 0.81f, 0.87f, 1.0f);

    /// <summary>The colour of the filled part of bars and sliders, and of toggles that are on, when the skin has no art for them.</summary>
    public Vector4 AccentColor { get; set; } = new(0.85f, 0.42f, 0.3f, 1.0f);

    /// <summary>What a panel that asks for a background is filled with when the skin has no art for it.</summary>
    public Vector4 PanelColor { get; set; } = new(0.1f, 0.12f, 0.17f, 0.92f);

    /// <summary>The colour of the text written on bars. It gets a shadow, as it has to read on both the filled and the empty part.</summary>
    public Vector4 BarTextColor { get; set; } = new(1.0f, 1.0f, 1.0f, 1.0f);

    /// <summary>The shadow under <see cref="BarTextColor"/>.</summary>
    public Vector4 TextShadowColor { get; set; } = new(0.0f, 0.0f, 0.0f, 0.7f);

    /// <summary>What the empty part of a bar or slider is filled with.</summary>
    public Vector4 TrackColor { get; set; } = new(0.0f, 0.0f, 0.0f, 0.35f);

    /// <summary>Laid over a control while the pointer is on it.</summary>
    public Vector4 HoverColor { get; set; } = new(1.0f, 1.0f, 1.0f, 0.22f);

    /// <summary>The outline drawn around a control while the pointer is on it.</summary>
    public Vector4 HighlightColor { get; set; } = new(1.0f, 1.0f, 1.0f, 0.9f);

    /// <summary>What a disabled control is tinted with.</summary>
    public Vector4 DisabledTint { get; set; } = new(1.0f, 1.0f, 1.0f, 0.4f);

    /// <summary>The space a button keeps between its label and its edges.</summary>
    public UIEdges ButtonPadding { get; set; } = new(16.0f, 10.0f);

    /// <summary>How far inside the frame of a progress bar or slider its fill sits.</summary>
    public UIEdges ProgressInset { get; set; } = new(7.0f, 6.0f);

    /// <summary>The side length of the on/off indicator of a toggle.</summary>
    public float ToggleSize { get; set; } = 30.0f;

    /// <summary>The gap between related things: a toggle and its caption, the children of a stack.</summary>
    public float Spacing { get; set; } = 10.0f;

    public UISkin(Texture texture, UIFont font)
    {
        Texture = texture;
        Font = font;
    }

    public void SetRegion(string name, in UIRegion region) => regions[name] = region;

    public bool TryGetRegion(string name, out UIRegion region) => regions.TryGetValue(name, out region);

    /// <summary>
    /// Loads a skin from a HIDL definition (see Assets/uix/skin.hor) that sits next to its texture.
    /// Has to run on the GL thread. Logs what went wrong and returns null if the skin can't be loaded.
    /// </summary>
    public static UISkin? Load(string directory, string file)
    {
        try
        {
            return Read(directory, file);
        }
        catch (Exception e)
        {
            ConcurrentLogger.Instance.Log(
                LogLevel.Error,
                $"[UISkin] Failed to load '{Path.Combine(directory, file)}': {e.Message}");
            return null;
        }
    }

    private static UISkin Read(string directory, string file)
    {
        string path = Path.Combine(directory, file);
        if (!File.Exists(path))
            throw new FileNotFoundException("The file doesn't exist.");

        HIDLRuntime runtime = new();
        var (success, message) = runtime.Evaluate(File.ReadAllText(path));
        if (!success)
            throw new Exception(message);

        if (runtime.UserScope.Lookup("skin") is not ObjectValue definition)
            throw new Exception("It has to declare an object called 'skin'.");

        var properties = definition.Properties;

        if (!properties.TryGetValue("texture", out var textureFile))
            throw new Exception("The skin has to name its texture.");

        if (!GameEngine
                .Instance
                .ObjectManager
                .Textures
                .TryCreate(
                    new TextureDescription
                    {
                        Paths = [Path.Combine(directory, UIScript.ToText(textureFile, "texture"))],
                        Definition = TextureDefinition.RgbaUnsignedByteNearest
                    },
                    out var texture))
        {
            throw new Exception(texture.Message);
        }

        string fontDirectory = DEFAULT_FONT_DIRECTORY;
        string fontFile = DEFAULT_FONT_FILE;
        if (properties.TryGetValue("font", out var fontValue))
        {
            if (fontValue is not ObjectValue font
                || !font.Properties.TryGetValue("dir", out var dir)
                || !font.Properties.TryGetValue("file", out var name))
            {
                throw new Exception("font has to be an object with a dir and a file.");
            }

            fontDirectory = UIScript.ToText(dir, "font.dir");
            fontFile = UIScript.ToText(name, "font.file");
        }

        var skin = new UISkin(texture.Asset, new UIFont(fontDirectory, fontFile));

        foreach (var (key, value) in properties)
        {
            switch (key)
            {
                case "texture" or "font":
                    break;

                case "text_scale":
                    skin.TextScale = UIScript.ToNumber(value, key);
                    break;
                case "text_color":
                    skin.TextColor = UIScript.ToColor(value, key);
                    break;
                case "control_text_color":
                    skin.ControlTextColor = UIScript.ToColor(value, key);
                    break;
                case "control_color":
                    skin.ControlColor = UIScript.ToColor(value, key);
                    break;
                case "accent_color":
                    skin.AccentColor = UIScript.ToColor(value, key);
                    break;
                case "panel_color":
                    skin.PanelColor = UIScript.ToColor(value, key);
                    break;
                case "bar_text_color":
                    skin.BarTextColor = UIScript.ToColor(value, key);
                    break;
                case "text_shadow_color":
                    skin.TextShadowColor = UIScript.ToColor(value, key);
                    break;
                case "track_color":
                    skin.TrackColor = UIScript.ToColor(value, key);
                    break;
                case "hover_color":
                    skin.HoverColor = UIScript.ToColor(value, key);
                    break;
                case "highlight_color":
                    skin.HighlightColor = UIScript.ToColor(value, key);
                    break;
                case "disabled_tint":
                    skin.DisabledTint = UIScript.ToColor(value, key);
                    break;
                case "button_padding":
                    skin.ButtonPadding = UIScript.ToEdges(value, key);
                    break;
                case "progress_inset":
                    skin.ProgressInset = UIScript.ToEdges(value, key);
                    break;
                case "toggle_size":
                    skin.ToggleSize = UIScript.ToNumber(value, key);
                    break;
                case "spacing":
                    skin.Spacing = UIScript.ToNumber(value, key);
                    break;

                case "regions":
                    if (value is not ObjectValue regions)
                        throw new Exception("regions has to be an object.");

                    foreach (var (name, region) in regions.Properties)
                        skin.SetRegion(name, ReadRegion(name, region));
                    break;

                default:
                    throw new Exception($"'{key}' isn't something a skin has.");
            }
        }

        return skin;
    }

    private static UIRegion ReadRegion(string name, IRuntimeValue value)
    {
        if (value is not ObjectValue region)
            throw new Exception($"Region '{name}' has to be an object.");

        float Number(string key) =>
            region.Properties.TryGetValue(key, out var number)
                ? UIScript.ToNumber(number, $"{name}.{key}")
                : throw new Exception($"Region '{name}' is missing its {key}.");

        return new UIRegion(
            new Vector2(Number("x"), Number("y")),
            new Vector2(Number("w"), Number("h")),
            region.Properties.TryGetValue("border", out var border)
                ? UIScript.ToEdges(border, $"{name}.border")
                : default);
    }
}
