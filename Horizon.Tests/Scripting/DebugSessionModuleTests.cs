using System.Text.Json.Nodes;
using Horizon.UI.Scripting;

namespace Horizon.Tests;

public partial class DebugSessionTests
{
    private static JsonObject Module(string id, string name, string symbols = "Symbols loaded.") => new()
    { ["id"] = id, ["name"] = name, ["path"] = "/modules/" + name, ["symbolStatus"] = symbols };
    private static void ModuleEvent(GotoAdapter adapter, string reason, JsonObject module) => adapter.Event("module", new JsonObject { ["reason"] = reason, ["module"] = module });
    private static JsonObject ModuleList(params JsonObject[] modules) => new() { ["modules"] = new JsonArray(modules.Select(module => (JsonNode?)module).ToArray()) };

    [Fact]
    public async Task MalformedModuleEventsCannotBreakTheDebugAdapterConnection()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch();
        adapter.Event("module", JsonValue.Create(7));
        adapter.Event("module", new JsonObject { ["module"] = "not a module", ["reason"] = "new" });
        ModuleEvent(adapter, "new", new JsonObject { ["id"] = true, ["name"] = "invalid" });
        ModuleEvent(adapter, "new", Module("a", "App.dll"));
        Assert.True(WaitFor(() => adapter.Session.LoadedModuleCount == 1));
        Assert.Equal(DebugState.Running, adapter.Session.State);
        var threads = adapter.Session.Threads();
        adapter.Answer(adapter.Read(), new JsonObject { ["threads"] = new JsonArray() });
        Assert.Empty(await threads.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ModuleEventsWorkWithoutQueryCapabilityAndPreserveUnknownNativeFields()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch();
        var module = Module("1", "App.dll");
        module["isOptimized"] = false;
        module["isUserCode"] = true;
        module["version"] = "1.2.3";
        module["symbolFilePath"] = "/symbols/App.pdb";
        module["addressRange"] = "0x1234";
        module["dateTimeStamp"] = "2026-11-09T00:00:00Z";
        ModuleEvent(adapter, "new", module);
        ModuleEvent(adapter, "new", new JsonObject { ["id"] = 1, ["name"] = "Native.dll" });
        Assert.True(WaitFor(() => adapter.Session.LoadedModuleCount == 2));
        Assert.False(adapter.Session.CanRequestModules);
        Assert.False(await adapter.Session.RefreshModules());
        var app = adapter.Session.LoadedModules.Single(module => !module.Id.Numeric);
        Assert.False(app.Optimized);
        Assert.True(app.UserCode);
        Assert.Equal("1.2.3", app.Version);
        Assert.Equal("/symbols/App.pdb", app.SymbolFilePath);
        Assert.Equal("0x1234", app.AddressRange);
        Assert.Equal("2026-11-09T00:00:00Z", app.DateTimeStamp);
        var native = adapter.Session.LoadedModules.Single(module => module.Id.Numeric);
        Assert.Null(native.Optimized);
        Assert.Null(native.UserCode);
        Assert.Empty(native.SymbolStatus);
        ModuleEvent(adapter, "changed", new JsonObject { ["id"] = "1", ["symbolStatus"] = "Symbols not found." });
        Assert.True(WaitFor(() => adapter.Session.LoadedModules.Any(module => module.SymbolStatus == "Symbols not found.")));
        app = adapter.Session.LoadedModules.Single(module => !module.Id.Numeric);
        Assert.Equal("App.dll", app.Name);
        Assert.Equal("/modules/App.dll", app.Path);
        ModuleEvent(adapter, "removed", new JsonObject { ["id"] = 1 });
        Assert.True(WaitFor(() => adapter.Session.LoadedModuleCount == 1));
        Assert.False(Assert.Single(adapter.Session.LoadedModules).Id.Numeric);
    }

