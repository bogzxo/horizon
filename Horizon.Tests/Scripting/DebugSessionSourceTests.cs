using System.Text.Json.Nodes;
using Horizon.UI.Scripting;

namespace Horizon.Tests;

public partial class DebugSessionTests
{
    private static DebugFrame StopWithSource(GotoAdapter adapter, JsonObject source)
    {
        adapter.Event("stopped", new JsonObject { ["reason"] = "breakpoint", ["threadId"] = 41 });
        adapter.Answer(adapter.Read(), new JsonObject { ["stackFrames"] = new JsonArray(new JsonObject
            { ["id"] = 9, ["name"] = "generated", ["line"] = 4, ["column"] = 3, ["source"] = source }) });
        DebugEvent stopped = default;
        Assert.True(WaitFor(() => adapter.Session.TryTake(out stopped) && stopped.Kind == DebugEventKind.Stopped));
        return Assert.Single(stopped.Frames);
    }

    [Theory]
    [InlineData(DebugSession.CORECLR)]
    [InlineData(DebugSession.PYTHON)]
    [InlineData("lldb-dap")]
    public async Task SourceReferencePreservesOpaqueDescriptorAndReadsNativeContentInsteadOfDisk(string kind)
    {
        string disk = Path.GetTempFileName();
        try
        {
            File.WriteAllText(disk, "wrong local file");
            using var adapter = new GotoAdapter();
            await adapter.Launch(kind: kind);
            var descriptor = new JsonObject
            {
                ["name"] = "Generated.cs", ["path"] = disk, ["sourceReference"] = 7, ["origin"] = "generated code",
                ["adapterData"] = new JsonObject { ["token"] = new JsonArray("opaque", 4, true) },
                ["checksums"] = new JsonArray(new JsonObject { ["algorithm"] = "SHA256", ["checksum"] = "not interpreted by the client" })
            };
            var expected = descriptor.DeepClone();
            var frame = StopWithSource(adapter, descriptor);
            var source = Assert.IsType<DebugSource>(frame.Source);
            Assert.Equal(3, frame.Line);
            Assert.Equal(2, frame.Column);
            Assert.Equal(disk, frame.File);
            Assert.Equal("Generated.cs", source.Name);
            Assert.Equal("generated code", source.Origin);
            Assert.True(adapter.Session.OwnsSource(source));
            var reading = adapter.Session.ReadSource(source);
            var request = adapter.Read();
            Assert.Equal("source", request["command"]!.GetValue<string>());
            Assert.Equal(7, request["arguments"]!["sourceReference"]!.GetValue<int>());
            Assert.True(JsonNode.DeepEquals(expected, request["arguments"]!["source"]));
            const string native = "// native α😀\r\n\tint answer = 42;\n";
            adapter.Answer(request, new JsonObject { ["content"] = native, ["mimeType"] = "text/x-csharp" });
            var content = Assert.IsType<DebugSourceContent>(await reading.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(source, content.Source);
            Assert.Equal(native, content.Text);
            Assert.Equal("text/x-csharp", content.MimeType);
            Assert.Equal("wrong local file", File.ReadAllText(disk));
        }
        finally { File.Delete(disk); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task LocalFramesKeepTheirFileWithoutCreatingAnAdapterSource(int reference)
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch();
        var frame = StopWithSource(adapter, new JsonObject { ["path"] = "/local/Program.cs", ["sourceReference"] = reference });
        Assert.Null(frame.Source);
        Assert.Equal("/local/Program.cs", frame.File);
    }

    [Fact]
    public async Task SourceReferencesCannotCrossSessionsOrSurviveSessionTermination()
    {
        using var first = new GotoAdapter();
        using var second = new GotoAdapter();
        await first.Launch();
        await second.Launch();
        var source = StopWithSource(first, new JsonObject { ["name"] = "virtual.py", ["sourceReference"] = 1 }).Source!;
        Assert.False(second.Session.OwnsSource(source));
        await Assert.ThrowsAsync<ArgumentException>(() => second.Session.ReadSource(source));
        var reading = first.Session.ReadSource(source);
        var request = first.Read();
        first.Event("terminated");
        Assert.True(WaitFor(() => first.Session.State == DebugState.Ended));
        first.Answer(request, new JsonObject { ["content"] = "late source" });
        Assert.Null(await reading.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(first.Session.OwnsSource(source));
        Assert.Null(await first.Session.ReadSource(source));
    }

    [Fact]
    public async Task SourceContentCanBeEmptyAndItsReferenceRemainsValidWhileRunning()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch();
        var source = StopWithSource(adapter, new JsonObject { ["name"] = "virtual.py", ["sourceReference"] = 1 }).Source!;
        adapter.Session.Continue();
        adapter.Answer(adapter.Read());
        var reading = adapter.Session.ReadSource(source);
        adapter.Answer(adapter.Read(), new JsonObject { ["content"] = "" });
        var content = Assert.IsType<DebugSourceContent>(await reading.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(content.Text);
        Assert.Null(content.MimeType);
    }

    [Fact]
    public async Task MalformedAndCancelledSourceRequestsDoNotReturnInventedText()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch();
        var source = StopWithSource(adapter, new JsonObject { ["sourceReference"] = 1 }).Source!;
        var invalid = adapter.Session.ReadSource(source);
        adapter.Answer(adapter.Read(), new JsonObject { ["content"] = 42 });
        await Assert.ThrowsAsync<InvalidDataException>(() => invalid);
        using var cancel = new CancellationTokenSource();
        var reading = adapter.Session.ReadSource(source, cancel.Token);
        var request = adapter.Read();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        adapter.Answer(request, new JsonObject { ["content"] = "late" });
        Assert.Equal(DebugState.Stopped, adapter.Session.State);
    }
}
