using System.Numerics;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Rendering.Spriting;
using Horizon.UI.Drawing;
using Horizon.UI.Scripting;

using Horizon.Rendering.Text;

namespace Horizon.UI.Skinning;

// The half of the skin that reads skin files. What a file can say is in Read, one case for every word of it.
public sealed partial class UISkin
{
    /// <summary>
    /// Reads what a skin file sets, on top of what the skin it inherits from sets.
    /// </summary>
    private static Dictionary<string, IRuntimeValue> ReadProperties(string directory, string file, int depth)
    {
        string path = Path.Combine(directory, file);
        if (!File.Exists(path))
            throw new FileNotFoundException($"'{path}' doesn't exist.");
        if (depth > MAX_INHERITANCE)
            throw new Exception($"'{path}' inherits from itself.");

        HIDLRuntime runtime = new();
        var (success, message) = runtime.Evaluate(File.ReadAllText(path));
        if (!success)
            throw new Exception($"'{path}': {message}");

        if (runtime.UserScope.Lookup("skin") is not ObjectValue definition)
            throw new Exception($"'{path}' has to declare an object called 'skin'.");

        if (!definition.Properties.TryGetValue("inherit", out var parent))
            return new Dictionary<string, IRuntimeValue>(definition.Properties);

        var properties = ReadProperties(directory, UIScript.ToText(parent, "inherit"), depth + 1);
        Merge(properties, definition.Properties);
        return properties;
    }

    /// <summary>
    /// Puts what one of the skin's themes does differently on top of what the skin sets, and takes the list of
    /// themes out of the properties.
    /// </summary>
    /// <returns>The names of the themes the skin has.</returns>
    private static string[] ApplyTheme(Dictionary<string, IRuntimeValue> properties, string? theme)
    {
        string? wanted = theme ?? (properties.TryGetValue("theme", out var usual) ? UIScript.ToText(usual, "theme") : null);
        string[] names = [];

        if (properties.Remove("themes", out var themesValue))
        {
            if (themesValue is not ObjectValue themes)
                throw new Exception("themes has to be an object.");

            names = [.. themes.Properties.Keys];

            if (wanted is not null && themes.Properties.TryGetValue(wanted, out var own))
            {
                if (own is not ObjectValue different)
                    throw new Exception($"themes.{wanted} has to be an object.");

                Merge(properties, different.Properties);
            }
            else if (theme is not null)
            {
                throw new Exception($"The skin has no theme called '{theme}'. It has: {string.Join(", ", names)}.");
            }
        }

        if (wanted is not null)
            properties["theme"] = new StringValue(wanted);

        return names;
    }

    /// <summary>
    /// Puts properties on top of others. Regions and icons are added to the ones that are there, everything
    /// else replaces what was there.
    /// </summary>
    private static void Merge(Dictionary<string, IRuntimeValue> properties, Dictionary<string, IRuntimeValue> over)
    {
        foreach (var (key, value) in over)
        {
            if (key == "inherit")
                continue;

            if (key is "regions" or "icons"
                && value is ObjectValue added
                && properties.TryGetValue(key, out var below)
                && below is ObjectValue existing)
            {
                var merged = new Dictionary<string, IRuntimeValue>(existing.Properties);
                foreach (var (name, entry) in added.Properties)
                    merged[name] = entry;

                properties[key] = new ObjectValue(merged);
            }
            else
            {
                properties[key] = value;
            }
        }
    }

