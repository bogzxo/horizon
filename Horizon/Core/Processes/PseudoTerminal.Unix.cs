using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

namespace Horizon.Core.Processes;

/// <summary>
/// A pty on Linux and macOS. The master out of posix_openpt, the child started with posix_spawn in a session of its
/// own with the slave as its stdin, stdout and stderr (opened by the child itself through the file actions, which is
/// what makes it the controlling terminal without a fork of our own, a fork in a .NET process is nothing to do by
/// hand). The constants differ between the two and are picked at run time. Run on Linux (Ubuntu, glibc), where the
/// first run found glibc's posix_spawnattr_t is 336 bytes and not the 256 it was given, which corrupted the heap.
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

        // The slave held open here until the child has it, with the size set on it. A Mac forgets a size given to the
        // master before the slave was first opened, and the child saw 0 rows and 0 columns
        int held = open(slave, O_RDWR | O_NOCTTY);
        SetSize(columns, rows);

        // The child, in a session of its own, the slave opened as its 0, 1 and 2
        string[] argv = SplitCommandLine(commandLine);
        string[] envp = EnvironmentOf(environment);

        // What these are is the C library's business and their size with it: glibc's file actions are 80 bytes and its
        // attributes 336, a Mac's a pointer each. Plenty either way, zeroed
        IntPtr actions = Marshal.AllocHGlobal(SPAWN_STRUCT), attributes = Marshal.AllocHGlobal(SPAWN_STRUCT);
        unsafe
        {
            new Span<byte>((void*)actions, SPAWN_STRUCT).Clear();
            new Span<byte>((void*)attributes, SPAWN_STRUCT).Clear();
        }

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

            // Let go of, or the master would never read the end of the child
            if (held >= 0)
                close(held);
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

    // Room enough for a posix_spawn_file_actions_t or a posix_spawnattr_t of any C library
    private const int SPAWN_STRUCT = 1024;

    private void SetSize(int columns, int rows)
    {
        var size = new WinSize { Rows = (ushort)rows, Columns = (ushort)columns };
        if (OperatingSystem.IsMacOS())
        {
            const ulong TIOCSWINSZ = 0x80087467UL;

            // ioctl takes what comes after the request as C's ..., which Apple's arm64 passes on the stack and not in a
            // register. Six more arguments fill the registers, so the size lands on the stack where ioctl looks
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                ioctl_arm64_apple(master, TIOCSWINSZ, 0, 0, 0, 0, 0, 0, ref size);
            else
                ioctl(master, TIOCSWINSZ, ref size);
            return;
        }

        ioctl(master, 0x5414UL, ref size);
    }

    /// <summary>
    /// Whether something other than the shell has the terminal (a program it started, in the foreground), which is
    /// whatever process group the terminal says is in front not being the shell's own.
    /// </summary>
    public override bool RunsAProgram => IsRunning && tcgetpgrp(master) is var front && front > 0 && front != pid;

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

    // The command line cut into words the way sh would: at spaces and tabs, with '...' kept as it is, "..." kept
    // together (a backslash in it escaping " \ $ and `), and a backslash outside quotes taking the next character as it is
    internal static string[] SplitCommandLine(string commandLine)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool started = false;
        char quote = '\0';
        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (quote == '\'')
            {
                if (c == '\'') quote = '\0';
                else current.Append(c);
                continue;
            }

            if (quote == '"')
            {
                if (c == '"') quote = '\0';
                else if (c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] is '"' or '\\' or '$' or '`') current.Append(commandLine[++i]);
                else current.Append(c);
                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    started = true;
                    break;
                case '\\' when i + 1 < commandLine.Length:
                    current.Append(commandLine[++i]);
                    started = true;
                    break;
                case ' ' or '\t':
                    if (started) { parts.Add(current.ToString()); current.Clear(); started = false; }
                    break;
                default:
                    current.Append(c);
                    started = true;
                    break;
            }
        }

        if (started)
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
    [DllImport("libc", SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, ulong request, ref WinSize size);
    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")] private static extern int ioctl_arm64_apple(int fd, ulong request, long r2, long r3, long r4, long r5, long r6, long r7, ref WinSize size);
    [DllImport("libc", SetLastError = true)] private static extern int tcgetpgrp(int fd);
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
