using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

using Microsoft.Win32.SafeHandles;

namespace Horizon.Core.Processes;

/// <summary>
/// A ConPTY. Two pipes, a pseudo console made over them (CreatePseudoConsole), and the process started with the
/// console in its attribute list, which is how Windows Terminal and VS Code do it. The console translates whatever the
/// process does to its screen into VT sequences for us and our bytes into console input for it, so a cmd that knows
/// nothing of escapes still comes out as a stream of them.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsPseudoTerminal : PseudoTerminal
{
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

    private readonly IntPtr console;
    private readonly SafeFileHandle inputWrite = null!, outputRead = null!;
    private readonly FileStream input = null!, output = null!;
    private readonly Process process = null!;
    private IntPtr attributes;
    private bool disposed;

    public WindowsPseudoTerminal(string commandLine, string workingDirectory, int columns, int rows, IReadOnlyDictionary<string, string>? environment)
        : base(commandLine, columns, rows)
    {
        SafeFileHandle? inputRead = null;
        SafeFileHandle? outputWrite = null;
        bool attributeListInitialized = false;
        bool initialized = false;

        try
        {
            // The console reads what we write to inputWrite and writes what we read from outputRead
            if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) || !CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
                throw new InvalidOperationException($"The pipes for the console couldn't be made, {new Win32Exception().Message}");

            int made = CreatePseudoConsole(new COORD((short)columns, (short)rows), inputRead, outputWrite, 0, out console);
            if (made != 0)
                throw new InvalidOperationException($"CreatePseudoConsole failed with {made}, Windows 10 1809 or newer is wanted.");

            // The console has its own handles on those two ends now
            inputRead.Dispose();
            inputRead = null;
            outputWrite.Dispose();
            outputWrite = null;

            // The process, told about the console through its attribute list
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw new InvalidOperationException($"The console couldn't be given to the process, {new Win32Exception().Message}");
            attributeListInitialized = true;

            if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException($"The console couldn't be given to the process, {new Win32Exception().Message}");

            // The std handles are given as none on purpose. Without the flag a child copies its parent's std handles by value
            // (a pipe to whoever started the editor, or nothing at all) and writes there instead of to the console, which is
            // how cmd's prompt went to the test runner and pwsh ended at once in a window. Given none, the console fills them
            // in with its own as the child starts, the way a console app started from a console gets them
            var startup = new STARTUPINFOEX { StartupInfo = { cb = Marshal.SizeOf<STARTUPINFOEX>(), dwFlags = STARTF_USESTDHANDLES }, lpAttributeList = attributes };
            var variables = Variables(environment);
            string? block = EnvironmentBlock(variables);
            IntPtr environmentBlock = block is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(block);

            // Found on the PATH the program gets, not this process's, which Windows would look on
            commandLine = ProcessEnvironment.Resolve(commandLine, variables.TryGetValue("Path", out string? path) ? path : string.Empty);
            char[] commandBuffer = new char[commandLine.Length + 1];
            commandLine.CopyTo(0, commandBuffer, 0, commandLine.Length);
            try
            {
                PROCESS_INFORMATION information;
                unsafe
                {
                    fixed (char* cmd = commandBuffer)
                    {
                        if (!CreateProcessW(null, (IntPtr)cmd, IntPtr.Zero, IntPtr.Zero, false, EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                            environmentBlock, Directory.Exists(workingDirectory) ? workingDirectory : null, ref startup, out information))
                        {
                            throw new InvalidOperationException($"'{commandLine}' couldn't be started, {new Win32Exception().Message}");
                        }
                    }
                }

                CloseHandle(information.hThread);
                CloseHandle(information.hProcess);
                process = Process.GetProcessById(information.dwProcessId);
            }
            finally
            {
                if (environmentBlock != IntPtr.Zero)
                    Marshal.FreeHGlobal(environmentBlock);
            }

            input = new FileStream(inputWrite, FileAccess.Write, 1, false);
            output = new FileStream(outputRead, FileAccess.Read, 1, false);

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => ProcessEnded();

            var reader = new Thread(() => Pump(output)) { IsBackground = true, Name = "conpty reader" };
            reader.Start();
            initialized = true;
        }
        finally
        {
            inputRead?.Dispose();
            outputWrite?.Dispose();

            if (!initialized)
            {
                CloseConsole();
                if (attributes != IntPtr.Zero)
                {
                    if (attributeListInitialized)
                        DeleteProcThreadAttributeList(attributes);
                    Marshal.FreeHGlobal(attributes);
                    attributes = IntPtr.Zero;
                }

                if (input is not null)
                    try { input.Dispose(); } catch (Exception) { }
                else
                    inputWrite?.Dispose();

                if (output is not null)
                    try { output.Dispose(); } catch (Exception) { }
                else
                    outputRead?.Dispose();

                process?.Dispose();
            }
        }
    }

    // How long conhost is given to write out the last of what the process wrote before the console is closed. ConPTY
    // renders in frames, and a process that prints a line and exits is gone before its frame is out, close on the spot
    // and the line is lost (measured, cmd /c echo lost its echo every time)
    private const int DRAIN_MILLISECONDS = 200;

    // The process went, the console is closed a moment later so the reader sees the end of the pipe once the last
    // of the output is through it, and whoever listens is told after that, so everything written is in by then
    private void ProcessEnded()
    {
        Task.Delay(DRAIN_MILLISECONDS).ContinueWith(_ => ConsoleDrained(), TaskScheduler.Default);
    }

    private void ConsoleDrained()
    {
        int code;
        try
        {
            code = process.ExitCode;
        }
        catch (Exception)
        {
            code = -1;
        }

        CloseConsole();
        EndedWith(code);
    }

    private void CloseConsole()
    {
        lock (this)
        {
            if (console != IntPtr.Zero && !disposed)
            {
                disposed = true;
                ClosePseudoConsole(console);
            }
        }
    }

    // The variables a program started now should have (ProcessEnvironment, the PATH as the system has it now and not as it
    // was when this process started) and then what was asked, as the block Windows wants, name=value\0 ... \0\0. Always
    // one of its own, a shell given nothing went on with the PATH Dusk was started with and couldn't find what was installed
    private static SortedDictionary<string, string> Variables(IReadOnlyDictionary<string, string>? environment)
    {
        var all = new SortedDictionary<string, string>(ProcessEnvironment.Current(), StringComparer.OrdinalIgnoreCase);
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                all[key] = value;
        }

        return all;
    }

    private static string? EnvironmentBlock(SortedDictionary<string, string> all)
    {
        var block = new StringBuilder();
        foreach (var (key, value) in all)
            block.Append(key).Append('=').Append(value).Append('\0');
        return block.Append('\0').ToString();
    }

    protected override void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        try
        {
            input.Write(bytes);
            input.Flush();
        }
        catch (Exception)
        {
            // Gone, the exit says so shortly
        }
    }

    protected override void ResizeTerminal(int columns, int rows) => ResizePseudoConsole(console, new COORD((short)columns, (short)rows));

    public override void Dispose()
    {
        if (IsRunning)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
        }

        CloseConsole();
        if (attributes != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            attributes = IntPtr.Zero;
        }

        try { input.Dispose(); } catch (Exception) { }
        try { output.Dispose(); } catch (Exception) { }
        process.Dispose();
    }

    /* Windows */

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD(short x, short y)
    {
        public short X = x, Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    // The processes the shell started, out of a snapshot of every process there is, the console hosts left out (one
    // stands in for every console app ConPTY runs and isn't a program anybody ran)
    public override bool RunsAProgram
    {
        get
        {
            if (!IsRunning)
                return false;

            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
                return false;

            try
            {
                unsafe
                {
                    var entry = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
                    for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                    {
                        if (entry.th32ParentProcessID != (uint)process.Id || entry.th32ProcessID == (uint)process.Id)
                            continue;

                        ReadOnlySpan<char> exe = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(entry.szExeFile);
                        if (!exe.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase)
                            && !exe.Equals("OpenConsole.exe", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }

                return false;
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }
    }

    private const uint TH32CS_SNAPPROCESS = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int ResizePseudoConsole(IntPtr hPC, COORD size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void ClosePseudoConsole(IntPtr hPC);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(string? lpApplicationName, IntPtr lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);
}
