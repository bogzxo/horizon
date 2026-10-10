using System.Collections.Concurrent;
using System.Numerics;

using Horizon.Logging;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Rendering.Spriting;
using Horizon.UI.Drawing;
using Horizon.UI.Scripting;

namespace Horizon.UI.Skinning;

/// <summary>
/// The look of a UI, the art the components draw with, a font and the colours and sizes components use
/// unless told otherwise.
/// The art comes from a sprite sheet definition (or straight out of an image) and is stitched into an
/// atlas of the skin's own, one piece at a time as it is first asked for. So a skin can sit on top of a
/// pack of thousands of sprites and only ever hold the handful a UI really uses, and any sprite of the
/// pack can be drawn by name without the skin having to list it.
/// Components ask for regions by name and fall back to flat colours when a skin doesn't have one, so
/// a skin only needs the art it actually has.
/// <para>
/// A skin is shared. Every UI that asks for the same file and theme draws with the one skin (see
/// <see cref="Shared"/>) and so with the one atlas, which is kept for as long as the game runs. Art is stitched in
/// once, the first time anything anywhere asks for it, rather than all over again by every screen that is opened.
/// </para>
/// </summary>
public sealed partial class UISkin : IUIIconSource, IDisposable
{
    private const string DEFAULT_FONT_DIRECTORY = "Assets/fonts/vcr_mono/";
    private const string DEFAULT_FONT_FILE = "vcr_mono.fnt";
    private const char FRAME_SEPARATOR = '#';
    private const int MAX_INHERITANCE = 8;

    /// <summary>What the skin file says a region is. A sprite of the sheet, or a rectangle of an image.</summary>
    private readonly record struct RegionSource(
        string? Sprite, string? Path, int X, int Y, int Width, int Height, UIEdges? Border, UIEdges? Content, float? Scale, Vector4 Tint);

    /// <summary>What the skin file says an icon is.</summary>
    private readonly record struct IconSource(
        string Region, string Label, Vector4? LabelColor, string? Symbol = null, Vector4 SymbolColor = default, float SymbolSize = 0.5f, float Height = 0.0f);

    /// <summary>A name worked out down to the pixels it stands for, which can be done before any of it is loaded.</summary>
    private sealed record Art(SpriteSource Source, UIEdges Border, UIEdges Content, float Scale, Vector4 Tint, string Key);

    /// <summary>An icon worked out the same way. One with frames animates in text, the names of its frames are made once.</summary>
    private sealed record IconArt(
        string Region, Vector2 TexelSize, string Label, Vector4 LabelColor, string? Symbol, Vector4 SymbolColor, float SymbolSize, float LineTexels,
        int Frames, float FrameTime)
    {
        public string[]? FrameNames { get; set; }
    }

    private readonly Dictionary<string, RegionSource> sources = [];
    private readonly Dictionary<string, IconSource> icons = [];

    // The sets of icons the skin has. In a set, the name on the left draws the icon on the right
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

    /// <summary>
    /// How often (in seconds) an icon with frames plays them, a gamepad button that presses itself say, resting on
    /// its first frame in between. <c>icon_blink</c> in the skin file.
    /// </summary>
    public float IconBlink { get; set; } = 3.0f;

    /// <summary>The sprite sheet definition the art is looked up in, null for a skin that only cuts its regions out of an image.</summary>
    public SpriteSheetDefinition? Sheet { get; }

    /// <summary>Which theme of the sprite sheet the skin uses, for sheets that hold their art in several colours.</summary>
    public string? Theme { get; }

    /// <summary>
    /// How many units on screen a texel of the art takes up. Pixel art is usually drawn at two or three
    /// times its size, and everything a region measures (its size, its borders) is scaled along.
    /// </summary>
    public float ArtScale { get; private set; } = 1.0f;

    /// <summary>The scale text is drawn at unless a component asks for its own.</summary>
    public float TextScale { get; set; } = 0.4f;

