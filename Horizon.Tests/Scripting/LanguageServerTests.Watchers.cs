using System.IO.Pipes;
using System.Text.Json.Nodes;

using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public partial class LanguageServerTests
{
    /// <summary>
    /// A server that registered the files it watches is told of those alone, csharp-ls's **/*.{cs,...} getting no
    /// chat saved as JSON, and one under a folder of its own by the path from there; one that never asked is told of all.
    /// </summary>
    [Fact]
    public void FilesChangedTellsAServerOnlyWhatItRegisteredToWatch()
    {
        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toServerRead = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        using var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toClientRead = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
        string root = Path.Combine(Path.GetTempPath(), "watched-root");
        using LanguageServer server = LanguageServer.Over("watchers", "csharp", root, toClientRead, toServer);
        using var serverOutput = toClient;

        JsonObject initialize = ReadMessage(toServerRead);
        Assert.True(initialize["params"]!["capabilities"]!["workspace"]!["didChangeWatchedFiles"]!["dynamicRegistration"]!.GetValue<bool>());
        Write(toClient, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = initialize["id"]!.GetValue<int>(), ["result"] = new JsonObject { ["capabilities"] = new JsonObject() } }.ToJsonString());
        Assert.Equal("initialized", ReadMessage(toServerRead)["method"]!.GetValue<string>());

        // Before it says, everything
        string json = Path.Combine(root, "hex", "data", "chat.json");
        Assert.True(server.Watches(json, LanguageServerFileChange.Changed));
        Assert.False(server.WatchesFiles);

        string under = new Uri(Path.Combine(root, "nested") + Path.DirectorySeparatorChar).AbsoluteUri;
        Write(toClient, new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 7, ["method"] = "client/registerCapability",
            ["params"] = new JsonObject
            {
                ["registrations"] = new JsonArray(
                    new JsonObject
                    {
                        ["id"] = "files", ["method"] = "workspace/didChangeWatchedFiles",
                        ["registerOptions"] = new JsonObject { ["watchers"] = new JsonArray(new JsonObject { ["globPattern"] = "**/*.{cs,cshtml,csproj,sln,slnx}", ["kind"] = 7 }) }
                    },
                    new JsonObject
                    {
                        ["id"] = "deleted", ["method"] = "workspace/didChangeWatchedFiles",
                        ["registerOptions"] = new JsonObject { ["watchers"] = new JsonArray(new JsonObject
                        {
                            ["globPattern"] = new JsonObject { ["baseUri"] = under, ["pattern"] = "*.json" }, ["kind"] = 4
                        }) }
                    })
            }
        }.ToJsonString());
        JsonObject answered = ReadMessage(toServerRead);
        Assert.Equal(7, answered["id"]!.GetValue<int>());
        Assert.True(server.WatchesFiles);

        Assert.True(server.Watches(Path.Combine(root, "Hex.Dawn", "Languages", "HexLanguages.cs"), LanguageServerFileChange.Changed));
        Assert.True(server.Watches(Path.Combine(root, "Hex.Dawn", "Hex.Dawn.CSPROJ"), LanguageServerFileChange.Created));
        Assert.True(server.Watches(Path.Combine(root, "Horizon.Workspace.sln"), LanguageServerFileChange.Deleted));
        Assert.False(server.Watches(json, LanguageServerFileChange.Changed));
        Assert.False(server.Watches(Path.Combine(root, "Hex.Dawn", "obj", "project.assets.json"), LanguageServerFileChange.Changed));
        Assert.False(server.Watches(Path.Combine(root, "notes.csx"), LanguageServerFileChange.Changed));
        // Under its folder and of its kind alone
        Assert.True(server.Watches(Path.Combine(root, "nested", "a.json"), LanguageServerFileChange.Deleted));
        Assert.False(server.Watches(Path.Combine(root, "nested", "a.json"), LanguageServerFileChange.Changed));
        Assert.False(server.Watches(Path.Combine(root, "nested", "deeper", "a.json"), LanguageServerFileChange.Deleted));

        // What's said goes by it
        server.FilesChanged([(json, LanguageServerFileChange.Changed), (Path.Combine(root, "A.cs"), LanguageServerFileChange.Created)]);
        JsonObject told = ReadMessage(toServerRead);
        Assert.Equal("workspace/didChangeWatchedFiles", told["method"]!.GetValue<string>());
        JsonNode change = Assert.Single(told["params"]!["changes"]!.AsArray())!;
        Assert.EndsWith("/A.cs", change["uri"]!.GetValue<string>());

        // Nothing it wants, nothing sent: the next message is what comes after
        server.FilesChanged([(json, LanguageServerFileChange.Created)]);
        server.Saved(Path.Combine(root, "A.cs"));
        Assert.Equal("textDocument/didSave", ReadMessage(toServerRead)["method"]!.GetValue<string>());

        // Unregistered, the rest stays
        Write(toClient, """{"jsonrpc":"2.0","id":8,"method":"client/unregisterCapability","params":{"unregisterations":[{"id":"files","method":"workspace/didChangeWatchedFiles"}]}}""");
        Assert.Equal(8, ReadMessage(toServerRead)["id"]!.GetValue<int>());
        Assert.False(server.Watches(Path.Combine(root, "A.cs"), LanguageServerFileChange.Changed));
        Assert.True(server.Watches(Path.Combine(root, "nested", "a.json"), LanguageServerFileChange.Deleted));
    }

    [Theory]
    [InlineData("**/*.cs", "C:/a/b/c.cs", true)]
    [InlineData("**/*.cs", "c.cs", true)]
    [InlineData("**/*.cs", "C:/a/b/c.csx", false)]
    [InlineData("*.cs", "C:/a/b/c.cs", true)]
    [InlineData("src/*.ts", "/w/src/a.ts", true)]
    [InlineData("src/*.ts", "/w/src/x/a.ts", false)]
    [InlineData("**/{Cargo.toml,Cargo.lock}", "/w/crate/Cargo.lock", true)]
    [InlineData("**/file?.[a-c]", "/w/file1.b", true)]
    [InlineData("**/file?.[a-c]", "/w/file1.d", false)]
    [InlineData("**/node_modules/**", "/w/node_modules/x/y.js", true)]
    public void LspGlobsMatchTheWayEditorsTakeThem(string glob, string path, bool matches) =>
        Assert.Equal(matches, LanguageServer.GlobRegex(glob, anywhere: true).IsMatch(path));
}
