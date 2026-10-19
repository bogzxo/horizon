using System.Numerics;

using Horizon.UI.Skinning;

namespace Horizon.Tests;

/// <summary>
/// Every theme of every skin, read off the disk without a device, and whether its text can be read. The contrast
/// between what is written and what it is written on, worked out the way the web's guidelines do (WCAG 2, the
/// ratio of the two luminances, 4.5 for text to read easily and 3 for text that is big or beside the point), each
/// colour laid over what is under it by its alpha first. A theme somebody writes is held to it the day its file is
/// there, and a theme that fails says which pair and by how much.
/// </summary>
public class UIThemeLegibilityTests
{
    private const string ROOT = "Horizon/Assets/uix";

    // What easy reading takes, what the quiet and the coloured text take, and what a comment in the code may sink to
    private const float TEXT = 4.5f, SECONDARY = 3.0f, QUIET = 2.4f;

    private static string Repo()
    {
        for (string? at = AppContext.BaseDirectory; at is not null; at = Path.GetDirectoryName(at))
        {
            if (File.Exists(Path.Combine(at, "Horizon.sln")))
                return at;
        }

        throw new DirectoryNotFoundException("The tests have to run somewhere under the repo.");
    }

    private static string Folder(string skin) => Path.Combine(Repo(), ROOT, skin) + Path.DirectorySeparatorChar;

    /// <summary>Every skin and every theme of it, the skin's only look as a null theme.</summary>
    public static TheoryData<string, string?> Themes()
    {
        var data = new TheoryData<string, string?>();
        foreach (string folder in Directory.GetDirectories(Path.Combine(Repo(), ROOT)).Order())
        {
            if (!File.Exists(Path.Combine(folder, UISkins.FILE)))
                continue;

            string skin = Path.GetFileName(folder)!;
            var (_, themes, _) = UISkin.Describe(Folder(skin), UISkins.FILE);
            if (themes.Length == 0) data.Add(skin, null);
            foreach (string theme in themes) data.Add(skin, theme);
        }

        return data;
    }

    private static bool HasArt(string skin) => File.Exists(Path.Combine(Folder(skin), "sprites.hor"));

    /// <summary>The relative luminance of a colour, the way WCAG 2 has it, with the sRGB curve taken out.</summary>
    private static float Luminance(Vector4 color)
    {
        static float Channel(float c) => c <= 0.03928f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        return 0.2126f * Channel(color.X) + 0.7152f * Channel(color.Y) + 0.0722f * Channel(color.Z);
    }

    /// <summary>A colour laid over another by its alpha, what the eye gets.</summary>
    private static Vector4 Over(Vector4 top, Vector4 under)
    {
        float a = top.W;
        return new Vector4(top.X * a + under.X * (1.0f - a), top.Y * a + under.Y * (1.0f - a), top.Z * a + under.Z * (1.0f - a), 1.0f);
    }

    /// <summary>The contrast ratio of text on a background, 1 for the same colour and 21 for black on white.</summary>
    public static float Contrast(Vector4 text, Vector4 background)
    {
        float lighter = Luminance(Over(text, background)), darker = Luminance(background);
        if (lighter < darker) (lighter, darker) = (darker, lighter);
        return (lighter + 0.05f) / (darker + 0.05f);
    }

