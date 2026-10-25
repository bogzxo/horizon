using System.Text;

namespace Horizon.Core.Processes;

/// <summary>
/// A process with a terminal attached, a shell or anything that wants one. On Windows it is a ConPTY (Windows 10
/// 1809 up), elsewhere a pty out of posix_openpt, and either way it is a stream of bytes out (<see cref="Output"/>,
/// raised on a reading thread of its own as the bytes come, a terminal's parser takes them from there), a stream of
/// bytes in (<see cref="Write"/>, what the keys type), a size in cells (<see cref="Resize"/>) and an end
/// (<see cref="Exited"/>). Nothing of the terminal protocol is in here, this is the pipe and the process and nothing
/// else, <c>Horizon.UI.Components.TerminalScreen</c> is what makes sense of the bytes.
/// </summary>
public abstract class PseudoTerminal : IDisposable
{
    // The reading thread starts inside Start, before whoever started it has had the chance to listen. What it reads
    // before then is kept and handed to the first listener, or a quick program's first line (a shell's prompt) was
    // gone, and an end that came first is said to whoever listens for it later. One lock keeps it all in order
    private readonly Lock listening = new();
    private readonly List<byte[]> unheard = [];
    private Action<byte[]>? output;
    private Action<int>? exitedListeners;

    /// <summary>
    /// Bytes the process wrote, as they came, on the reading thread. Whoever takes them queues them for their own
    /// thread. What came before the first listener is handed to it as it starts listening, in order.
    /// </summary>
    public event Action<byte[]>? Output
    {
        add
        {
            lock (listening)
            {
                output += value;
                foreach (byte[] early in unheard)
                    value?.Invoke(early);
                unheard.Clear();
            }
        }
        remove
        {
            lock (listening)
                output -= value;
        }
    }

    /// <summary>The process ended, with its exit code, on the reading thread. Said at once to a listener that comes after the end.</summary>
    public event Action<int>? Exited
    {
        add
        {
            lock (listening)
            {
                exitedListeners += value;
                if (exited)
                    value?.Invoke(ExitCode);
            }
        }
        remove
        {
            lock (listening)
                exitedListeners -= value;
        }
    }

    /// <summary>The command it was started with, for a title.</summary>
    public string Command { get; }

    /// <summary>How many columns and rows it was last told it has.</summary>
    public int Columns { get; private set; }

    public int Rows { get; private set; }

    /// <summary>Whether the process is still going.</summary>
    public bool IsRunning => !exited;

    /// <summary>
    /// Whether the shell has a program of its own running right now (micro, a TUI, a build), asked of the system and
    /// not of the screen, which only knows what the shell's marks said and when they last settled. False where it can't
    /// be told. Not cheap enough for every frame, it's for a key that means something else at a prompt.
    /// </summary>
    public virtual bool RunsAProgram => false;

    /// <summary>The exit code, once it has one.</summary>
    public int ExitCode { get; private set; } = -1;

    private volatile bool exited;

    /// <summary>Why the reading stopped, when it stopped on an error, for a test.</summary>
    public string? PumpEnded { get; private set; }
    private readonly Lock writing = new();

    protected PseudoTerminal(string command, int columns, int rows)
    {
        Command = command;
        Columns = columns;
        Rows = rows;
    }

    /// <summary>
    /// Starts a command in a folder with a terminal of so many cells. The command line is the program and its arguments
    /// as a shell would read them ("pwsh -NoLogo", "bash -l"), the environment is the editor's with what is given laid
    /// over it (TERM and COLORTERM are set on Unix when they aren't).
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">On a platform without a pty of either kind.</exception>
    /// <exception cref="InvalidOperationException">When the process couldn't be started, with why.</exception>
    public static PseudoTerminal Start(string commandLine, string workingDirectory, int columns, int rows, IReadOnlyDictionary<string, string>? environment = null)
    {
        columns = Math.Clamp(columns, 2, 1000);
        rows = Math.Clamp(rows, 1, 500);

        if (OperatingSystem.IsWindows())
            return new WindowsPseudoTerminal(commandLine, workingDirectory, columns, rows, environment);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            return new UnixPseudoTerminal(commandLine, workingDirectory, columns, rows, environment);

        throw new PlatformNotSupportedException("There is no pseudo terminal on this platform.");
    }

