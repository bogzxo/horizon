using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

namespace Horizon.Core.Processes;

/// <summary>
/// A console Windows handed over rather than one we made. When a terminal is the default one, a console program started
/// anywhere (cmd from the Run box, a script double clicked) gets a console host that passes the console on to the
/// terminal (ITerminalHandoff3, Windows Terminal's OpenConsole), and what comes over is two pipes, the console's input
/// that we write to and its output that we read, a signal pipe the size goes down, a handle on the console that keeps it
/// alive while we hold it, and the console host's and the program's processes. The program was started by whoever
/// started it, so it's watched rather than run, and it's the program ending that ends it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class HandedOverPseudoTerminal : PseudoTerminal
{
    // What the console host takes down the signal pipe to change its size, three shorts, the signal and the size
    private const ushort PTY_SIGNAL_RESIZE_WINDOW = 8;

    private readonly FileStream input = null!, output = null!;
    private readonly SafeFileHandle signal, reference, server, client;
    private readonly RegisteredWaitHandle? watching;
    private readonly ManualResetEvent clientEnded = new(false);
    private int closed;

    public HandedOverPseudoTerminal(SafeFileHandle inputWrite, SafeFileHandle outputRead, SafeFileHandle signal, SafeFileHandle reference,
        SafeFileHandle server, SafeFileHandle client, string command)
        // No size yet, so the first one the terminal says goes down to the console however big it is
        : base(command, 0, 0)
    {
        this.signal = signal;
        this.reference = reference;
        this.server = server;
        this.client = client;

        bool initialized = false;
        try
        {
            input = new FileStream(inputWrite, FileAccess.Write, 1, false);
            output = new FileStream(outputRead, FileAccess.Read, 1, false);

            // The program ending is the end of it, the way a shell ending is for a console of our own
            clientEnded.SafeWaitHandle = new SafeWaitHandle(client.DangerousGetHandle(), ownsHandle: false);
            watching = ThreadPool.RegisterWaitForSingleObject(clientEnded, (_, _) => ProgramEnded(), null, Timeout.Infinite, executeOnlyOnce: true);

            var reader = new Thread(() =>
            {
                Pump(output);
                // The console host went without the program going first (it was closed from outside), which ends it too
                ProgramEnded();
            }) { IsBackground = true, Name = "handed over console reader" };
            reader.Start();
            initialized = true;
        }
        finally
        {
            if (!initialized)
            {
                watching?.Unregister(null);
                if (input is not null) try { input.Dispose(); } catch (Exception) { } else inputWrite.Dispose();
                if (output is not null) try { output.Dispose(); } catch (Exception) { } else outputRead.Dispose();
                signal.Dispose();
                reference.Dispose();
                server.Dispose();
                client.Dispose();
                clientEnded.Dispose();
            }
        }
    }

    // How long the console host is given to write out the last of what the program wrote, the same as a console of our own
    private const int DRAIN_MILLISECONDS = 200;

    private void ProgramEnded()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0)
            return;

        Task.Delay(DRAIN_MILLISECONDS).ContinueWith(_ =>
        {
            int code = GetExitCodeProcess(client, out uint exit) ? unchecked((int)exit) : -1;
            LetGo();
            EndedWith(code);
        }, TaskScheduler.Default);
    }

    // Letting go of the reference and the signal is what tells the console host it can go
    private void LetGo()
    {
        watching?.Unregister(null);
        try { reference.Dispose(); } catch (Exception) { }
        try { signal.Dispose(); } catch (Exception) { }
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
            // Gone, the end says so shortly
        }
    }

    protected override void ResizeTerminal(int columns, int rows)
    {
        Span<byte> packet = stackalloc byte[6];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packet, PTY_SIGNAL_RESIZE_WINDOW);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packet[2..], (ushort)columns);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packet[4..], (ushort)rows);
        try
        {
            if (!signal.IsClosed)
                WriteFile(signal, packet, (uint)packet.Length, out _, 0);
        }
        catch (Exception)
        {
        }
    }

    public override void Dispose()
    {
        // A tab closed on a program that's still going takes it with it, the way closing a console window does
        if (IsRunning && !clientEnded.WaitOne(0))
            TerminateProcess(client, 1);

        Interlocked.Exchange(ref closed, 1);
        LetGo();
        try { input.Dispose(); } catch (Exception) { }
        try { output.Dispose(); } catch (Exception) { }
        server.Dispose();
        client.Dispose();
        clientEnded.Dispose();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(SafeFileHandle file, ReadOnlySpan<byte> buffer, uint count, out uint written, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeFileHandle process, out uint code);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeFileHandle process, uint code);
}
