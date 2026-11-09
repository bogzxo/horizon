using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public class TypeHierarchyTests
{
    [Theory]
    [InlineData("textDocument/prepareTypeHierarchy")]
    [InlineData("typeHierarchy/supertypes")]
    [InlineData("typeHierarchy/subtypes")]
    public async Task ServerErrorsAreNotReportedAsAnEmptyHierarchy(string method)
    {
        string script = LanguageServerTests.FAKE_SERVER
            .Replace("\"textDocumentSync\": 1", "\"textDocumentSync\": 1, \"typeHierarchyProvider\": True")
            .Replace("    elif method == \"textDocument/completion\":", """

                    elif method in ('textDocument/prepareTypeHierarchy', 'typeHierarchy/supertypes', 'typeHierarchy/subtypes'):
                        send({'jsonrpc':'2.0', 'id':message['id'], 'error':{'code':-32603, 'message':'Hierarchy index unavailable'}})
                    elif method == "textDocument/completion":
                """);
        string path = Path.Combine(Path.GetTempPath(), "hex-type-error-" + Guid.NewGuid() + ".py");
        File.WriteAllText(path, script);
        try
        {
            using var server = LanguageServer.Start("type-error", "python", [path], "fake", Path.GetTempPath());
            Assert.NotNull(server);
            Assert.True(SpinWait.SpinUntil(() => server.IsReady || server.Failure is not null, TimeSpan.FromSeconds(10)));
            Assert.True(server.IsReady, server.Failure);
            var item = new LanguageServerTypeItem("Type", "", 5, new(path, 0, 0, 0, 4), new());
            var error = await Assert.ThrowsAnyAsync<Exception>(() => (method == "textDocument/prepareTypeHierarchy"
                ? server.TypeItems(path, 0, 0) : server.RelatedTypes(item, method.EndsWith("supertypes", StringComparison.Ordinal)))
                .WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("Hierarchy index unavailable", error.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task NativeItemsKeepOpaqueDataAndSelectionAndResolveBothDirections()
    {
        string script = LanguageServerTests.FAKE_SERVER
            .Replace("\"textDocumentSync\": 1", "\"textDocumentSync\": 1, \"typeHierarchyProvider\": True")
            .Replace("    elif method == \"textDocument/completion\":", """

                    elif method == "textDocument/prepareTypeHierarchy":
                        p = message['params']
                        assert p['position'] == {'line':2,'character':3}
                        span = {'start':{'line':1,'character':0}, 'end':{'line':8,'character':0}}
                        selection = {'start':{'line':2,'character':3}, 'end':{'line':2,'character':9}}
                        item = {'name':'Middle', 'kind':5, 'detail':'fixture', 'uri':p['textDocument']['uri'], 'range':span, 'selectionRange':selection, 'data':{'token':[17,'opaque']}}
                        send({'jsonrpc':'2.0','id':message['id'],'result':[item]})
                    elif method in ('typeHierarchy/supertypes', 'typeHierarchy/subtypes'):
                        item = message['params']['item']
                        assert item['data'] == {'token':[17,'opaque']}
                        assert item['name'] == 'Middle'
                        item['name'] = 'Base' if method.endswith('/supertypes') else 'Leaf'
                        send({'jsonrpc':'2.0','id':message['id'],'result':[item]})
                    elif method == "textDocument/completion":
                """);
        string path = Path.Combine(Path.GetTempPath(), "hex-type-hierarchy-" + Guid.NewGuid() + ".py");
        File.WriteAllText(path, script);
        try
        {
            using var server = LanguageServer.Start("type-hierarchy", "python", [path], "fake", Path.GetTempPath());
            Assert.NotNull(server);
            Assert.True(SpinWait.SpinUntil(() => server.IsReady || server.Failure is not null, TimeSpan.FromSeconds(10)));
            Assert.True(server.IsReady, server.Failure);
            var item = Assert.Single(await server.TypeItems(path, 2, 3).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(2, item.Declared.Line);
            Assert.Equal(3, item.Declared.Character);
            Assert.Equal("fixture", item.Detail);
            Assert.Equal("Base", Assert.Single(await server.RelatedTypes(item, true).WaitAsync(TimeSpan.FromSeconds(10))).Name);
            Assert.Equal("Leaf", Assert.Single(await server.RelatedTypes(item, false).WaitAsync(TimeSpan.FromSeconds(10))).Name);
            Assert.Equal("Middle", item.Item["name"]!.GetValue<string>());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ClangdResolvesInheritance()
    {
        string? clangd = Environment.GetEnvironmentVariable("HEX_TEST_CLANGD");
        if (string.IsNullOrEmpty(clangd)) return;
        string root = Path.Combine(Path.GetTempPath(), "hex-native-type-hierarchy-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "types.cpp");
        const string source = "struct Base {};\nstruct Middle : Base {};\nstruct Leaf : Middle {};\n";
        File.WriteAllText(path, source);
        try
        {
            using var server = LanguageServer.Start("type-hierarchy", clangd, ["--background-index=false"], "cpp", root);
            Assert.NotNull(server);
            Assert.True(SpinWait.SpinUntil(() => server.IsReady || server.Failure is not null, TimeSpan.FromSeconds(15)));
            Assert.True(server.IsReady, server.Failure);
            Assert.True(server.Offers("typeHierarchyProvider"));
            server.Open(path, source);
            var item = Assert.Single(await server.TypeItems(path, 1, 9).WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal("Middle", item.Name);
            var parent = Assert.Single(await server.RelatedTypes(item, true).WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal("Base", parent.Name);
            Assert.Equal(0, parent.Declared.Line);
            var child = Assert.Single(await server.RelatedTypes(item, false).WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal("Leaf", child.Name);
            Assert.Equal(2, child.Declared.Line);
            Assert.Empty(await server.RelatedTypes(parent, true).WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal("Middle", Assert.Single(await server.RelatedTypes(child, true).WaitAsync(TimeSpan.FromSeconds(15))).Name);
        }
        finally { Directory.Delete(root, true); }
    }
}
