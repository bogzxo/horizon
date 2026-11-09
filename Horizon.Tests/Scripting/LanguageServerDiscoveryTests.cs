using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public sealed class LanguageServerDiscoveryTests
{
    [Fact]
    public void SearchOrderPreservesPathPrecedenceAndExplicitPaths()
    {
        string root = Path.Combine(Path.GetTempPath(), "hex-server-discovery-" + Guid.NewGuid());
        string first = Path.Combine(root, "custom"), fallback = Path.Combine(root, "LLVM", "bin");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(fallback);
        string name = OperatingSystem.IsWindows() ? "clangd.exe" : "clangd";
        string preferred = Path.Combine(first, name), installed = Path.Combine(fallback, name);
        File.WriteAllText(preferred, "");
        File.WriteAllText(installed, "");
        try
        {
            string search = first + Path.PathSeparator + fallback;
            Assert.Equal(preferred, LanguageServerProgram.LocateIn("clangd", search), ignoreCase: OperatingSystem.IsWindows());
            Assert.Equal(installed, LanguageServerProgram.LocateIn(installed, search));
            File.Delete(preferred);
            Assert.Equal(installed, LanguageServerProgram.LocateIn("clangd", search), ignoreCase: OperatingSystem.IsWindows());
            Assert.Null(LanguageServerProgram.LocateIn(Path.Combine(first, name), search));
        }
        finally { Directory.Delete(root, true); }
    }
}
