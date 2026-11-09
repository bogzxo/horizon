using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public class RangeFormattingTests
{
    [Fact]
    public async Task SendsTheSelectionAndIndentationOptionsAndReturnsNativeEdits()
    {
        Assert.True(LanguageServers.IsAvailable("python"));
        string script = LanguageServerTests.FAKE_SERVER
            .Replace("\"textDocumentSync\": 1", "\"textDocumentSync\": 1, \"documentRangeFormattingProvider\": True")
            .Replace("    elif method == \"textDocument/completion\":", """
                
                    elif method == "textDocument/rangeFormatting":
                        params = message['params']
                        assert params['range'] == {'start':{'line':2,'character':3}, 'end':{'line':4,'character':5}}
                        assert params['options'] == {'tabSize':8,'insertSpaces':False}
                        send({'jsonrpc':'2.0', 'id':message['id'], 'result':[{'range':params['range'],'newText':'formatted selection'}]})
                    elif method == "textDocument/completion":
                """);
        string path = Path.Combine(Path.GetTempPath(), "hex-range-format-" + Guid.NewGuid() + ".py");
        File.WriteAllText(path, script);
        try
        {
            using var server = LanguageServer.Start("range-format", "python", [path], "fake", Path.GetTempPath());
            Assert.NotNull(server);
            Assert.True(SpinWait.SpinUntil(() => server.IsReady || server.Failure is not null, TimeSpan.FromSeconds(10)));
            Assert.True(server.IsReady, server.Failure);
            var result = await server.FormatRange(Path.Combine(Path.GetTempPath(), "format.fake"), 2, 3, 4, 5, 8, false).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(result);
            Assert.Equal(new LanguageServerEdit(2, 3, 4, 5, "formatted selection"), Assert.Single(result));
        }
        finally { File.Delete(path); }
    }
}