    private static UISkin Read(string directory, Dictionary<string, IRuntimeValue> properties)
    {
        string fontDirectory = DEFAULT_FONT_DIRECTORY;
        string fontFile = DEFAULT_FONT_FILE;
        int fontSize = DistanceFieldFont.DEFAULT_EM;
        if (properties.TryGetValue("font", out var fontValue))
        {
            if (fontValue is not ObjectValue font
                || !font.Properties.TryGetValue("dir", out var dir)
                || !font.Properties.TryGetValue("file", out var name))
            {
                throw new Exception("font has to be an object with a dir and a file (and a size, pixels to the em, for a TrueType one).");
            }

            fontDirectory = UIScript.ToText(dir, "font.dir");
            fontFile = UIScript.ToText(name, "font.file");
            if (font.Properties.TryGetValue("size", out var size))
                fontSize = (int)UIScript.ToNumber(size, "font.size");
        }

        SpriteSheetDefinition? sheet = properties.TryGetValue("sprites", out var sprites)
            ? SpriteSheetDefinition.Load(directory, UIScript.ToText(sprites, "sprites"))
            : null;

        string? theme = properties.TryGetValue("theme", out var themeValue) ? UIScript.ToText(themeValue, "theme") : null;
        if (theme is not null && sheet is not null && !sheet.Themes.Contains(theme))
            throw new Exception($"The sprite sheet has no theme called '{theme}'. It has: {string.Join(", ", sheet.Themes)}.");

        // The image regions are cut out of when they give a rectangle rather than the name of a sprite.
        string? texture = properties.TryGetValue("texture", out var textureFile)
            ? Path.Combine(directory, UIScript.ToText(textureFile, "texture"))
            : null;

        var skin = new UISkin(new UIFont(fontDirectory, fontFile, fontSize), sheet, theme);

        // Before anything else. It is what every region is scaled by unless it says otherwise.
        if (properties.TryGetValue("art_scale", out var artScale))
            skin.ArtScale = UIScript.ToNumber(artScale, "art_scale");

        foreach (var (key, value) in properties)
        {
            switch (key)
            {
                case "texture" or "font" or "sprites" or "theme" or "art_scale":
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
                case "field_color":
                    skin.FieldColor = UIScript.ToColor(value, key);
                    break;
                case "border_color":
                    skin.BorderColor = UIScript.ToColor(value, key);
                    break;
                case "corner_radius":
                    skin.CornerRadius = MathF.Max(0.0f, UIScript.ToNumber(value, key));
                    break;
                case "icon_blink":
                    skin.IconBlink = MathF.Max(0.0f, UIScript.ToNumber(value, key));
                    break;
                case "syntax":
                    ReadSyntax(skin.Syntax, value);
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
                case "icon_label_color":
                    skin.IconLabelColor = UIScript.ToColor(value, key);
                    break;
                case "icon_scale":
                    skin.IconScale = UIScript.ToNumber(value, key);
                    break;
                case "progress_fill_over_frame":
                    skin.ProgressFillOverFrame = UIScript.ToBoolean(value, key);
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
                        skin.sources[name] = ReadRegion(name, region, texture);
                    break;

                case "icons":
                    if (value is not ObjectValue icons)
                        throw new Exception("icons has to be an object.");

                    foreach (var (name, icon) in icons.Properties)
                        skin.ReadIcon(name, icon, texture);
                    break;

                case "icon_sets":
                    if (value is not ObjectValue sets)
                        throw new Exception("icon_sets has to be an object.");

                    foreach (var (set, names) in sets.Properties)
                    {
                        if (names is not ObjectValue renamed)
                            throw new Exception($"icon_sets.{set} has to be an object.");

                        var other = skin.iconSets[set] = [];
                        foreach (var (name, icon) in renamed.Properties)
                            other[name] = UIScript.ToText(icon, $"icon_sets.{set}.{name}");
                    }
                    break;

                default:
                    throw new Exception($"'{key}' isn't something a skin has.");
            }
        }

        // A skin that only says what its accent is has things highlighted in it.
        if (properties.ContainsKey("accent_color") && !properties.ContainsKey("highlight_color"))
            skin.HighlightColor = skin.AccentColor with { W = 0.9f };

        UIRenderer.PrepareFont(skin.Font);

        // The art the skin declares is what its components are about to ask for, so it is stitched in
        // right away rather than a frame late.
        foreach (string name in skin.sources.Keys)
            skin.TryGetRegion(name, out _);

        // Its icons too, with the symbols some of them carry. They are what a text asks for the moment somebody
        // picks up a gamepad, which is no time for a screen to be waiting for its art
        foreach (IconSource icon in skin.icons.Values)
        {
            skin.TryGetRegion(icon.Region, out _);
            if (icon.Symbol is not null)
                skin.TryGetRegion(icon.Symbol, out _);
        }

        skin.Update();

        return skin;
    }

    /// <summary>
    /// The colours code is shown in, by what they colour. A skin only lists the ones it wants its own way.
    /// </summary>
    private static void ReadSyntax(UISyntaxColors colors, IRuntimeValue value)
    {
        if (value is not ObjectValue syntax)
            throw new Exception("syntax has to be an object.");

        foreach (var (name, color) in syntax.Properties)
        {
            Vector4 read = UIScript.ToColor(color, $"syntax.{name}");

            switch (name)
            {
                case "name": colors.Name = read; break;
                case "keyword": colors.Keyword = read; break;
                case "property": colors.Property = read; break;
                case "call": colors.Call = read; break;
                case "text": colors.Text = read; break;
                case "number": colors.Number = read; break;
                case "comment": colors.Comment = read; break;
                case "punctuation": colors.Punctuation = read; break;
                case "line_number": colors.LineNumber = read; break;
                default: throw new Exception($"'{name}' isn't something code is coloured by.");
            }
        }
    }

