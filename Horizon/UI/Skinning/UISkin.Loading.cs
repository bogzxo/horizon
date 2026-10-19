using System.Numerics;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Rendering.Spriting;
using Horizon.UI.Drawing;
using Horizon.UI.Scripting;

using Horizon.Rendering.Text;

namespace Horizon.UI.Skinning;

// The half of the skin that reads skin files. What a file can say is in Read, one case for every word of it, and
// what a theme can say is UITheme. A theme is found (ReadThemes), laid over the skin's own colours (ApplyTheme,
// base first) and the palette fills in whatever neither wrote (UIPalette.Derive).
public sealed partial class UISkin
{
    // The keys of a skin file that hold an object of their own which a theme (or a skin that inherits) adds to
    // rather than replaces, a region, an icon or a colour of code at a time
    private static readonly string[] MergedObjects = ["regions", "icons", "syntax"];

    // The fonts the skins have made, by the skin file and the font, so the themes of a skin share the one distance
    // field rather than building it again each. A font belongs to one skin (it asks it about icons), so two skins
    // with the same TrueType still get two
    private static readonly Dictionary<(string Skin, string Directory, string File, int Size), UIFont> fonts = [];

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

        var definition = Evaluate(path, "skin");

        if (!definition.Properties.TryGetValue("inherit", out var parent))
            return new Dictionary<string, IRuntimeValue>(definition.Properties);

