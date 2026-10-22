using Horizon.UI.Drawing;

namespace Horizon.Tests.UI;

/// <summary>What somebody wrote, kept as it is in a label that reads markdown (a path in the language servers' list).</summary>
public class UIRichTextLiteralTests
{
    [Fact]
    public void A_path_keeps_its_backslashes_and_underscores_in_markdown()
    {
        Assert.Equal(@"C:\\Users\\bogz\\my\_tools\\serve-d.exe", UIRichText.MarkdownLiteral(@"C:\Users\bogz\my_tools\serve-d.exe"));
        Assert.Equal(@"npm i \*\*", UIRichText.MarkdownLiteral("npm i **"));
    }

    [Fact]
    public void Text_markdown_leaves_alone_comes_back_the_same()
    {
        const string plain = "serve-d 0.8.0 on the PATH";
        Assert.Same(plain, UIRichText.MarkdownLiteral(plain));
    }
}
