using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public class CodeActionResolutionTests
{
    [Fact]
    public async Task DeferredActionsPreserveDataResolveOnceAndRejectDisabledOrFailedResolutions()
    {
        Assert.True(LanguageServers.IsAvailable("python"));
        string script = LanguageServerTests.FAKE_SERVER
            .Replace("\"textDocumentSync\": 1", "\"textDocumentSync\": 1, \"codeActionProvider\": {\"resolveProvider\": True}")
            .Replace("    if method == \"initialize\":", """
                
                    if method == "initialize":
                        capabilities = message['params']['capabilities']['textDocument']['codeAction']
                        assert capabilities['dataSupport']
                        assert capabilities['resolveSupport']['properties'] == ['edit']
                """)
            .Replace("    elif method == \"textDocument/completion\":", """
                
                    elif method == "textDocument/codeAction":
                        uri = message['params']['textDocument']['uri']
                        actions = [{'title': title, 'kind': 'quickfix', 'data': {'opaque': [42, 'token'], 'uri': uri}} for title in ['Deferred.', 'Disabled', 'Broken']]
                        actions.append({'title': 'Bare', 'command': 'fixture.command'})
                        if 'only' in message['params']['context']:
                            assert message['params']['context']['only'] == ['source.organizeImports']
                            actions.append(dict(actions[0], title='Organize', kind='source.organizeImports'))
                        send({'jsonrpc': '2.0', 'id': message['id'], 'result': actions})
                    elif method == "codeAction/resolve":
                        action = message['params']
                        assert action['data']['opaque'] == [42, 'token']
                        if action['title'] == 'Broken':
                            send({'jsonrpc': '2.0', 'id': message['id'], 'error': {'code': -32603, 'message': 'fixture failure'}})
                            continue
                        if action['title'] == 'Disabled': action['disabled'] = {'reason': 'no longer available'}
                        action['edit'] = {'changes': {action['data']['uri']: [{'range': {'start': {'line': 0, 'character': 0}, 'end': {'line': 0, 'character': 3}}, 'newText': 'fixed'}]}}
                        send({'jsonrpc': '2.0', 'id': message['id'], 'result': action})
                    elif method == "textDocument/completion":
                """);
        string path = Path.Combine(Path.GetTempPath(), "hex-action-resolve-" + Guid.NewGuid() + ".py");
        File.WriteAllText(path, script);
        try
        {
            using var server = LanguageServer.Start("action-resolve", "python", [path], "fake", Path.GetTempPath());
            Assert.NotNull(server);
            Assert.True(SpinWait.SpinUntil(() => server.IsReady || server.Failure is not null, TimeSpan.FromSeconds(10)));
            Assert.True(server.IsReady, server.Failure);
            string file = Path.Combine(Path.GetTempPath(), "resolve-action.fake");
            server.Open(file, "old");
            var actions = await server.Actions(file, 0, 0, 0, 3).WaitAsync(TimeSpan.FromSeconds(10));
            var deferred = Assert.Single(actions, action => action.Title == "Deferred");
            Assert.Empty(deferred.Edits);
            Assert.NotNull(deferred.Resolve);
            var first = deferred.Resolve();
            Assert.Same(first, deferred.Resolve());
            var resolved = await first.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(resolved);
            Assert.Null(resolved.Resolve);
            Assert.Equal("fixed", Assert.Single(resolved.Edits[file]).Text);
            Assert.Null(await actions.Single(action => action.Title == "Disabled").Resolve!());
            await Assert.ThrowsAnyAsync<Exception>(async () => await actions.Single(action => action.Title == "Broken").Resolve!());
            Assert.Null(actions.Single(action => action.Title == "Bare").Resolve);
            var organized = await server.Actions(file, 0, 0, 0, 3, "source.organizeImports").WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("Organize", Assert.Single(organized).Title);
            Assert.NotNull(organized[0].Resolve);

        }
        finally { File.Delete(path); }
    }
}