    /// <summary>The colour of text that stands on its own. Labels and captions.</summary>
    public Vector4 TextColor { get; set; } = new(0.08f, 0.09f, 0.13f, 1.0f);

    /// <summary>The colour of text drawn on top of a control's art, like the label of a button.</summary>
    public Vector4 ControlTextColor { get; set; } = new(0.08f, 0.09f, 0.13f, 1.0f);

    /// <summary>What a control is filled with when the skin has no art for it.</summary>
    public Vector4 ControlColor { get; set; } = new(0.78f, 0.81f, 0.87f, 1.0f);

    /// <summary>What something to type into is filled with when the skin has no art for it.</summary>
    public Vector4 FieldColor { get; set; } = new(0.0f, 0.0f, 0.0f, 0.35f);

    /// <summary>The thin line around controls that are drawn without art. See-through (which it is unless a skin says otherwise) for none.</summary>
    public Vector4 BorderColor { get; set; }

    /// <summary>
    /// How round the corners are of everything that is drawn without art, in pixels. Zero for square ones.
    /// See <see cref="UIDrawList.Box"/>.
    /// </summary>
    public float CornerRadius { get; set; }

    /// <summary>The colours code is shown in, see <see cref="Components.CodeView"/>.</summary>
    public UISyntaxColors Syntax { get; } = new();

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

    /// <summary>The gap between related things. A toggle and its caption, the children of a stack.</summary>
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
    /// Puts the art that was asked for since the last time into the atlas. Has to be called on the render
    /// thread, the compositor does before it draws.
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
    /// see <see cref="UIFont"/>). If the set has another icon for that name, that is the one. A set is how the
    /// same text shows the buttons of whichever gamepad is being held. The text says <c>[icon:pad_a]</c>, and the
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
    /// Finds a piece of art by name. A region the skin declares, or failing that any sprite of its sheet
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

    // How big the image files that are drawn whole are, in pixels, read off the files once. Zero for one that can't be read
    private readonly ConcurrentDictionary<string, Vector2> imageSizes = new();