    /// <summary>
    /// A console Windows handed over to the terminal, for a terminal that is the default one (ITerminalHandoff3). The
    /// console's input that is written to, its output that is read, the signal pipe its size goes down, the handle that
    /// keeps the console alive, and the console host's and the program's processes, all of them the terminal's to close.
    /// The command is what it's called, for a title, it was started by whoever started the program.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static PseudoTerminal HandedOver(Microsoft.Win32.SafeHandles.SafeFileHandle input, Microsoft.Win32.SafeHandles.SafeFileHandle output,
        Microsoft.Win32.SafeHandles.SafeFileHandle signal, Microsoft.Win32.SafeHandles.SafeFileHandle reference,
        Microsoft.Win32.SafeHandles.SafeFileHandle server, Microsoft.Win32.SafeHandles.SafeFileHandle client, string command) =>
        new HandedOverPseudoTerminal(input, output, signal, reference, server, client, command);

    /// <summary>The shell this machine comes with, PowerShell (pwsh if it is there), bash, zsh, whatever $SHELL says.</summary>
    public static string DefaultShell()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (File.Exists(Path.Combine(folder.Trim('"'), "pwsh.exe")))
                    return "pwsh.exe -NoLogo";
            }

            return "powershell.exe -NoLogo";
        }

        return Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } shell ? shell : "/bin/sh";
    }

    /// <summary>Writes bytes to the process's input, what a key or a paste types. Any thread.</summary>
    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (exited || bytes.IsEmpty)
            return;

        lock (writing)
            WriteBytes(bytes);
    }

    /// <summary>Writes text to the process's input as UTF-8.</summary>
    public void Write(string text)
    {
        if (text.Length == 0)
            return;

        Span<byte> buffer = text.Length <= 256 ? stackalloc byte[text.Length * 3] : new byte[Encoding.UTF8.GetMaxByteCount(text.Length)];
        int count = Encoding.UTF8.GetBytes(text, buffer);
        Write(buffer[..count]);
    }

    /// <summary>Tells the process its terminal is now so many cells, which a shell hears as a window change.</summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 2, 1000);
        rows = Math.Clamp(rows, 1, 500);
        if (columns == Columns && rows == Rows)
            return;

        Columns = columns;
        Rows = rows;
        if (!exited)
            ResizeTerminal(columns, rows);
    }

    protected abstract void WriteBytes(ReadOnlySpan<byte> bytes);

    protected abstract void ResizeTerminal(int columns, int rows);

    /// <summary>Helper for the platform's reading thread, bytes to whoever listens.</summary>
    protected void Emit(byte[] bytes)
    {
        lock (listening)
        {
            if (output is null)
                unheard.Add(bytes);
            else
                output(bytes);
        }
    }

    /// <summary>Helper for the platform's side, the process is gone.</summary>
    protected void EndedWith(int code)
    {
        lock (listening)
        {
            if (exited)
                return;

            ExitCode = code;
            exited = true;
            exitedListeners?.Invoke(code);
        }
    }

    /// <summary>Helper method to read a stream to its end, handing every piece on, on the thread it is called on.</summary>
    protected void Pump(Stream stream)
    {
        var buffer = new byte[16384];
        try
        {
            while (true)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    break;

                Emit(buffer.AsSpan(0, read).ToArray());
            }
        }
        catch (Exception e)
        {
            // The pipe went with the process, which is the end of it
            PumpEnded = e.Message;
        }
    }

    /// <summary>Ends the process if it is still going and lets the pipes go.</summary>
    public abstract void Dispose();
}
