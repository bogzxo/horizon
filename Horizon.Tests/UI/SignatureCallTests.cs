using Horizon.UI.Components;

namespace Horizon.Tests.UI;

public class SignatureCallTests
{
    private sealed class Editor : CodeEditor
    {
        public void Advance() => Update(0.01f);
    }

    [Theory]
    [InlineData("call(',', ")]
    [InlineData("call('a,b', ")]
    [InlineData("call('a\\'b,c', ")]
    [InlineData("call(\"a\\\"b,c\", ")]
    [InlineData("call(\"a,b\", ")]
    [InlineData("call(inner(1, 2), ")]
    [InlineData("call(1'000, ")]
    [InlineData("call(0xAB'CD, ")]
    [InlineData("call(@\"a\"\"b,c\", ")]
    public void CommasInsideQuotedArgumentsDoNotAdvanceTheParameter(string text)
    {
        int argument = -1;
        var editor = new Editor { Text = text, SignatureSource = (name, index) =>
        {
            Assert.Equal("call", name);
            argument = index;
            return new UISignature("call(first, second)", 12, 6);
        }};
        editor.Document.MoveToEnd(false);
        editor.Advance();
        Assert.Equal(1, argument);
        Assert.NotNull(editor.Signature);
    }
}
