using System.Text.Json.Nodes;

using Horizon.UI.Scripting;

namespace Horizon.Tests;

public partial class DebugSessionTests
{
    [Theory]
    [InlineData("debugpy", 4)]
    [InlineData("lldb-dap", 0)]
    public async Task NativeAdapterCompletionOffsetsReplaceThePrecedingPrefix(string kind, int expectedStart)
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(complete: true, kind: kind);
        adapter.StopAt();
        var completing = adapter.Session.Completions("box.va + 1", 6, 1);
        JsonObject item = kind == "debugpy" ? new() { ["label"] = "value", ["start"] = 4, ["length"] = 2 }
            : new() { ["label"] = "box.value", ["length"] = 6 };
        adapter.Answer(adapter.Read(), new JsonObject { ["targets"] = new JsonArray(item) });
        var found = Assert.Single((await completing)!);
        Assert.Equal(expectedStart, found.Start);
        Assert.Equal(6, found.Start + found.Length);
    }

    [Fact]
    public async Task DebuggerCompletionsHonorUtf16RangesInsertionSelectionAndNativeSort()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(complete: true);
        adapter.StopAt();
        var completing = adapter.Session.Completions("😀.ca", 5, 17);
        var request = adapter.Read();
        Assert.Equal("completions", request["command"]!.GetValue<string>());
        Assert.Equal(17, request["arguments"]!["frameId"]!.GetValue<int>());
        Assert.Equal(6, request["arguments"]!["column"]!.GetValue<int>());
        adapter.Answer(request, JsonNode.Parse("""
            {"targets":[
              {"label":"call","text":"call(arg)","detail":"function","type":"function","sortText":"a","start":4,"length":2,"selectionStart":5,"selectionLength":3},
              {"label":"zebra"},
              {"label":"invalid-range","start":0},
              {"label":"outside","start":4,"length":99},
              {"label":"split-surrogate","start":2,"length":4},
              {"label":"bad-selection","selectionStart":999},
              {"label":""}]}
            """)!.AsObject());
        var found = (await completing)!;
        Assert.Equal(2, found.Count);
        Assert.Equal(new DebugCompletion("call", "call(arg)", "function", "function", 3, 2, 5, 3), found[0]);
        Assert.Equal(new DebugCompletion("zebra", "zebra", "", "", 5, 0, 5, 0), found[1]);
    }

    [Fact]
    public async Task DebuggerCompletionsRejectAnswersFromAnEarlierStopAndUnsupportedAdapters()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(complete: true);
        Assert.Null(await adapter.Session.Completions("box.", 4, 1));
        adapter.StopAt();
        var completing = adapter.Session.Completions("box.", 4, 1);
        var request = adapter.Read();
        adapter.Session.StepOver();
        adapter.Answer(adapter.Read());
        adapter.StopAt(2);
        adapter.Answer(request, GotoAdapter.Targets());
        Assert.Null(await completing);
        using var unsupported = new GotoAdapter();
        await unsupported.Launch();
        unsupported.StopAt();
        Assert.False(unsupported.Session.CanComplete);
        Assert.Null(await unsupported.Session.Completions("box.", 4, 1));
        unsupported.Session.StepOver();
        Assert.Equal("next", unsupported.Read()["command"]!.GetValue<string>());
    }
}