        var properties = ReadProperties(directory, UIScript.ToText(parent, "inherit"), depth + 1);
        Merge(properties, definition.Properties);
        return properties;
    }

    /// <summary>
    /// Helper method to run a .hor file and take the object it declares under a name.
    /// </summary>
    private static ObjectValue Evaluate(string path, string name)
    {
        HIDLRuntime runtime = new();
        var (success, message) = runtime.Evaluate(File.ReadAllText(path));
        if (!success)
            throw new Exception($"'{path}': {message}");

        if (runtime.UserScope.Lookup(name) is not ObjectValue declared)
            throw new Exception($"'{path}' has to declare an object called '{name}'.");

        return declared;
    }

    /// <summary>
    /// The themes a skin comes in, the ones its file lists under <c>themes</c> in the order they are written and
    /// then the ones in the <c>themes</c> folder next to the file by the order of their names, the usual one first
    /// whichever it is. Takes <c>themes</c> out of the properties. A skin that inherits gets the themes of the file
    /// that said them last and the folder next to its own file, a theme folder isn't inherited.
    /// </summary>
    private static List<UITheme> ReadThemes(string directory, Dictionary<string, IRuntimeValue> properties)
    {
        var themes = new List<UITheme>();

        if (properties.Remove("themes", out var listed))
        {
            if (listed is not ObjectValue inline)
                throw new Exception("themes has to be an object, a theme under every name.");

            foreach (var (name, value) in inline.Properties)
                themes.Add(UITheme.Read(name, value, $"themes.{name}"));
        }

        string folder = Path.Combine(directory, UITheme.FOLDER);
        if (Directory.Exists(folder))
        {
            foreach (string file in Directory.GetFiles(folder, "*.hor").Order(StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (themes.Exists(theme => theme.Name == name))
                    throw new Exception($"the theme '{name}' is both in the skin file and in {UITheme.FOLDER}/{name}.hor, it can only be one of them.");

                themes.Add(UITheme.Read(name, Evaluate(file, "theme"), $"{UITheme.FOLDER}/{name}.hor"));
            }
        }

        foreach (UITheme theme in themes)
        {
            if (theme.Base is { } baseName && !themes.Exists(other => other.Name == baseName))
                throw new Exception($"the theme '{theme.Name}' is based on '{baseName}', which the skin hasn't got.");
        }

        // The one the skin comes up in goes first, that is what steps through them from the start
        if (properties.TryGetValue("theme", out var usualValue))
        {
            string usual = UIScript.ToText(usualValue, "theme");
            int at = themes.FindIndex(theme => theme.Name == usual);
            if (at > 0)
            {
                UITheme first = themes[at];
                themes.RemoveAt(at);
                themes.Insert(0, first);
            }
        }

        return themes;
    }

    /// <summary>
    /// Puts what a theme does differently on top of what the skin sets, the theme it is based on first (and that
    /// one's base before it), and says which theme the skin is in.
    /// </summary>
    /// <param name="theme">The theme wanted, null for the one the skin says is its usual one.</param>
    /// <returns>The theme the skin is in, null for a skin without any.</returns>
    private static UITheme? ApplyTheme(Dictionary<string, IRuntimeValue> properties, List<UITheme> themes, string? theme)
    {
        string? wanted = theme ?? (properties.TryGetValue("theme", out var usual) ? UIScript.ToText(usual, "theme") : null);
        if (wanted is null)
            return null;

        UITheme? found = themes.Find(candidate => candidate.Name == wanted);
        if (found is null)
        {
            if (theme is not null || themes.Count > 0)
                throw new Exception($"The skin has no theme called '{wanted}'. It has: {string.Join(", ", themes.Select(candidate => candidate.Name))}.");

            // A skin with no themes of its own that names the layer of its art it draws with, that name is its one theme
            found = UITheme.Read(wanted, new ObjectValue([]), "theme");
        }

        // The chain from the theme back to the skin, laid on the other way round so the theme has the last word
        var chain = new List<UITheme>();
        for (UITheme? at = found; at is not null; at = at.Base is { } baseName ? themes.Find(candidate => candidate.Name == baseName) : null)
        {
            if (chain.Contains(at))
                throw new Exception($"the theme '{found.Name}' is based on itself, round through {string.Join(", ", chain.Select(link => link.Name))}.");
            chain.Add(at);
        }

        for (int i = chain.Count - 1; i >= 0; i--)
        {
            // A theme that says its surface means its panel and its controls to come out of it, whatever the skin
            // wrote for those, so those go unless the theme writes them too. The same for the other colours things
            // are worked out from, see UIPalette.DerivedFrom
            foreach (string key in chain[i].Changes.Keys)
            {
                foreach (string derived in UIPalette.DerivedFrom(key))
                {
                    if (!chain[i].Changes.ContainsKey(derived)) properties.Remove(derived);
                }
            }

            Merge(properties, chain[i].Changes);
        }

        properties["theme"] = new StringValue(found.Name);
        return found;
    }

    /// <summary>
    /// Puts properties on top of others. Regions, icons and the colours of code are added to the ones that are
    /// there, everything else replaces what was there.
    /// </summary>
    private static void Merge(Dictionary<string, IRuntimeValue> properties, IReadOnlyDictionary<string, IRuntimeValue> over)
    {
        foreach (var (key, value) in over)
        {
            if (key == "inherit")
                continue;

            if (Array.IndexOf(MergedObjects, key) >= 0
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

    /// <summary>
    /// What a skin file says about itself without loading any of it, what it wants to be called, the themes it
    /// comes in and which of them is its usual one. Throws, saying what is wrong, for a file that can't be read.
    /// </summary>
    public static (string? Title, string[] Themes, string? UsualTheme) Describe(string directory, string file)
    {
        var properties = ReadProperties(directory, file, 0);
        string? title = properties.TryGetValue("title", out var titled) ? UIScript.ToText(titled, "title") : null;
        List<UITheme> themes = ReadThemes(directory, properties);
        UITheme? usual = ApplyTheme(properties, themes, null);
        return (title, [.. themes.Select(theme => theme.Name)], usual?.Name);
    }

    /// <summary>
    /// The themes of a skin file with what each says about itself, its title for an options screen, the layer of
    /// the art it draws with and the theme it is based on, without loading any of it. In the order they are stepped
    /// through, the usual one first.
    /// </summary>
    public static IReadOnlyList<UIThemeInfo> ThemesOf(string directory, string file)
    {
        var properties = ReadProperties(directory, file, 0);
        return [.. ReadThemes(directory, properties).Select(theme => theme.Info)];
    }

    /// <summary>
    /// Helper method to make (or find) the font of a skin file, shared by its themes.
    /// </summary>
    private static UIFont FontOf(string skinPath, string directory, string file, int size)
    {
        var key = (skinPath, directory, file, size);
        lock (fonts)
        {
            if (!fonts.TryGetValue(key, out UIFont? font))
                fonts[key] = font = new UIFont(directory, file, size);
            return font;
        }
    }

    private static UISkin Read(string directory, string file, Dictionary<string, IRuntimeValue> properties, UITheme? worn)
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

        // The theme draws with a layer of the art, its own name unless it says, which the sheet has to have
        string? art = worn?.Art;
        if (art is not null && sheet is not null && !sheet.Themes.Contains(art))
            throw new Exception($"The theme '{worn!.Name}' draws with the art '{art}', which the sprite sheet hasn't got. It has: {string.Join(", ", sheet.Themes)}.");

        // The image regions are cut out of when they give a rectangle rather than the name of a sprite.
        string? texture = properties.TryGetValue("texture", out var textureFile)
            ? Path.Combine(directory, UIScript.ToText(textureFile, "texture"))
            : null;

        UIFont skinFont = FontOf(Path.GetFullPath(Path.Combine(directory, file)), fontDirectory, fontFile, fontSize);
        var skin = new UISkin(skinFont, sheet, worn?.Name, art);

        // Art that was drawn big to be shown small (art_scale under one) is smoothed on its way down, pixel art
        // never is. Before any of it is asked for, the atlas makes its texture the way this says
        if (properties.TryGetValue("smooth", out var smooth))
            skin.Atlas.Smooth = UIScript.ToBoolean(smooth, "smooth");

        // Before anything else. It is what every region is scaled by unless it says otherwise.
        if (properties.TryGetValue("art_scale", out var artScale))
            skin.ArtScale = UIScript.ToNumber(artScale, "art_scale");

        ReadPalette(properties, skin.Palette);

        foreach (var (key, value) in properties)
        {
            switch (key)
            {
                case "texture" or "font" or "sprites" or "theme" or "art_scale" or "title" or "smooth":
                    break;

                // Read above, the lot of them together
                case var color when UIPalette.IsKey(color):
                    break;

                case "text_scale":
                    skin.TextScale = UIScript.ToNumber(value, key);
                    break;
                case "row_height":
                    skin.RowHeight = MathF.Max(0.0f, UIScript.ToNumber(value, key));
                    break;
                case "text_sizes" when value is ObjectValue sizes:
                    foreach (var (role, size) in sizes.Properties)
                    {
                        float scale = UIScript.ToNumber(size, $"text_sizes.{role}");
                        switch (role)
                        {
                            case "caption": skin.CaptionScale = scale; break;
                            case "body": skin.TextScale = scale; break;
                            case "heading": skin.HeadingScale = scale; break;
                            case "title": skin.TitleScale = scale; break;
                            default: throw new Exception($"'{role}' isn't a size of text, caption, body, heading or title.");
                        }
                    }
                    break;
                case "corner_radius":
                    skin.CornerRadius = MathF.Max(0.0f, UIScript.ToNumber(value, key));
                    break;
                case "popup_radius":
                    skin.PopupRadius = UIScript.ToNumber(value, key);
                    break;
                case "icon_blink":
                    skin.IconBlink = MathF.Max(0.0f, UIScript.ToNumber(value, key));
                    break;
                // Read with the colours, see ReadPalette
                case "syntax":
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
    /// The palette of a skin in a theme, every colour of it, without loading any art or font and so from any thread
    /// and without a device, for a test or for a game that wants a theme's colours before it is drawn in them.
    /// Throws, saying what is wrong, for a file or theme that can't be read.
    /// </summary>
    /// <param name="theme">Which of the skin's themes, null for its usual one.</param>
    public static UIPalette PaletteOf(string directory, string file, string? theme = null)
    {
        var properties = ReadProperties(directory, file, 0);
        List<UITheme> themes = ReadThemes(directory, properties);
        ApplyTheme(properties, themes, theme);

        var palette = new UIPalette();
        ReadPalette(properties, palette);
        return palette;
    }

    /// <summary>
    /// Helper method to read every colour a skin file (with its theme over it) wrote into a palette, the colours of
    /// code with them, and have the palette work out the rest.
    /// </summary>
    private static void ReadPalette(Dictionary<string, IRuntimeValue> properties, UIPalette palette)
    {
        var given = new Dictionary<string, Vector4>();
        foreach (string key in UIPalette.Keys)
        {
            if (!properties.TryGetValue(key, out var value))
                continue;

            Vector4 color = UIScript.ToColor(value, key);
            given[key] = color;
            palette.Set(key, color);
        }

        bool syntaxGiven = properties.TryGetValue("syntax", out var syntax);
        if (syntaxGiven)
            ReadSyntax(palette.Syntax, syntax!);

        palette.Derive(given, syntaxGiven);
    }

    /// <summary>
    /// The colours code is shown in, by what they colour. A skin (or a theme) only lists the ones it wants its own way.
    /// </summary>
    private static void ReadSyntax(UISyntaxColors colors, IRuntimeValue value)
    {
        if (value is not ObjectValue syntax)
            throw new Exception("syntax has to be an object.");

        foreach (var (name, color) in syntax.Properties)
        {
            if (!colors.Set(name, UIScript.ToColor(color, $"syntax.{name}")))
                throw new Exception($"'{name}' isn't something code is coloured by.");
        }
    }

    /// <summary>
    /// A region is the name of a sprite, or an object, <c>{ sprite: "name" }</c> to change something about a
    /// sprite (its border, content, scale or tint) and <c>{ x, y, w, h }</c> to cut a rectangle out of the skin's texture.
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

        Vector4 textColor = properties.TryGetValue("text_color", out var textValue)
            ? UIScript.ToColor(textValue, $"{name}.text_color")
            : default;

        if (properties.TryGetValue("sprite", out var spriteName))
            return new RegionSource(UIScript.ToText(spriteName, $"{name}.sprite"), null, 0, 0, 0, 0, border, content, scale, tint, textColor);

        if (texture is null)
            throw new Exception($"Region '{name}' is a rectangle, but the skin names no texture to cut it out of.");

        int Number(string key) =>
            properties.TryGetValue(key, out var number)
                ? (int)UIScript.ToNumber(number, $"{name}.{key}")
                : throw new Exception($"Region '{name}' is missing its {key}.");

        // A rectangle with no border isn't stretched as a nine-slice, which is what null would leave to the sprite.
        return new RegionSource(
            null, texture, Number("x"), Number("y"), Number("w"), Number("h"), border ?? default(UIEdges), content ?? default(UIEdges), scale, tint, textColor);
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
