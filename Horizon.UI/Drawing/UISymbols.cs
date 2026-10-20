using System.Numerics;

using Horizon.Graphics;
using Horizon.HIDL.Runtime;
using Horizon.HIDL;
using Horizon.Logging;
using Horizon.Rendering.Text;

namespace Horizon.UI.Drawing;

/// <summary>
/// The icons any UI can draw whatever its skin, Codicons (VS Code's own, CC BY 4.0, Assets/fonts/codicons/README.txt),
/// five hundred and seventy of them. They come as a TrueType font with every picture a character up in the private use
/// area, so they are a distance field the same as the text is (<see cref="DistanceFieldFont"/> built with those
/// characters), crisp at any size and tinted by whoever draws them, and a name is a character by codicons.hor.
/// <para>
/// In text they are <c>$(name)</c>, VS Code's way of writing them, <c>"$(save) Save"</c>, a dash or an underscore
/// in the name the same (<c>$(git-branch)</c>). A name there is no icon of is left as it is written. On their own
/// they are <see cref="UIDrawList.Symbol(char, UIRect, Vector4)"/>, an <c>icon</c> on a button or a menu item, or the
/// <c>symbol</c> component.
/// </para>
/// </summary>
public static class UISymbols
{
    private const string DIRECTORY = "Assets/fonts/codicons/";
    private const string FONT_FILE = "codicon.ttf";
    private const string NAMES_FILE = "codicons.hor";

    // Where the pictures are in the font, all of them
    private static readonly (int From, int To)[] Characters = [(0xE000, 0xF8FF)];

    // How tall a symbol in a line of text is against the line, and the gap either side of it
    private const float IN_TEXT = 0.9f;
    private const float GAP = 0.15f;

    private static readonly Lock loading = new();
    private static DistanceFieldFont? font;
    private static float fieldSlope;

    // Both spellings of every name, the one with dashes the way VS Code writes them and the one with underscores
    private static readonly Dictionary<string, char> names = new(StringComparer.Ordinal);
    private static readonly List<string> listed = [];

    /// <summary>Whether the icons have loaded. They load with the first skin.</summary>
    public static bool IsLoaded => font is { Texture.Handle: not 0 };

    /// <summary>The atlas, a distance field.</summary>
    public static Texture Texture => font?.Texture ?? Texture.Invalid;

    /// <summary>Every name there is, with underscores, in order. For a picker of icons.</summary>
    public static IReadOnlyList<string> Names => listed;

    /// <summary>See <see cref="UIFont.FieldSlope"/>.</summary>
    internal static float FieldSlope => fieldSlope;

    /// <summary>
    /// Loads the icons, once, on whichever thread loads skins (it makes a texture). Nothing happens the second time.
    /// </summary>
    internal static void EnsureLoaded()
    {
        lock (loading)
        {
            if (font is not null)
                return;

            using var nobody = Horizon.Content.AssetScope.EnterGlobal();

            font = DistanceFieldFont.Load(DIRECTORY, FONT_FILE, DistanceFieldFont.DEFAULT_EM, Characters);
            foreach (var glyph in font.Glyphs.Values)
            {
                if (glyph.Size.X <= 0.0f || glyph.TexelSize.X <= 0.0f) continue;

                float spreadTexels = font.Spread * glyph.TexelSize.X / glyph.Size.X;
                if (spreadTexels > 0.0f) fieldSlope = 0.5f / spreadTexels;
                break;
            }

            ReadNames();
        }
    }

    private static void ReadNames()
    {
        string path = Path.Combine(DIRECTORY, NAMES_FILE);
        if (!File.Exists(path))
        {
            Log.Error($"[UISymbols] There are no names of the icons at '{path}'.");
            return;
        }

        var runtime = new HIDLRuntime();
        var (success, _) = runtime.Evaluate(File.ReadAllText(path));
        if (!success || runtime.UserScope.Lookup("icons") is not ObjectValue { Properties: { } icons })
        {
            Log.Error($"[UISymbols] '{path}' didn't come out as an object of icons.");
            return;
        }

        foreach (var (name, value) in icons)
        {
            if (value is not NumberValue { Value: var number }) continue;

            char code = (char)(int)number;
            names[name] = code;
            names[name.Replace('_', '-')] = code;
            listed.Add(name);
        }

        listed.Sort(StringComparer.Ordinal);
    }

    /// <summary>The character of an icon by its name, false if there is no icon of that name.</summary>
    public static bool TryGet(ReadOnlySpan<char> name, out char code)
    {
        code = default;
        return names.Count > 0 && names.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out code);
    }

    /// <summary>The picture of an icon in the atlas.</summary>
    internal static bool TryGetGlyph(char code, out CharDefinition glyph)
    {
        glyph = default;
        return font is not null && font.Glyphs.TryGetValue(code, out glyph) && glyph.Size.X > 0.0f;
    }

    /// <summary>The em of the font, which is the square every icon is drawn in.</summary>
    internal static float Em => font?.EmSize ?? DistanceFieldFont.DEFAULT_EM;

    /// <summary>
    /// Tests whether text starts with <c>$(name)</c> for an icon there is.
    /// </summary>
    /// <param name="lineHeight">How tall the line the icon is in is, in the units the width comes out in.</param>
    /// <param name="length">How many characters it takes up.</param>
    /// <param name="width">How much of the line it takes up, the icon and a little gap either side.</param>
    internal static bool TryRead(ReadOnlySpan<char> text, float lineHeight, out char code, out int length, out float width)
    {
        code = default;
        length = 0;
        width = 0.0f;

        if (text.Length < 4 || text[0] != '$' || text[1] != '(' || names.Count == 0)
            return false;

        int end = text.IndexOf(')');
        if (end < 3 || !TryGet(text[2..end], out code))
            return false;

        length = end + 1;
        width = MathF.Round(lineHeight * (IN_TEXT + GAP * 2.0f));
        return true;
    }

    /// <summary>Where an icon read out of text is drawn, in the room <see cref="TryRead"/> said it takes up.</summary>
    /// <param name="pen">The pen, the top left corner of the room in a line whose Y is up.</param>
    internal static UIRect InText(Vector2 pen, float lineHeight)
    {
        float side = MathF.Round(lineHeight * IN_TEXT);
        Vector2 corner = new(MathF.Round(pen.X + lineHeight * GAP), MathF.Round(pen.Y - (lineHeight + side) * 0.5f));
        return new UIRect(corner, corner + new Vector2(side));
    }
}