    [Fact]
    public async Task ModuleSnapshotReplaysConcurrentLoadsChangesAndUnloads()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(modules: true);
        ModuleEvent(adapter, "new", Module("a", "App.dll"));
        ModuleEvent(adapter, "new", Module("b", "Removed.dll"));
        Assert.True(WaitFor(() => adapter.Session.LoadedModuleCount == 2));
        var query = adapter.Session.RefreshModules();
        var request = adapter.Read();
        Assert.Equal("modules", request["command"]!.GetValue<string>());
        Assert.Equal(0, request["arguments"]!["startModule"]!.GetValue<int>());
        Assert.Equal(0, request["arguments"]!["moduleCount"]!.GetValue<int>());
        ModuleEvent(adapter, "changed", new JsonObject { ["id"] = "a", ["symbolStatus"] = "Loading symbols" });
        ModuleEvent(adapter, "removed", new JsonObject { ["id"] = "b" });
        ModuleEvent(adapter, "new", Module("c", "Late.dll"));
        adapter.Answer(request, ModuleList(Module("a", "App.dll"), Module("b", "Removed.dll"), Module("z", "QueryOnly.dll")));
        Assert.True(await query.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { "App.dll", "Late.dll", "QueryOnly.dll" }, adapter.Session.LoadedModules.Select(module => module.Name));
        Assert.Equal("Loading symbols", adapter.Session.LoadedModules[0].SymbolStatus);
        Assert.Equal("/modules/App.dll", adapter.Session.LoadedModules[0].Path);
    }

    [Fact]
    public async Task OnlyLatestModuleSnapshotCanReplaceTheList()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(modules: true);
        var older = adapter.Session.RefreshModules();
        var oldRequest = adapter.Read();
        var newer = adapter.Session.RefreshModules();
        adapter.Answer(adapter.Read(), ModuleList(Module("new", "New.dll")));
        Assert.True(await newer.WaitAsync(TimeSpan.FromSeconds(5)));
        long revision = adapter.Session.ModulesRevision;
        adapter.Answer(oldRequest, ModuleList(Module("old", "Old.dll")));
        Assert.False(await older.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("New.dll", Assert.Single(adapter.Session.LoadedModules).Name);
        Assert.Equal(revision, adapter.Session.ModulesRevision);
    }

    [Fact]
    public async Task EndClearsModulesAndRejectsLateSnapshotsAndEvents()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(modules: true);
        ModuleEvent(adapter, "new", Module("a", "App.dll"));
        Assert.True(WaitFor(() => adapter.Session.LoadedModuleCount == 1));
        var query = adapter.Session.RefreshModules();
        var request = adapter.Read();
        adapter.Event("terminated");
        Assert.True(WaitFor(() => adapter.Session.State == DebugState.Ended));
        ModuleEvent(adapter, "new", Module("late", "Late.dll"));
        adapter.Answer(request, ModuleList(Module("a", "App.dll")));
        Assert.False(await query.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(adapter.Session.LoadedModules);
        Assert.False(await adapter.Session.RefreshModules());
    }

    [Fact]
    public async Task MalformedAndCancelledSnapshotsPreserveTheLastValidList()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(modules: true);
        ModuleEvent(adapter, "new", Module("a", "App.dll"));
        Assert.True(WaitFor(() => adapter.Session.LoadedModuleCount == 1));
        var invalid = adapter.Session.RefreshModules();
        adapter.Answer(adapter.Read(), new JsonObject());
        await Assert.ThrowsAsync<InvalidDataException>(() => invalid);
        using var cancel = new CancellationTokenSource();
        var query = adapter.Session.RefreshModules(cancel.Token);
        var request = adapter.Read();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        adapter.Answer(request, ModuleList());
        Assert.Equal("App.dll", Assert.Single(adapter.Session.LoadedModules).Name);
    }

    [Fact]
    public async Task AnEmptyNativeSnapshotClearsPreviousModulesWithoutInventingMetadata()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(modules: true);
        var populated = adapter.Session.RefreshModules();
        adapter.Answer(adapter.Read(), ModuleList(Module("a", "App.dll")));
        Assert.True(await populated.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(adapter.Session.LoadedModules);
        var empty = adapter.Session.RefreshModules();
        adapter.Answer(adapter.Read(), ModuleList());
        Assert.True(await empty.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(adapter.Session.LoadedModules);
    }
}
