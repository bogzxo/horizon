using System.Collections.Concurrent;
using System.Numerics;

using Bogz.Logging;
using Bogz.Logging.Loggers;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Scripting;

namespace Horizon.Rendering.UIX.Skinning;

/// <summary>
/// The look of a UI: the art the components draw with, a font, and the colours and sizes components use
/// unless told otherwise.
/// The art comes from a sprite sheet definition (or straight out of an image) and is stitched into an
/// atlas of the skin's own, one piece at a time as it is first asked for. So a skin can sit on top of a
/// pack of thousands of sprites and only ever hold the handful a UI really uses, and any sprite of the
/// pack can be drawn by name without the skin having to list it.
/// Components ask for regions by name and fall back to flat colours when a skin doesn't have one, so
/// a skin only needs the art it actually has.
/// <para>
/// A skin is shared: every UI that asks for the same file and theme draws with the one skin (see
/// <see cref="Shared"/>) and so with the one atlas, which is kept for as long as the game runs. Art is stitched in
/// once, the first time anything anywhere asks for it, rather than all over again by every screen that is opened.
/// </para>
/// </summary>
public sealed class UISkin : IUIIconSource, IDisposable
{
    private const string DEFAULT_FONT_DIRECTORY = "fonts/vcr_mono/";
    private const string DEFAULT_FONT_FILE = "vcr_mono.fnt";
    private const char FRAME_SEPARATOR = '#';
    private const int MAX_INHERITANCE = 8;

    /// <summary>What the skin file says a region is: a sprite of the sheet, or a rectangle of an image.</summary>
    private readonly record struct RegionSource(
        string? Sprite, string? Path, int X, int Y, int Width, int Height, UIEdges? Border, UIEdges? Content, float? Scale, Vector4 Tint);

    /// <summary>What the skin file says an icon is.</summary>
    private readonly record struct IconSource(
        string Region, string Label, Vector4? LabelColor, string? Symbol = null, Vector4 SymbolColor = default, float SymbolSize = 0.5f);

    /// <summary>A name worked out down to the pixels it stands for, which can be done before any of it is loaded.</summary>
    private sealed record Art(SpriteSource Source, UIEdges Border, UIEdges Content, float Scale, Vector4 Tint, string Key);

    /// <summary>An icon worked out the same way.</summary>
    private sealed record IconArt(
        string Region, Vector2 TexelSize, string Label, Vector4 LabelColor, string? Symbol, Vector4 SymbolColor, float SymbolSize);

    private readonly Dictionary<string, RegionSource> sources = [];
    private readonly Dictionary<string, IconSource> icons = [];

    // The sets of icons the skin has: in a set, the name on the left draws the icon on the right
    private readonly Dictionary<string, Dictionary<string, string>> iconSets = [];

    // The skins that have been loaded, by where they are from and their theme
    private static readonly Dictionary<(string Path, string? Theme), UISkin> shared = [];

    // Filled in as names are asked for. A name that stands for nothing is remembered as null, as
    // components ask for art a skin might not have on every single frame.
    private readonly ConcurrentDictionary<string, Art?> arts = new();
    private readonly ConcurrentDictionary<string, IconArt?> iconArts = new();
    private readonly ConcurrentDictionary<string, UIRegion> regions = new();

    /// <summary>The texture the art of the skin is stitched into.</summary>
    public TextureAtlas Atlas { get; } = new();

    public UIFont Font { get; }

    /// <summary>The sprite sheet definition the art is looked up in, null for a skin that only cuts its regions out of an image.</summary>
    public SpriteSheetDefinition? Sheet { get; }

    /// <summary>Which theme of the sprite sheet the skin uses, for sheets that hold their art in several colours.</summary>
    public string? Theme { get; }

    /// <summary>
    /// How many units on screen a texel of the art takes up. Pixel art is usually drawn at two or three
    /// times its size; everything a region measures (its size, its borders) is scaled along.
    /// </summary>
    public float ArtScale { get; private set; } = 1.0f;

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

    /// <summary>The colour of the label on an icon that doesn't say its own.</summary>
    public Vector4 IconLabelColor { get; set; } = new(1.0f, 1.0f, 1.0f, 1.0f);

    /// <summary>
    /// How tall the icons in a line of text are, as a multiple of the height of the line. Every icon is
    /// drawn at that height, whatever the size of its art.
    /// </summary>
    public float IconScale { get; set; } = 1.0f;

    /// <summary>
    /// Whether the fill of a progress bar or slider is drawn on top of its frame rather than underneath.
    /// For art whose frame is the whole empty bar, not just the rim of one.
    /// </summary>
    public bool ProgressFillOverFrame { get; set; }

    /// <summary>The space a button keeps between its label and its edges.</summary>
    public UIEdges ButtonPadding { get; set; } = new(16.0f, 10.0f);

    /// <summary>How far inside the frame of a progress bar or slider its fill sits.</summary>
    public UIEdges ProgressInset { get; set; } = new(7.0f, 6.0f);

    /// <summary>The side length of the on/off indicator of a toggle.</summary>
    public float ToggleSize { get; set; } = 30.0f;