    private static Vector4 Opaque(Vector4 color) => Over(color, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));

    [Theory]
    [MemberData(nameof(Themes))]
    public void Text_reads_on_what_it_is_written_on(string skin, string? theme)
    {
        UIPalette palette = UISkin.PaletteOf(Folder(skin), UISkins.FILE, theme);
        string where = $"{skin} {theme ?? "(one look)"}";
        var failed = new List<string>();

        void Check(string what, Vector4 text, Vector4 on, float least)
        {
            float ratio = Contrast(text, on);
            if (ratio < least) failed.Add($"{what} is {ratio:0.00} to 1, wants {least:0.0}");
        }

        // A panel with alpha sits over whatever the game draws, the darkest that can be is black. A skin with art
        // writes on its art and its flat colours are only for what has none, so only the flat skin's controls are
        // held to the full measure, the rest to what the quiet text takes
        Vector4 panel = Opaque(palette.Panel);
        Vector4 field = Over(palette.Field, panel);
        Vector4 control = Over(palette.Control, panel);
        bool art = HasArt(skin);

        Check("the text on a panel", palette.Text, panel, TEXT);
        Check("the quiet text on a panel", palette.DimText, panel, art ? QUIET : SECONDARY);
        Check("the text in a field", palette.Text, field, art ? SECONDARY : TEXT);
        Check("a placeholder in a field", palette.DimText, field, QUIET);
        Check("the label on a control", palette.ControlText, control, art ? SECONDARY : TEXT);
        Check("a link on a panel", palette.Link, panel, SECONDARY);
        Check("an error on a panel", palette.Error, panel, SECONDARY);
        Check("a warning on a panel", palette.Warning, panel, SECONDARY);
        Check("a success on a panel", palette.Success, panel, SECONDARY);
        // With a shadow under it the text on a bar is outlined, so it only has to be told apart from the fill
        Check("the text on a bar's fill", Over(palette.BarText, palette.Accent), Over(palette.Accent, panel), palette.TextShadow.W > 0.3f ? 1.5f : SECONDARY);

        // The code is written on the field, its line numbers in the gutter, which is the panel
        UISyntaxColors code = palette.Syntax;
        foreach (var (name, color) in new (string, Vector4)[]
        {
            ("name", code.Name), ("keyword", code.Keyword), ("property", code.Property), ("call", code.Call), ("text", code.Text),
            ("number", code.Number), ("punctuation", code.Punctuation), ("directive", code.Directive), ("declaration", code.Declaration),
            ("builtin", code.Builtin), ("type", code.Type), ("constant", code.Constant), ("reference", code.Reference),
            ("escape", code.Escape), ("operator", code.Operator), ("bracket1", code.Bracket1), ("bracket2", code.Bracket2), ("bracket3", code.Bracket3)
        })
        {
            Check($"code, a {name}", color, field, SECONDARY);
        }
        Check("code, a comment", code.Comment, field, QUIET);
        Check("code, the markup", code.Markup, field, QUIET);
        Check("code, a line number", code.LineNumber, panel, 1.8f);

        Assert.True(failed.Count == 0, $"{where} (text {palette.Text}, panel {panel}, field {field}, control {control}): {string.Join("; ", failed)}");
    }

    [Fact]
    public void The_contrast_is_worked_out_the_way_the_guidelines_say()
    {
        Assert.Equal(21.0f, Contrast(Vector4.One, new Vector4(0.0f, 0.0f, 0.0f, 1.0f)), 1);
        Assert.Equal(1.0f, Contrast(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), new Vector4(0.5f, 0.5f, 0.5f, 1.0f)), 2);

        // White at half strength over black is grey on black
        Assert.True(Contrast(new Vector4(1.0f, 1.0f, 1.0f, 0.5f), new Vector4(0.0f, 0.0f, 0.0f, 1.0f)) < 10.0f);
    }

    [Fact]
    public void The_kinds_of_text_follow_the_palette()
    {
        // A dark one gets light error and success colours, a light one deep ones, and the quiet text is the text seen through
        var dark = new UIPalette { Text = new(0.9f, 0.9f, 0.9f, 1.0f), Panel = new(0.1f, 0.1f, 0.1f, 1.0f), Accent = new(0.2f, 0.5f, 1.0f, 1.0f) };
        dark.Derive(new Dictionary<string, Vector4> { ["accent_color"] = dark.Accent });
        Assert.Equal(dark.Accent, dark.Link);
        Assert.Equal(dark.Text with { W = 0.62f }, dark.DimText);
        Assert.True(UIPalette.Luminance(dark.Error) > 0.3f);

        var light = new UIPalette { Text = new(0.1f, 0.1f, 0.1f, 1.0f), Panel = new(0.95f, 0.95f, 0.95f, 1.0f) };
        light.Derive(new Dictionary<string, Vector4>());
        Assert.True(UIPalette.Luminance(light.Error) < 0.3f);
        Assert.True(UIPalette.Luminance(light.Success) < 0.4f);

        // Written out, taken as written (the loader sets what was written before it asks for the rest)
        var own = new UIPalette { Error = Vector4.One, DimText = Vector4.UnitW };
        own.Derive(new Dictionary<string, Vector4> { ["error_color"] = Vector4.One, ["dim_text_color"] = Vector4.UnitW });
        Assert.Equal(Vector4.One, own.Error);
        Assert.Equal(Vector4.UnitW, own.DimText);
    }
}
