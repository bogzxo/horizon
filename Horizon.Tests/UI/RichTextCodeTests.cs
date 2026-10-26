using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// The blocks of code of markdown coloured by the language their fence says, the way the code editor colours it, a
/// patch in green and red, and code nothing knows in one colour like it always was.
/// </summary>
public class RichTextCodeTests
{
    // A font that isn't there, everything comes out with no width, which is all a test of colours needs
    private static readonly UIFont font = new(Path.GetTempPath(), "no-font-here.ttf");

    private static UIRichText Laid(string text, string? codeLanguage = null)
    {
        var rich = new UIRichText { CodeLanguage = codeLanguage };
        rich.Build(text, font, 1.0f, 0.0f, markdown: true);
        return rich;
    }

    private static HashSet<Vector4> ColoursOf(UIRichText rich, int block, UIPalette palette)
    {
        var seen = new HashSet<Vector4>();
        rich.CodeColours(block, palette, seen);
        return seen;
    }

    [Fact]
    public void A_block_of_csharp_comes_out_in_the_colours_of_csharp()
    {
        var palette = new UIPalette();
        UIRichText rich = Laid("```cs\nclass A { int x = 1; }\n```");

        Assert.Equal(1, rich.CodeBlockCount);
        HashSet<Vector4> seen = ColoursOf(rich, 0, palette);
        Assert.True(seen.Count >= 3, $"only {seen.Count} colours");

        // Every one of them a colour the editor gives C#
        UISyntaxColors editor = CodeLanguages.ColorsFor(CodeLanguages.ForExtension(".cs"), palette);
        Assert.All(seen, colour => Assert.Contains(Enum.GetValues<SyntaxKind>(), kind => editor.Of(kind) == colour));
    }

    [Fact]
    public void A_language_can_be_said_by_its_id_or_its_name()
    {
        var palette = new UIPalette();
        Assert.True(Laid("```csharp\nclass A { int x = 1; }\n```").CodeColours(0, palette) >= 3);
        Assert.True(Laid("```json\n{ \"a\": 1, \"b\": true }\n```").CodeColours(0, palette) >= 3);
        Assert.True(Laid("```hidl\nlet speed = 3;\nfunc add(a, b) { return a + b; }\n```").CodeColours(0, palette) >= 3);
    }

    [Fact]
    public void A_patch_has_its_added_lines_green_and_its_taken_out_lines_red()
    {
        var palette = new UIPalette();
        UIRichText rich = Laid("```diff\n@@ -1,2 +1,2 @@\n context\n-gone\n+here\n```");

        HashSet<Vector4> seen = ColoursOf(rich, 0, palette);
        Assert.Contains(palette.Success, seen);
        Assert.Contains(palette.Error, seen);
        Assert.Contains(palette.Accent, seen);
        Assert.Contains(palette.Text, seen);
    }

    [Fact]
    public void A_language_nothing_knows_is_code_in_one_colour()
    {
        var palette = new UIPalette();
        UIRichText rich = Laid("```no-such-language-at-all\nclass A { int x = 1; }\n```");

        Assert.Equal(1, rich.CodeBlockCount);
        Assert.Equal(1, rich.CodeColours(0, palette));
    }

    [Fact]
    public void A_fence_that_says_nothing_is_whatever_the_text_says_code_is()
    {
        var palette = new UIPalette();
        const string code = "```\nlet speed = 3;\nfunc add(a, b) { return a + b; }\n```";

        Assert.Equal(1, Laid(code).CodeColours(0, palette));
        Assert.True(Laid(code, "hidl").CodeColours(0, palette) >= 3);

        // A fence that names one wins over it
        Assert.Equal(1, Laid("```text\nlet speed = 3;\n```", "hidl").CodeColours(0, palette));
    }

    [Fact]
    public void Markdown_round_the_code_is_left_as_it_was()
    {
        var palette = new UIPalette();
        UIRichText rich = Laid("# A heading\n- a bullet with `inline code`\n\n```cs\nint x = 1;\n```\nafter it");

        // Inline code is no block, and the one block there is ends at its fence
        Assert.Equal(1, rich.CodeBlockCount);
        Assert.Equal(0, rich.CodeColours(1, palette));
        Assert.Equal(0, Laid("plain `code` and **bold**").CodeBlockCount);
    }

    [Fact]
    public void A_block_nobody_closed_is_still_coloured()
    {
        var palette = new UIPalette();
        Assert.True(Laid("```cs\nclass A { int x = 1; }").CodeColours(0, palette) >= 3);
    }
}
