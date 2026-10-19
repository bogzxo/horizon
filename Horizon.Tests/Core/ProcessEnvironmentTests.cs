using Horizon.Core.Processes;

namespace Horizon.Tests;

// What a program started now gets for an environment (ProcessEnvironment). The PATH as the system has it now and not as
// it was when this process started, what this process had on top of that kept, each folder once.
public class ProcessEnvironmentTests
{
    [Fact]
    public void The_path_is_the_machines_then_the_users_then_what_else_this_process_had()
    {
        string path = ProcessEnvironment.PathFrom(own: @"C:\a;C:\dev\tools", machine: @"C:\a;C:\b\", user: @"C:\B;C:\new");

        Assert.Equal(@"C:\a;C:\b\;C:\new;C:\dev\tools", path);
    }

    [Fact]
    public void A_program_named_on_its_own_is_found_on_the_path_it_was_given()
    {
        string folder = Path.Combine(Path.GetTempPath(), "horizon-resolve-test");
        Directory.CreateDirectory(folder);
        string program = Path.Combine(folder, "newshell.exe");
        File.WriteAllText(program, string.Empty);

        string path = @"C:\nowhere;" + folder;
        Assert.Equal($"\"{program}\" -NoLogo -i", ProcessEnvironment.Resolve("newshell -NoLogo -i", path));
        Assert.Equal($"\"{program}\"", ProcessEnvironment.Resolve("\"newshell.exe\"", path));

        // A program with a folder in it, or one that isn't there, is left as it was
        Assert.Equal(@"C:\tools\x.exe -a", ProcessEnvironment.Resolve(@"C:\tools\x.exe -a", path));
        Assert.Equal("missing --flag", ProcessEnvironment.Resolve("missing --flag", path));
    }

    [Fact]
    public void A_path_this_process_was_started_with_doesnt_hide_the_systems()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string? was = Environment.GetEnvironmentVariable("PATH");
        try
        {
            // As good as a terminal started long before somebody changed their PATH
            Environment.SetEnvironmentVariable("PATH", @"C:\horizon-stale-path");

            string path = ProcessEnvironment.Path();
            string machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? string.Empty;
            string first = machine.Split(';', StringSplitOptions.RemoveEmptyEntries)[0];

            Assert.StartsWith(first, path);
            Assert.EndsWith(@"C:\horizon-stale-path", path);
            Assert.Equal(path, ProcessEnvironment.Current()["PATH"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", was);
        }
    }
}
