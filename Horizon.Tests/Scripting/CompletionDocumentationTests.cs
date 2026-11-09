using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public class CompletionDocumentationTests
{
    [Fact]
    public async Task ResolutionPreservesOpaqueDataAndCachesOneRequestWithoutChangingInsertion()
    {
        if (!LanguageServers.IsAvailable("python")) return;
        string script = LanguageServerTests.FAKE_SERVER
            .Replace("import sys, json, re", "import sys, json, re\nresolve_count = 0")
            .Replace("\"textDocumentSync\": 1", "\"textDocumentSync\": 1, \"completionProvider\": {\"resolveProvider\": True}")
            .Replace("\"detail\": \"the first\", \"kind\": 3", "\"detail\": \"the first\", \"kind\": 3, \"data\": {\"opaque\": [42, \"token\"]}")
            .Replace("    elif method == \"textDocument/completion\":", """
                
                    elif method == "completionItem/resolve":
                        assert message["params"]["data"] == {"opaque": [42, "token"]}
                        resolve_count += 1
                        resolved = dict(message["params"])
                        resolved["detail"] = "resolved " + str(resolve_count)
                        resolved["documentation"] = {"kind": "markdown", "value": "The **fixture** documentation."}
                        resolved["insertText"] = "must not replace the original insertion"
                        send({"jsonrpc": "2.0", "id": message["id"], "result": resolved})
                    elif method == "textDocument/completion":
                """);
        string path = Path.Combine(Path.GetTempPath(), "hex-resolve-" + Guid.NewGuid() + ".py");
        File.WriteAllText(path, script);
        try
        {
            using var server = LanguageServer.Start("resolve", "python", [path], "fake", Path.GetTempPath());
            Assert.NotNull(server);
            string file = Path.Combine(Path.GetTempPath(), "resolve.fake");
            server.Open(file, "a");
            var result = await server.Complete(file, 0, 1, 0).WaitAsync(TimeSpan.FromSeconds(10));
            var item = Assert.Single(result!.Value.Items, item => item.Text == "alpha");
            Assert.Equal("the first", item.Detail);
            Assert.NotNull(item.ResolveDocumentation);
            var first = item.ResolveDocumentation();
            Assert.Same(first, item.ResolveDocumentation());
            var resolved = await first.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(resolved);
            Assert.Equal("resolved 1", resolved.Value.Detail);
            Assert.Equal("The fixture documentation.", resolved.Value.Documentation);
            Assert.Equal("alpha", item.Insert ?? item.Text);
        }
        finally { File.Delete(path); }
    }
}
