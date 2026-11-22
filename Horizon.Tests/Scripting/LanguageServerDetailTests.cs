using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

/// <summary>What a completion's detail and documentation say beside the list, as the servers write them.</summary>
public class LanguageServerDetailTests
{
    [Theory]
    // serve-d's own, the name taken out
    [InlineData("int x", "x", "int")]
    // csharp-ls says the member with its type, which went with it
    [InlineData("Voice Mixer.Add(float frequency, float gain)", "Add", "Voice (float frequency, float gain)")]
    // TypeScript's kind in brackets and its member's type
    [InlineData("(property) Track.title: string", "title", "string")]
    [InlineData("(method) Array<number>.reduce(callback): number", "reduce", "(callback): number")]
    // Nothing of the name in it, all of it
    [InlineData("string", "title", "string")]
    public void A_detail_says_what_the_name_beside_it_doesnt(string detail, string label, string expected) =>
        Assert.Equal(expected, LanguageServer.DetailWithout(detail, label));

    [Fact]
    public void Documentation_is_said_without_its_markdown()
    {
        string docs = LanguageServer.PlainDocs(
            "The position CSS property sets how an element is positioned in a document\\. The top, right, bottom, and left properties determine the final location of positioned elements\\.\n\n"
            + "![Baseline icon](data:image/svg+xml;base64,AAAA) _Widely available across major browsers (Baseline since 2015)_\n\n"
            + "[MDN Reference](https://developer.mozilla.org/docs/Web/CSS/position)");

        Assert.Contains("in a document. The top", docs);
        Assert.Contains("positioned elements.", docs);
        Assert.DoesNotContain("\\", docs);
        Assert.DoesNotContain("Baseline icon", docs);
        Assert.Contains("Widely available across major browsers (Baseline since 2015)", docs);
        Assert.DoesNotContain("_", docs);
        Assert.Contains("MDN Reference", docs);
    }

    [Fact]
    public void An_html_entity_is_said_as_its_character() =>
        Assert.Equal("Syntax: <color>", LanguageServer.PlainDocs("Syntax: &lt;color&gt;"));

    [Fact]
    public void A_name_with_underscores_keeps_them() =>
        Assert.Contains("snake_case_name", LanguageServer.PlainDocs("Returns snake_case_name as it is."));
}
