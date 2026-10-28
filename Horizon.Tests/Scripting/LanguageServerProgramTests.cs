using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public class LanguageServerProgramTests
{
    [Fact]
    public void Windows_npm_shims_use_node_and_preserve_literal_arguments()
    {
        if (!OperatingSystem.IsWindows()) return;
        string folder = Path.Combine(Path.GetTempPath(), "lsp npm & literal % text " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "node_modules", "example"));
        try
        {
            string shim = Path.Combine(folder, "example.cmd"), node = Path.Combine(folder, "node.exe");
            string script = Path.Combine(folder, "node_modules", "example", "server.js");
            File.WriteAllText(Path.Combine(folder, "example"), "a POSIX shell shim must not win on Windows");
            File.WriteAllText(shim, "@echo off\r\n\"%_prog%\" \"%dp0%\\node_modules\\example\\server.js\" %*\r\n");
            File.WriteAllText(node, "");
            File.WriteAllText(script, "");
            Assert.Equal(shim, LanguageServerProgram.LocateIn("example", folder), ignoreCase: true);
            var info = LanguageServerProgram.StartInfo(shim, ["--stdio", "a & b", "100%", "a\"b", ""]);
            Assert.Equal(node, info.FileName);
            Assert.Equal(new[] { script, "--stdio", "a & b", "100%", "a\"b", "" }, info.ArgumentList);
            Assert.True(info.RedirectStandardInput && info.RedirectStandardOutput && info.RedirectStandardError);
            Assert.False(info.UseShellExecute);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Native_programs_keep_their_arguments_without_shell_interpretation()
    {
        var info = LanguageServerProgram.StartInfo("missing-test-server", ["--stdio", "a & b"]);
        Assert.Equal("missing-test-server", info.FileName);
        Assert.Equal(new[] { "--stdio", "a & b" }, info.ArgumentList);
        Assert.Null(LanguageServerProgram.Locate("missing-test-server"));
    }
}
