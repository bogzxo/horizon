using System.Text;

using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>
/// Finding the shells a machine has, told what is on it rather than looking.
/// </summary>
public class TerminalProfilesTests
{
    private static Func<string, string?> Environment(params (string Name, string Value)[] variables) =>
        name => variables.FirstOrDefault(variable => variable.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    [Fact]
    public void Windows_finds_pwsh_git_bash_and_the_wsl_distributions_when_they_are_there()
    {
        // Windows' paths put together the way Windows does, which only Windows' Path does
        if (!OperatingSystem.IsWindows())
            return;

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Windows\System32\wsl.exe"
        };
        var found = TerminalProfiles.DiscoverWindows(files.Contains,
            Environment(("ProgramFiles", @"C:\Program Files"), ("SystemRoot", @"C:\Windows"), ("PATH", @"C:\Windows")),
            () => "Ubuntu\r\nDebian\r\n");

        Assert.Equal(["pwsh", "powershell", "cmd", "git_bash", "wsl:Ubuntu", "wsl:Debian"], found.Select(profile => profile.Id));
        Assert.Equal("wsl.exe -d Ubuntu", found[4].Command);
        Assert.Equal("terminal-linux", found[4].Icon);
        Assert.StartsWith("\"C:\\Program Files\\Git\\bin\\bash.exe\"", found[3].Command);
    }

    [Fact]
    public void Windows_without_any_of_them_is_the_two_that_always_are()
    {
        var found = TerminalProfiles.DiscoverWindows(_ => false, Environment(), () => null);

        Assert.Equal(["powershell", "cmd"], found.Select(profile => profile.Id));
        Assert.All(found, profile => Assert.Equal(TerminalProfileKind.Shell, profile.Kind));
    }

    [Fact]
    public void The_wsl_list_is_read_whether_it_came_as_utf16_or_not()
    {
        // wsl -l -q writes UTF-16 with a byte order mark down a pipe, which read a byte to a char is every other one a nul
        byte[] raw = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Ubuntu-22.04\r\ndocker-desktop\r\n\r\nArch\r\n")).ToArray();
        string asRead = Encoding.Latin1.GetString(raw);

        Assert.Equal(["Ubuntu-22.04", "Arch"], TerminalProfiles.ParseWslList(asRead));
        Assert.Equal(["Ubuntu"], TerminalProfiles.ParseWslList("Ubuntu\n"));
        Assert.Empty(TerminalProfiles.ParseWslList(null));
        Assert.Empty(TerminalProfiles.ParseWslList(string.Empty));
    }

    [Fact]
    public void Unix_lists_the_login_shell_first_and_the_rest_that_exist()
    {
        var files = new HashSet<string> { "/bin/zsh", "/usr/bin/bash", "/opt/homebrew/bin/fish" };
        var found = TerminalProfiles.DiscoverUnix(files.Contains, Environment(("SHELL", "/bin/zsh")));

        Assert.Equal(["zsh", "bash", "fish"], found.Select(profile => profile.Id));
        Assert.Equal("/usr/bin/bash", found[1].Command);
    }

    [Fact]
    public void A_profile_gets_an_id_out_of_its_name_when_none_is_given()
    {
        var profile = new TerminalProfile("Git Bash", "bash.exe");
        Assert.Equal("git_bash", profile.Id);
        Assert.Equal("terminal", profile.Icon);
        Assert.Equal("Git Bash", profile.ToString());
    }
}
