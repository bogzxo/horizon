using Horizon.UI.Components;

using static Horizon.UI.Components.TerminalDroppedPaths;

namespace Horizon.Tests.UI;

/// <summary>Files dropped on a terminal typed as its shell reads them, quoted only where they have to be.</summary>
public class TerminalDroppedPathsTests
{
    [Theory]
    [InlineData("pwsh.exe -NoLogo", Quoting.PowerShell)]
    [InlineData("C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe", Quoting.PowerShell)]
    [InlineData("C:\\Windows\\System32\\cmd.exe", Quoting.Cmd)]
    [InlineData("wsl.exe -d Ubuntu", Quoting.Wsl)]
    [InlineData("C:\\Program Files\\Git\\bin\\bash.exe --login -i", Quoting.Posix)]
    [InlineData("/bin/zsh -l", Quoting.Posix)]
    public void The_shell_is_known_by_the_command_its_profile_starts(string command, Quoting expected)
    {
        Assert.Equal(expected, QuotingOf(new TerminalProfile("shell", command)));
    }

    [Fact]
    public void A_path_with_nothing_special_in_it_is_typed_as_it_is()
    {
        Assert.Equal("C:\\code\\main.cs", Quoted("C:\\code\\main.cs", Quoting.PowerShell));
        Assert.Equal("C:\\code\\main.cs", Quoted("C:\\code\\main.cs", Quoting.Cmd));
        Assert.Equal("/home/me/main.cs", Quoted("/home/me/main.cs", Quoting.Posix));
    }

    [Fact]
    public void A_path_with_a_space_is_quoted_the_way_each_shell_quotes()
    {
        Assert.Equal("\"C:\\my code\\a.cs\"", Quoted("C:\\my code\\a.cs", Quoting.Cmd));
        Assert.Equal("'C:\\my code\\a.cs'", Quoted("C:\\my code\\a.cs", Quoting.PowerShell));
        Assert.Equal("'/home/me/my code'", Quoted("/home/me/my code", Quoting.Posix));
    }

    [Fact]
    public void A_quote_in_a_path_is_escaped_inside_the_quotes()
    {
        Assert.Equal("'C:\\it''s\\a.cs'", Quoted("C:\\it's\\a.cs", Quoting.PowerShell));
        Assert.Equal("'/tmp/it'\\''s'", Quoted("/tmp/it's", Quoting.Posix));
    }

    [Fact]
    public void Wsl_is_handed_the_mnt_path_of_a_windows_one()
    {
        Assert.Equal("/mnt/c/Users/me/a.cs", Quoted("C:\\Users\\me\\a.cs", Quoting.Wsl));
        Assert.Equal("'/mnt/d/my files/a.cs'", Quoted("D:\\my files\\a.cs", Quoting.Wsl));
    }

    [Fact]
    public void Several_paths_are_typed_with_a_space_after_each()
    {
        var profile = new TerminalProfile("PowerShell", "pwsh.exe");
        Assert.Equal("C:\\a.cs 'C:\\b c.cs' ", Typed(["C:\\a.cs", "C:\\b c.cs", ""], profile));
    }
}