    /// <summary>
    /// A region is the name of a sprite, or an object: { sprite: "name" } to change something about a
    /// sprite (its border, content, scale or tint), { x, y, w, h } to cut a rectangle out of the skin's texture.
    /// </summary>
    private static RegionSource ReadRegion(string name, IRuntimeValue value, string? texture)
    {
        if (value is StringValue sprite)
            return new RegionSource(sprite.Value, null, 0, 0, 0, 0, null, null, null, Vector4.One);

        if (value is not ObjectValue region)
            throw new Exception($"Region '{name}' has to be the name of a sprite or an object.");

        var properties = region.Properties;

        UIEdges? border = properties.TryGetValue("border", out var borderValue)
            ? UIScript.ToEdges(borderValue, $"{name}.border")
            : null;
        UIEdges? content = properties.TryGetValue("content", out var contentValue)
            ? UIScript.ToEdges(contentValue, $"{name}.content")
            : null;
        float? scale = properties.TryGetValue("scale", out var scaleValue)
            ? UIScript.ToNumber(scaleValue, $"{name}.scale")
            : null;
        Vector4 tint = properties.TryGetValue("tint", out var tintValue)
            ? UIScript.ToColor(tintValue, $"{name}.tint")
            : Vector4.One;

        if (properties.TryGetValue("sprite", out var spriteName))
            return new RegionSource(UIScript.ToText(spriteName, $"{name}.sprite"), null, 0, 0, 0, 0, border, content, scale, tint);

        if (texture is null)
            throw new Exception($"Region '{name}' is a rectangle, but the skin names no texture to cut it out of.");

        int Number(string key) =>
            properties.TryGetValue(key, out var number)
                ? (int)UIScript.ToNumber(number, $"{name}.{key}")
                : throw new Exception($"Region '{name}' is missing its {key}.");

        // A rectangle with no border isn't stretched as a nine-slice, which is what null would leave to the sprite.
        return new RegionSource(
            null, texture, Number("x"), Number("y"), Number("w"), Number("h"), border ?? default(UIEdges), content ?? default(UIEdges), scale, tint);
    }

    /// <summary>
    /// An icon is the name of a region, or an object that is a region of its own (see <see cref="ReadRegion"/>)
    /// with an optional label and label_color on top, or a symbol (the name of a region) with a symbol_color
    /// and a symbol_size. An icon is drawn as tall as the line it is in whatever the size of its art, unless it gives
    /// a height. That is how many pixels of its art make a line, which keeps a set of icons at one scale (the four arrows).
    /// </summary>
    private void ReadIcon(string name, IRuntimeValue value, string? texture)
    {
        if (value is StringValue region)
        {
            icons[name] = new IconSource(region.Value, string.Empty, null);
            return;
        }

        if (value is not ObjectValue icon)
            throw new Exception($"Icon '{name}' has to be the name of a region or an object.");

        // The art of an icon is a region nobody else can ask for by name.
        string own = $"icon:{name}";
        var properties = new Dictionary<string, IRuntimeValue>(icon.Properties);

        string label = properties.Remove("label", out var labelValue) ? UIScript.ToText(labelValue, $"{name}.label") : string.Empty;
        Vector4? labelColor = properties.Remove("label_color", out var colorValue)
            ? UIScript.ToColor(colorValue, $"{name}.label_color")
            : null;

        string? symbol = properties.Remove("symbol", out var symbolValue) ? UIScript.ToText(symbolValue, $"{name}.symbol") : null;
        Vector4 symbolColor = properties.Remove("symbol_color", out var symbolColorValue)
            ? UIScript.ToColor(symbolColorValue, $"{name}.symbol_color")
            : Vector4.One;
        float symbolSize = properties.Remove("symbol_size", out var symbolSizeValue)
            ? UIScript.ToNumber(symbolSizeValue, $"{name}.symbol_size")
            : 0.5f;

        // How many pixels of the art are as tall as a line of text, for icons that belong together but aren't all the same height
        float height = properties.Remove("height", out var heightValue) ? UIScript.ToNumber(heightValue, $"{name}.height") : 0.0f;

        sources[own] = ReadRegion(name, new ObjectValue(properties), texture);
        icons[name] = new IconSource(own, label, labelColor, symbol, symbolColor, symbolSize, height);
    }
}
