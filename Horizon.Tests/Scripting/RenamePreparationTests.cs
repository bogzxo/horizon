using System.Text.Json.Nodes;
using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public class RenamePreparationTests
{
    [Theory]
    [InlineData("{\"start\":{\"line\":0,\"character\":1},\"end\":{\"line\":0,\"character\":5}}", true, null)]
    [InlineData("{\"range\":{\"start\":{\"line\":0,\"character\":1},\"end\":{\"line\":0,\"character\":5}},\"placeholder\":\"server_name\"}", true, "server_name")]
    [InlineData("{\"defaultBehavior\":true}", true, null)]
    [InlineData("{\"defaultBehavior\":false}", false, null)]
    [InlineData("null", false, null)]
    [InlineData("{}", false, null)]
    [InlineData("{\"start\":{\"line\":0,\"character\":5},\"end\":{\"line\":0,\"character\":1}}", false, null)]
    [InlineData("error", false, null)]
    public async Task HonorsNativePreparationResultsAndErrors(string response, bool accepted, string? placeholder)
    {
        Assert.True(LanguageServers.IsAvailable("python"));
        string script = LanguageServerTests.FAKE_SERVER
            .Replace("\"textDocumentSync\": 1", "\"textDocumentSync\": 1, \"renameProvider\": {\"prepareProvider\": True}")
            .Replace("    if method == \"initialize\":", """
                
                    if method == "initialize":
                        capability = message['params']['capabilities']['textDocument']['rename']
                        assert capability['prepareSupport']
                        assert capability['prepareSupportDefaultBehavior'] == 1
                """)
            .Replace("    elif method == \"textDocument/completion\":", """
                
                    elif method == "textDocument/prepareRename":
                        assert message['params']['position'] == {'line': 0, 'character': 2}
                        response = RESPONSE
                        if response == 'error':
                            send({'jsonrpc':'2.0', 'id':message['id'], 'error':{'code':-32602, 'message':'Cannot rename this symbol'}})
                        else:
                            send({'jsonrpc':'2.0', 'id':message['id'], 'result':json.loads(response)})
                    elif method == "textDocument/completion":
                """.Replace("RESPONSE", JsonValue.Create(response)!.ToJsonString()));
        string path = Path.Combine(Path.GetTempPath(), "hex-rename-prepare-" + Guid.NewGuid() + ".py");
        File.WriteAllText(path, script);
        try
        {
            using var server = LanguageServer.Start("rename-prepare", "python", [path], "fake", Path.GetTempPath());
            Assert.NotNull(server);
            Assert.True(SpinWait.SpinUntil(() => server.IsReady || server.Failure is not null, TimeSpan.FromSeconds(10)));
            Assert.True(server.PreparesRename, server.Failure);
            string file = Path.Combine(Path.GetTempPath(), "rename.fake");
            if (response == "error")
            {
                var error = await Assert.ThrowsAnyAsync<Exception>(() => server.PrepareRename(file, 0, 2));
                Assert.Contains("Cannot rename this symbol", error.Message);
                return;
            }
            var result = await server.PrepareRename(file, 0, 2).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(accepted, result is not null);
            if (result is not null)
            {
                Assert.Equal(placeholder, result.Placeholder);
                if (response.Contains("defaultBehavior")) Assert.Null(result.Range);
                else Assert.Equal(new LanguageServerPlace(file, 0, 1, 0, 5), result.Range);
            }
        }
        finally { File.Delete(path); }
    }
}