    /// <summary>The gap between related things: a toggle and its caption, the children of a stack.</summary>
    public float Spacing { get; set; } = 10.0f;

    /// <summary>
    /// The themes the skin file comes in, the same skin with some things done differently (its art in another
    /// colour). Empty for a skin that is just the one.
    /// </summary>
    public IReadOnlyList<string> Themes { get; private set; } = [];

    public UISkin(UIFont font, SpriteSheetDefinition? sheet = null, string? theme = null)
    {
        Font = font;
        Sheet = sheet;
        Theme = theme;

        // The font can't know what an icon is, but text has to make room for them.
        font.Icons = this;
    }

    /// <summary>
    /// Puts the art that was asked for since the last time into the atlas. Has to be called on the GL
    /// thread; the compositor does, before it draws.
    /// </summary>
    /// <returns>Whether any art arrived.</returns>
    internal bool Update()
    {
        // Nothing to add, and the atlas has its texture (which it only gets the first time it is updated)
        if (!Atlas.HasPending && Atlas.Texture.Handle != 0) return false;

        // The atlas belongs to nobody, least of all to the scene that happened to be drawing when it grew
        using var nobody = Horizon.Content.AssetScope.EnterGlobal();
        return Atlas.Update();
    }

    /// <summary>
    /// Whether art that was asked for is still on its way into the atlas, which it gets the next time a UI with
    /// this skin is drawn. What is painted in the meantime is painted without it.
    /// </summary>
    public bool HasPendingArt => Atlas.HasPending;

    /// <summary>The sets of icons the skin has, see <see cref="TryGetIcon(ReadOnlySpan{char}, ReadOnlySpan{char}, out UIIcon)"/>.</summary>
    public IReadOnlyCollection<string> IconSets => iconSets.Keys;

    /// <summary>
    /// Finds an icon by name the way a text that is written in a set of icons does (<c>[icons:playstation]</c>,
    /// see <see cref="UIFont"/>): if the set has another icon for that name, that is the one. A set is how the
    /// same text shows the buttons of whichever gamepad is being held: the text says <c>[icon:pad_a]</c>, and the
    /// set of a gamepad whose buttons aren't called that says what to draw for it. A name the set says nothing
    /// about is the icon it always is, and so is every name in a set the skin doesn't have.
    /// </summary>
    public bool TryGetIcon(ReadOnlySpan<char> name, ReadOnlySpan<char> set, out UIIcon icon) =>
        TryGetIcon(ResolveIcon(name, set), out icon);

    private ReadOnlySpan<char> ResolveIcon(ReadOnlySpan<char> name, ReadOnlySpan<char> set)
    {
        if (set.IsEmpty || iconSets.Count == 0)
            return name;

        if (iconSets.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(set, out var names)
            && names.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out string? other))
        {
            return other;
        }

