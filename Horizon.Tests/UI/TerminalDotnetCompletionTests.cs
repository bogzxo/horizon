using Horizon.UI.Components;

namespace Horizon.Tests.UI;

public class TerminalDotnetCompletionTests
{
    [Theory]
    [InlineData("App.csproj --runtime win-x64")]
    [InlineData("App.csproj -r win-x64")]
    [InlineData("App.csproj --runtime=win-x64")]
    [InlineData("--runtime win-x64")]
    public async Task NativeFrameworksRespectRuntimeIdentifier(string arguments)
    {
        if (Environment.GetEnvironmentVariable("HEX_TEST_DOTNET_COMPLETE") != "1") return;
        string root = Path.Combine(Path.GetTempPath(), "hex-framework-runtime-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "App.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
                  <TargetFramework Condition="'$(RuntimeIdentifier)' != 'win-x64'">net9.0</TargetFramework>
                  <TargetFramework Condition="'$(RuntimeIdentifier)' == 'win-x64'">net11.0</TargetFramework>
                </PropertyGroup></Project>
                """);
            string? frameworks = await TerminalDotnetCompletion.Query("dotnet publish " + arguments + " --framework n", root);
            Assert.Contains("net11.0", frameworks);
            Assert.DoesNotContain("net9.0", frameworks);
            Assert.False(Directory.Exists(Path.Combine(root, "obj")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("My App.csproj")]
    [InlineData("App & Library.csproj")]
    [InlineData("App;Library.csproj")]
    [InlineData("App (Library).csproj")]
    public async Task NativeCompletionIsAsyncKeepsQuotedProjectAndUpdatesTheView(string project)
    {
        var called = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new TerminalDotnetCompletion((command, folder) =>
        {
            Assert.Equal(Path.GetTempPath(), folder);
            called.TrySetResult(command);
            return Task.FromResult<string?>("Newtonsoft.Json\nNewtonsoft.Json\n");
        });
        var help = new TerminalToolHelp();
        help.Teach("dotnet", [], TerminalHelpTopic.Empty);
        var tools = new TerminalToolCompletion(["dotnet"], help, dotnet: native);
        var shell = new TerminalShellCompletion(new TerminalShellLine("powershell", new TerminalCommandLookup("powershell", ["dotnet"], sure: true)), tools, toolsOnly: true);
        string line = $"dotnet add \"{project}\" package Newt";
        Assert.Empty(shell.Complete(line, Path.GetTempPath(), out _));
        Assert.Equal(line, await called.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (native.Version == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        var item = Assert.Single(shell.Complete(line, Path.GetTempPath(), out int start));
        Assert.Equal("Newtonsoft.Json", item.Text);
        Assert.Equal(line.LastIndexOf("Newt", StringComparison.Ordinal), start);
    }

    [Theory]
    [InlineData("echo first | dotnet build \"App & Library.csproj\" --framework ", "App & Library.csproj")]
    [InlineData("echo first; dotnet build 'App;Library.csproj' --framework ", "App;Library.csproj")]
    [InlineData("echo \"ignored | text\" && dotnet build \"App (Library).csproj\" --framework ", "App (Library).csproj")]
    public void CommandOperatorsOnlySplitOutsideQuotes(string line, string project)
        => Assert.Equal(["dotnet", "build", project, "--framework"], TerminalShellCompletion.CommandWords(line, line.Length));

    [Theory]
    [InlineData("--project \"app folder\"")]
    [InlineData("--project=\"app folder\"")]
    public async Task NativeFrameworkCompletionResolvesProjectDirectories(string project)
    {
        if (Environment.GetEnvironmentVariable("HEX_TEST_DOTNET_COMPLETE") != "1") return;
        string root = Path.Combine(Path.GetTempPath(), "hex framework directory " + Guid.NewGuid());
        string folder = Path.Combine(root, "app folder");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "App.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
                  <TargetFramework Condition="'$(Configuration)' != 'Release'">net9.0</TargetFramework>
                  <TargetFramework Condition="'$(Configuration)' == 'Release'">net11.0</TargetFramework>
                </PropertyGroup></Project>
                """);
            string? frameworks = await TerminalDotnetCompletion.Query("dotnet run " + project + " -c Release --framework n", root);
            Assert.Contains("net11.0", frameworks);
            Assert.DoesNotContain("net9.0", frameworks);
            Assert.False(Directory.Exists(Path.Combine(folder, "obj")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task EmptyArgumentAfterSpaceOffersSdkValuesAndTabOnlyInsertsText()
    {
        var native = new TerminalDotnetCompletion((command, _) => Task.FromResult<string?>(command == "dotnet build --configuration " ? "Debug\nRelease\n" : ""));
        var help = new TerminalToolHelp();
        help.Teach("dotnet", [], TerminalHelpTopic.Empty);
        var tools = new TerminalToolCompletion(["dotnet"], help, dotnet: native);
        var shell = new TerminalShellCompletion(new TerminalShellLine("powershell", new TerminalCommandLookup("powershell", ["dotnet"], sure: true)), tools, toolsOnly: true);
        var view = new TerminalView { Completer = shell };
        var typed = new List<string>();
        view.OnInput = typed.Add;
        view.Feed("\u001b]133;A\u0007PS> \u001b]133;B\u0007dotnet build --configuration");
        view.Type(" ");
        view.Feed(" ");
        view.ColourLine();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (native.Version == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        view.ColourLine();
        Assert.Contains(view.Completions, item => item.Text == "Debug");
        Assert.True(view.AcceptCompletion());
        Assert.Equal("Debug", typed[^1]);
        Assert.DoesNotContain(typed, text => text.Contains('\r') || text.Contains('\n'));
    }

    [Theory]
    [InlineData("App.csproj --configuration Release")]
    [InlineData("App.csproj -c Release")]
    [InlineData("App.csproj --configuration=Release")]
    [InlineData("--configuration Release")]
    public async Task NativeFrameworksRespectBuildConfiguration(string arguments)
    {
        if (Environment.GetEnvironmentVariable("HEX_TEST_DOTNET_COMPLETE") != "1") return;
        string root = Path.Combine(Path.GetTempPath(), "hex-framework-configuration-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "App.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework Condition="'$(Configuration)' != 'Release'">net9.0</TargetFramework>
                    <TargetFramework Condition="'$(Configuration)' == 'Release'">net11.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            string? frameworks = await TerminalDotnetCompletion.Query("dotnet build " + arguments + " --framework n", root);
            Assert.Contains("net11.0", frameworks);
            Assert.DoesNotContain("net9.0", frameworks);
            Assert.False(Directory.Exists(Path.Combine(root, "obj")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RealSdkCompletesCommandsOptionsAndNugetPackages()
    {
        if (Environment.GetEnvironmentVariable("HEX_TEST_DOTNET_COMPLETE") != "1") return;
        Assert.Contains("build", await TerminalDotnetCompletion.Query("dotnet bu", Path.GetTempPath()));
        Assert.Contains("--configuration", await TerminalDotnetCompletion.Query("dotnet build --conf", Path.GetTempPath()));
        Assert.Contains("Debug", await TerminalDotnetCompletion.Query("dotnet build --configuration ", Path.GetTempPath()));
        Assert.Contains("newtonsoft.json", (await TerminalDotnetCompletion.Query("dotnet package add Newt", Path.GetTempPath()))?.ToLowerInvariant());
        Assert.Contains("newtonsoft.json", (await TerminalDotnetCompletion.Query("dotnet add package Newt", Path.GetTempPath()))?.ToLowerInvariant());
        string? versions = await TerminalDotnetCompletion.Query("dotnet package add Newtonsoft.Json --version 13.0", Path.GetTempPath());
        Assert.False(string.IsNullOrWhiteSpace(versions));
        // Available versions depend on the user's configured feeds and SDK cache.
        Assert.All(versions!.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), value =>
        {
            Assert.True(Version.TryParse(value, out var version), $"Expected a package version, got '{value}'.");
            Assert.Equal(13, version!.Major);
            Assert.Equal(0, version.Minor);
        });
        string root = Path.Combine(Path.GetTempPath(), "hex-dotnet-complete-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup></Project>");
            string? frameworks = await TerminalDotnetCompletion.Query("dotnet build --framework n", root);
            Assert.Contains("net8.0", frameworks);
            Assert.Contains("net9.0", frameworks);
            File.WriteAllText(Path.Combine(root, "Library & Tools.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"Framework.props\" /></Project>");
            File.WriteAllText(Path.Combine(root, "Framework.props"), "<Project><PropertyGroup><TargetFrameworks>net10.0;net11.0</TargetFrameworks></PropertyGroup></Project>");
            string? selected = await TerminalDotnetCompletion.Query("dotnet build \"Library & Tools.csproj\" --framework n", root);
            Assert.Contains("net10.0", selected);
            Assert.Contains("net11.0", selected);
            Assert.DoesNotContain("net8.0", selected);
            Assert.False(Directory.Exists(Path.Combine(root, "obj")));
        }
        finally { Directory.Delete(root, true); }
    }
}
