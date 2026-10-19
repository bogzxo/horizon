using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

namespace Horizon.Core.Processes;

/// <summary>
/// A pty on Linux and macOS. The master out of posix_openpt, the child started with posix_spawn in a session of its
/// own with the slave as its stdin, stdout and stderr (opened by the child itself through the file actions, which is
/// what makes it the controlling terminal without a fork of our own, a fork in a .NET process is nothing to do by
/// hand). Written from the manuals, like the Mac window was, and not run on either yet, the first run is the test.
/// The constants differ between the two and are picked at run time.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class UnixPseudoTerminal : PseudoTerminal
{
    private readonly int master;
    private readonly int pid;
    private readonly FileStream stream;

    public UnixPseudoTerminal(string commandLine, string workingDirectory, int columns, int rows, IReadOnlyDictionary<string, string>? environment)
        : base(commandLine, columns, rows)
    {
        bool mac = OperatingSystem.IsMacOS();
        int O_RDWR = 2, O_NOCTTY = mac ? 0x20000 : 0x100;

        master = posix_openpt(O_RDWR | O_NOCTTY);
        if (master < 0 || grantpt(master) != 0 || unlockpt(master) != 0)
            throw new InvalidOperationException($"No pty could be opened, errno {Marshal.GetLastPInvokeError()}.");

        IntPtr namePointer = ptsname(master);
        string? slave = namePointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(namePointer);
        if (slave is null)
            throw new InvalidOperationException("The pty has no name.");

        SetSize(columns, rows);

        // The child, in a session of its own, the slave opened as its 0, 1 and 2
        string[] argv = SplitCommandLine(commandLine);
        string[] envp = EnvironmentOf(environment);

        IntPtr actions = Marshal.AllocHGlobal(256), attributes = Marshal.AllocHGlobal(256);
        try
        {
            posix_spawn_file_actions_init(actions);
            posix_spawn_file_actions_addopen(actions, 0, slave, O_RDWR, 0);
            posix_spawn_file_actions_adddup2(actions, 0, 1);
            posix_spawn_file_actions_adddup2(actions, 0, 2);
            if (Directory.Exists(workingDirectory))
                posix_spawn_file_actions_addchdir_np(actions, workingDirectory);

            posix_spawnattr_init(attributes);
            short POSIX_SPAWN_SETSID = (short)(mac ? 0x0400 : 0x80);
            posix_spawnattr_setflags(attributes, POSIX_SPAWN_SETSID);

            IntPtr argvBlock = StringArray(argv), envpBlock = StringArray(envp);
            try
            {
                int spawned = posix_spawnp(out pid, argv[0], actions, attributes, argvBlock, envpBlock);
                if (spawned != 0)
                    throw new InvalidOperationException($"'{commandLine}' couldn't be started, errno {spawned}.");
            }
            finally
            {
                FreeStringArray(argvBlock, argv.Length);
                FreeStringArray(envpBlock, envp.Length);
            }
        }
        finally
        {
            posix_spawn_file_actions_destroy(actions);
            posix_spawnattr_destroy(attributes);
            Marshal.FreeHGlobal(actions);
            Marshal.FreeHGlobal(attributes);
        }

        stream = new FileStream(new SafeFileHandle((IntPtr)master, ownsHandle: true), FileAccess.ReadWrite, 1, false);

        var reader = new Thread(() =>
        {
            Pump(stream);
            int status = 0;
            waitpid(pid, ref status, 0);
            EndedWith((status >> 8) & 0xFF);
        }) { IsBackground = true, Name = "pty reader" };
        reader.Start();
    }

    private void SetSize(int columns, int rows)
    {
        var size = new WinSize { Rows = (ushort)rows, Columns = (ushort)columns };
        ulong TIOCSWINSZ = OperatingSystem.IsMacOS() ? 0x80087467UL : 0x5414UL;
        ioctl(master, TIOCSWINSZ, ref size);
    }

    protected override void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        try
        {
            stream.Write(bytes);
            stream.Flush();
        }
        catch (Exception)
        {
        }
    }

    protected override void ResizeTerminal(int columns, int rows) => SetSize(columns, rows);

    public override void Dispose()
    {
        if (IsRunning)
            kill(pid, 9);
        try { stream.Dispose(); } catch (Exception) { }
    }

    // The command line cut at spaces, with quotes kept together, the shell's own quoting rules are for the shell
    private static string[] SplitCommandLine(string commandLine)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in commandLine)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ' ' && !quoted)
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts.Count == 0 ? ["/bin/sh"] : [.. parts];
    }

    private static string[] EnvironmentOf(IReadOnlyDictionary<string, string>? environment)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            all[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        all.TryAdd("TERM", "xterm-256color");
        all.TryAdd("COLORTERM", "truecolor");
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                all[key] = value;
        }

        return [.. all.Select(pair => $"{pair.Key}={pair.Value}")];
    }

    private static IntPtr StringArray(string[] strings)
    {
        IntPtr block = Marshal.AllocHGlobal(IntPtr.Size * (strings.Length + 1));
        for (int i = 0; i < strings.Length; i++)
            Marshal.WriteIntPtr(block, i * IntPtr.Size, Marshal.StringToCoTaskMemUTF8(strings[i]));
        Marshal.WriteIntPtr(block, strings.Length * IntPtr.Size, IntPtr.Zero);
        return block;
    }

    private static void FreeStringArray(IntPtr block, int count)
    {
        for (int i = 0; i < count; i++)
            Marshal.FreeCoTaskMem(Marshal.ReadIntPtr(block, i * IntPtr.Size));
        Marshal.FreeHGlobal(block);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Rows, Columns, XPixels, YPixels;
    }

    [DllImport("libc", SetLastError = true)] private static extern int posix_openpt(int flags);
    [DllImport("libc", SetLastError = true)] private static extern int grantpt(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int unlockpt(int fd);
    [DllImport("libc", SetLastError = true)] private static extern IntPtr ptsname(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, ulong request, ref WinSize size);
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);
    [DllImport("libc", SetLastError = true)] private static extern int waitpid(int pid, ref int status, int options);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_init(IntPtr actions);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_destroy(IntPtr actions);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_addopen(IntPtr actions, int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_adddup2(IntPtr actions, int fd, int newFd);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_addchdir_np(IntPtr actions, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport("libc")] private static extern int posix_spawnattr_init(IntPtr attributes);
    [DllImport("libc")] private static extern int posix_spawnattr_destroy(IntPtr attributes);
    [DllImport("libc")] private static extern int posix_spawnattr_setflags(IntPtr attributes, short flags);
    [DllImport("libc")] private static extern int posix_spawnp(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string file, IntPtr actions, IntPtr attributes, IntPtr argv, IntPtr envp);
}