        return name;
    }

    /// <summary>
    /// Finds a piece of art by name: a region the skin declares, or failing that any sprite of its sheet
    /// ("name#2" being the third frame of one that has frames). Art that is asked for the first time isn't
    /// there until the next frame is drawn, as it has to be stitched into the atlas first.
    /// </summary>
    public bool TryGetRegion(string name, out UIRegion region)
    {
        if (regions.TryGetValue(name, out region))
            return true;

        if (name.Length == 0 || Describe(name) is not { } art)
            return false;

        Atlas.Request(art.Key, art.Source.Path, art.Source.X, art.Source.Y, art.Source.Width, art.Source.Height);
        if (!Atlas.TryGet(art.Key, out var placed))
            return false;

        region = new UIRegion(
            placed.Position, placed.Size, art.Border, art.Scale, art.Tint, art.Source.Frames, art.Source.FrameTime, art.Content);
        regions[name] = region;
        return true;
    }

    /// <summary>
    /// Finds an icon by name, the way an <c>[icon:name]</c> tag does: an icon the skin declares, or
    /// failing that the region of that name.
    /// </summary>
    public bool TryGetIcon(ReadOnlySpan<char> name, out UIIcon icon)
    {
        icon = default;

        if (DescribeIcon(name) is not { } art || !TryGetRegion(art.Region, out var region))
            return false;

        // A symbol that hasn't made it into the atlas yet is left off for a frame, the icon is still an icon.
        UIRegion? symbol = art.Symbol is not null && TryGetRegion(art.Symbol, out var found) ? found : null;

        icon = new UIIcon(region, art.Label, art.LabelColor, symbol, art.SymbolColor, art.SymbolSize);
        return true;
    }

    float IUIIconSource.IconScale => IconScale;

    bool IUIIconSource.TryGetIconSize(ReadOnlySpan<char> name, ReadOnlySpan<char> set, out Vector2 texelSize)
    {
        texelSize = DescribeIcon(ResolveIcon(name, set))?.TexelSize ?? default;
        return texelSize != default;
    }

    private IconArt? DescribeIcon(ReadOnlySpan<char> name)
    {
        // Looked up by the characters of the tag as they are, so text with icons in it doesn't make a
        // string for every one of them on every frame.
        var lookup = iconArts.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(name, out var known))
            return known;

        string key = name.ToString();
        IconSource source = icons.TryGetValue(key, out var declared) ? declared : new IconSource(key, string.Empty, null);

        IconArt? icon = Describe(source.Region) is { } art
            ? new IconArt(
                source.Region,
                new Vector2(art.Source.Width, art.Source.Height),
                source.Label,
                source.LabelColor ?? IconLabelColor,
                source.Symbol,
                source.SymbolColor,
                source.SymbolSize)
            : null;

        iconArts[key] = icon;
        return icon;
    }

    private Art? Describe(string name) => arts.TryGetValue(name, out var art) ? art : arts.GetOrAdd(name, Lookup);

    private Art? Lookup(string name)
    {
        // A single frame is asked for as "name#3", of whatever the name stands for.
        string frame = string.Empty;
        int separator = name.LastIndexOf(FRAME_SEPARATOR);
        if (separator > 0)
        {
            frame = name[separator..];
            name = name[..separator];
        }

        SpriteSource source;
        UIEdges? border = null;
        UIEdges? content = null;
        float scale = ArtScale;
        Vector4 tint = Vector4.One;

        if (sources.TryGetValue(name, out var declared))
        {
            if (declared.Sprite is { } sprite)
            {
                if (Sheet is null || !Sheet.TryGetSprite(sprite + frame, Theme, out source))
                {
                    ConcurrentLogger.Instance.Log(
                        LogLevel.Error,
                        $"[UISkin] '{name}' is meant to be the sprite '{sprite + frame}', which the sprite sheet doesn't have.");
                    return null;
                }
            }
            else
            {
                if (frame.Length > 0)
                    return null;

                source = new SpriteSource(
                    declared.Path!, declared.X, declared.Y, declared.Width, declared.Height, Vector4.Zero, Vector4.Zero, 1, 0.1f);
            }

            border = declared.Border;
            content = declared.Content;
            scale = declared.Scale ?? ArtScale;
            tint = declared.Tint;
        }
        else if (Sheet is null || !Sheet.TryGetSprite(name + frame, Theme, out source))
        {
            return null;
        }

        return new Art(
            source,
            border ?? new UIEdges(source.Border.X, source.Border.Y, source.Border.Z, source.Border.W),
            content ?? new UIEdges(source.Content.X, source.Content.Y, source.Content.Z, source.Content.W),
            scale,
            tint,
            // The same pixels asked for under two names are only stitched in once.
            TextureAtlas.KeyFor(source.Path, source.X, source.Y, source.Width, source.Height));
    }

    public void Dispose() => Atlas.Dispose();

    /// <summary>
    /// The skin of a file and a theme, loaded the first time it is asked for and the same one for everybody from
    /// then on: this is what a UI gets its skin with. Has to run on the GL thread. It is never disposed of,
    /// whoever asks next (the next screen) finds it as it was left, with all the art that was ever drawn from it
    /// in its atlas already. Null if the skin can't be loaded, which has been logged, and is tried again the next time.
    /// </summary>
    public static UISkin? Shared(string directory, string file, string? theme = null)
    {
        var key = (Path.GetFullPath(Path.Combine(directory, file)), theme);
        if (shared.TryGetValue(key, out UISkin? known))
            return known;

        // Whatever it makes on the GPU stays for good, whichever scene is being set up right now
        using var nobody = Horizon.Content.AssetScope.EnterGlobal();

        if (Load(directory, file, theme) is not { } skin)
            return null;

        return shared[key] = skin;
    }

    /// <summary>
    /// Loads a skin from a HIDL definition (see Assets/uix/dead_revolver/skin.hor for one that explains
    /// itself), a new one that is the caller's own and theirs to dispose of: see <see cref="Shared"/> for the one
    /// a UI normally draws with. Has to run on the GL thread. Logs what went wrong and returns null if the skin can't be loaded.
    /// </summary>
    /// <param name="theme">Which of the skin's themes to load, null for the one the file says is its usual one.</param>
    public static UISkin? Load(string directory, string file, string? theme = null)
    {
        try
        {
            var properties = ReadProperties(directory, file, 0);
            string[] themes = ApplyTheme(properties, theme);

            UISkin skin = Read(directory, properties);
            skin.Themes = themes;
            return skin;
        }
        catch (Exception e)
        {
            ConcurrentLogger.Instance.Log(
                LogLevel.Error,
                $"[UISkin] Failed to load '{Path.Combine(directory, file)}': {e.Message}");
            return null;
        }
    }

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

        var skin = new UISkin(new UIFont(fontDirectory, fontFile), sheet, theme);

        // Before anything else: it is what every region is scaled by unless it says otherwise.
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

        // Its icons too, with the symbols some of them carry: they are what a text asks for the moment somebody
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
    /// and a symbol_size.
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

        sources[own] = ReadRegion(name, new ObjectValue(properties), texture);
        icons[name] = new IconSource(own, label, labelColor, symbol, symbolColor, symbolSize);
    }
}
