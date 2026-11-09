using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public class CallHierarchyErrorTests
{
    [Theory]
    [InlineData("textDocument/prepareCallHierarchy", true)]
    [InlineData("callHierarchy/incomingCalls", true)]
    [InlineData("callHierarchy/outgoingCalls", true)]
    [InlineData("textDocument/prepareCallHierarchy", false)]
    [InlineData("callHierarchy/incomingCalls", false)]
    [InlineData("callHierarchy/outgoingCalls", false)]
    public async Task DistinguishesServerErrorsFromEmptyResults(string method, bool fail)
    {
        string response = fail ? "'error':{'code':-32603,'message':'Call index unavailable'}" : "'result':None";
        string script = LanguageServerTests.FAKE_SERVER
            .Replace("\"textDocumentSync\": 1", "\"textDocumentSync\": 1, \"callHierarchyProvider\": True")
            .Replace("    elif method == \"textDocument/completion\":",
                "    elif method in ('textDocument/prepareCallHierarchy', 'callHierarchy/incomingCalls', 'callHierarchy/outgoingCalls'):\n"
                + "        send({'jsonrpc':'2.0','id':message['id']," + response + "})\n"
                + "    elif method == \"textDocument/completion\":");
        string path = Path.Combine(Path.GetTempPath(), "hex-call-error-" + Guid.NewGuid() + ".py");
        File.WriteAllText(path, script);
        try
        {
            using var server = LanguageServer.Start("call-error", "python", [path], "fake", Path.GetTempPath());
            Assert.NotNull(server);
            Assert.True(SpinWait.SpinUntil(() => server.IsReady || server.Failure is not null, TimeSpan.FromSeconds(10)));
            Assert.True(server.IsReady, server.Failure);
            async Task Request()
            {
                if (method == "textDocument/prepareCallHierarchy")
                    Assert.Empty(await server.CallItems(path, 0, 0).WaitAsync(TimeSpan.FromSeconds(10)));
                else
                {
                    var item = new LanguageServerCallItem("Method", "", 6, new(path, 0, 0, 0, 6), new());
                    Assert.Empty(await (method.EndsWith("incomingCalls", StringComparison.Ordinal)
                        ? server.IncomingCalls(item) : server.OutgoingCalls(item)).WaitAsync(TimeSpan.FromSeconds(10)));
                }
            }
            if (fail)
            {
                var error = await Assert.ThrowsAnyAsync<Exception>(Request);
                Assert.Contains("Call index unavailable", error.Message);
            }
            else await Request();
        }
        finally { File.Delete(path); }
    }
}