    /// <summary>
    /// How big an image file is, in pixels, which is what an image drawn out of it whole measures. Zero if it isn't there or
    /// isn't a PNG. From any thread, the file is only looked at the first time.
    /// </summary>
    public Vector2 ImageSize(string path) => imageSizes.GetOrAdd(path, static path =>
    {
        try
        {
            // Width and height are the first thing in a PNG after its signature and the header's length and name
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[24];
            if (file.Read(header) < header.Length || header[1] != (byte)'P' || header[2] != (byte)'N' || header[3] != (byte)'G')
                return Vector2.Zero;

            return new Vector2(
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..]),
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..]));
        }
        catch (Exception)
        {
            return Vector2.Zero;
        }
    });

    /// <summary>
    /// An image file, the whole of it, as a piece of art of the skin, stitched into the atlas the first time it is
    /// asked for (and there by the next frame), drawn at the scale of the UI like the rest of the skin. For art that
    /// belongs to a layout rather than the skin, a logo say. PNG only.
    /// </summary>
    public bool TryGetImage(string path, out UIRegion region)
    {
        string name = "file:" + path;
        if (regions.TryGetValue(name, out region))
            return true;

        Vector2 size = ImageSize(path);
        if (size.X <= 0.0f || size.Y <= 0.0f)
            return false;

        int width = (int)size.X, height = (int)size.Y;
        string key = TextureAtlas.KeyFor(path, 0, 0, width, height);

        Atlas.Request(key, path, 0, 0, width, height);
        if (!Atlas.TryGet(key, out var placed))
            return false;

        region = new UIRegion(placed.Position, placed.Size);
        regions[name] = region;
        return true;
    }

    /// <summary>
    /// Finds an icon by name, the way an <c>[icon:name]</c> tag does, an icon the skin declares or
    /// failing that the region of that name.
    /// </summary>
    public bool TryGetIcon(ReadOnlySpan<char> name, out UIIcon icon) => TryGetIcon(name, 0.0f, out icon);

    /// <summary>
    /// Finds an icon by name at a moment in time, for the ones that animate. An icon whose art has frames shows
    /// whichever frame is due at that time, round and round. Zero is the first frame, always.
    /// </summary>
    /// <param name="time">Seconds, from whenever. Only how far along the animation is comes out of it.</param>
    public bool TryGetIcon(ReadOnlySpan<char> name, float time, out UIIcon icon)
    {
        icon = default;

        if (DescribeIcon(name) is not { } art || !TryGetRegion(art.Region, out var region))
            return false;

        if (art.Frames > 1 && time > 0.0f && art.FrameTime > 0.0f)
        {
            // Played through once every so often and left on the first frame in between, a button that pressed
            // itself over and over looked like it was flickering
            float play = art.Frames * art.FrameTime;
            float cycle = MathF.Max(play, IconBlink);
            float phase = time % cycle;
            int frame = phase < play ? (int)(phase / art.FrameTime) % art.Frames : 0;
            if (frame > 0)
            {
                // The names are made once, this is asked on every draw of every icon. And made somewhere else,
                // see NameFrames for what making them in here cost
                art.FrameNames ??= NameFrames(art);

                // A frame that isn't in the atlas yet is asked for by this, and the first one stands in until it is
                if (TryGetRegion(art.FrameNames[frame], out var due))
                    region = due;
            }
        }

        // A symbol that hasn't made it into the atlas yet is left off for a frame, the icon is still an icon.
        UIRegion? symbol = art.Symbol is not null && TryGetRegion(art.Symbol, out var found) ? found : null;

        icon = new UIIcon(region, art.Label, art.LabelColor, symbol, art.SymbolColor, art.SymbolSize);
        return true;
    }

    /// <summary>
    /// Finds an icon the way a text written in a set of icons does, at a moment in time. See <see cref="TryGetIcon(ReadOnlySpan{char}, float, out UIIcon)"/>.
    /// </summary>
    public bool TryGetIcon(ReadOnlySpan<char> name, ReadOnlySpan<char> set, float time, out UIIcon icon) =>
        TryGetIcon(ResolveIcon(name, set), time, out icon);

    /// <summary>
    /// Helper method to name the frames of an icon that animates. A loop in a method of its own. It was a lambda
    /// inside of <see cref="TryGetIcon(ReadOnlySpan{char}, float, out UIIcon)"/> that needed the icon, and what a
    /// lambda needs is put on the heap as the method it is written in starts. So every icon in every text made
    /// an object every time it was drawn, a hundred and twenty times a second each, to name frames that had
    /// their names already. An input display is nothing but icons.
    /// </summary>
    private static string[] NameFrames(IconArt art)
    {
        var names = new string[art.Frames];
        for (int i = 0; i < names.Length; i++)
            names[i] = $"{art.Region}{FRAME_SEPARATOR}{i}";

        return names;
    }

    float IUIIconSource.IconScale => IconScale;

    bool IUIIconSource.TryGetIconSize(ReadOnlySpan<char> name, ReadOnlySpan<char> set, out Vector2 texelSize, out float lineTexels)
    {
        IconArt? art = DescribeIcon(ResolveIcon(name, set));

        texelSize = art?.TexelSize ?? default;
        lineTexels = art?.LineTexels ?? 0.0f;
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
                source.SymbolSize,
                source.Height > 0.0f ? source.Height : art.Source.Height,
                art.Source.Frames,
                art.Source.FrameTime)
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
                    Log.Error($"[UISkin] '{name}' is meant to be the sprite '{sprite + frame}', which the sprite sheet doesn't have.");
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
    /// then on. This is what a UI gets its skin with. Has to run on the render thread. It is never disposed of,
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
    /// itself), a new one that is the caller's own and theirs to dispose of, see <see cref="Shared"/> for the one
    /// a UI normally draws with. Has to run on the render thread. Logs what went wrong and returns null if the skin can't be loaded.
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
            Log.Error($"[UISkin] Failed to load '{Path.Combine(directory, file)}': {e.Message}");
            return null;
        }
    }
}
