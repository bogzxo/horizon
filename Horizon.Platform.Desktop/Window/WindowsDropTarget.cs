using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Horizon.Core;
using Horizon.Logging;

namespace Horizon.Platform.Desktop;

/// <summary>
/// The window as a drop target of OLE's on Windows, which hears files dragged from Explorer as they come over the window,
/// move about on it and leave, not only once they are dropped (all GLFW hears, WM_DROPFILES). OLE only takes a target from
/// a thread in a single threaded apartment and the window's is the program's main thread, which .NET has put in the
/// multithreaded one, so the target has a thread of its own that OLE calls it on, and says what it heard from there.
/// An IDropTarget of its own making (a table of function pointers), so there's nothing for trimming or AOT to take away.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsDropTarget : IDisposable
{
    private const string Name = "Window Manager";

    private const uint WM_QUIT = 0x0012;
    private const int S_OK = 0, E_NOINTERFACE = unchecked((int)0x80004002);
    private const uint DROPEFFECT_NONE = 0, DROPEFFECT_COPY = 1;
    private const ushort CF_HDROP = 15;
    private const uint DVASPECT_CONTENT = 1, TYMED_HGLOBAL = 1;

    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_IDropTarget = new("00000122-0000-0000-C000-000000000046");

    // IUnknown's three and IDropTarget's four, the same for every target
    private static readonly nint* VTable = MakeVTable();

    private readonly nint window;
    private readonly Action<FileDrop> said;
    private readonly Thread thread;
    private readonly ManualResetEventSlim started = new();
    private uint threadId;
    private bool registered;

    // The COM object OLE is handed, the table, this and its count, freed once OLE let go of it
    private Target* target;

    // What is held over the window, and where it was last said to be, so moving about says only a change
    private bool holding;
    private Vector2 lastAt = new(float.NaN);

    [StructLayout(LayoutKind.Sequential)]
    private struct Target
    {
        public nint* Table;
        public nint Self;
        public int References;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FormatEtc
    {
        public ushort Format;
        public nint Device;
        public uint Aspect;
        public int Index;
        public uint Medium;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StgMedium
    {
        public uint Medium;
        public nint Handle;
        public nint ReleaseWith;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Kind;
        public nint WParam, LParam;
        public uint Time;
        public PointL Point;
        public uint Private;
    }

    private WindowsDropTarget(nint window, Action<FileDrop> said)
    {
        this.window = window;
        this.said = said;
        thread = new Thread(Run) { Name = "Drop Target", IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>The window made a drop target, null when OLE wouldn't have it (GLFW's drop is all there is then). Any thread.</summary>
    public static WindowsDropTarget? Start(nint window, Action<FileDrop> said)
    {
        var made = new WindowsDropTarget(window, said);
        made.thread.Start();
        if (made.started.Wait(5000) && made.registered)
            return made;

        made.Dispose();
        return null;
    }

    /// <summary>Where the pointer is in the window's inside, in pixels. Any thread.</summary>
    public static bool CursorInClient(nint window, out Vector2 at)
    {
        PointL point;
        if (GetCursorPos(&point) && ScreenToClient(window, &point))
        {
            at = new Vector2(point.X, point.Y);
            return true;
        }

        at = Vector2.Zero;
        return false;
    }

    /// <summary>The thread of the target, which OLE calls it on, its messages heard until it's told to quit.</summary>
    private void Run()
    {
        int ole = OleInitialize(0);
        if (ole < 0)
        {
            Log.Warning($"[{Name}] OLE would not start for the drop target (0x{ole:X8}), files are heard only once dropped.");
            started.Set();
            return;
        }

        threadId = GetCurrentThreadId();
        target = (Target*)NativeMemory.AllocZeroed((nuint)sizeof(Target));
        target->Table = VTable;
        target->Self = GCHandle.ToIntPtr(GCHandle.Alloc(this));
        target->References = 1;

        int answer = RegisterDragDrop(window, target);
        registered = answer >= 0;
        if (!registered)
            Log.Warning($"[{Name}] The window would not be a drop target (0x{answer:X8}), files are heard only once dropped.");
        started.Set();

        if (registered)
        {
            Message message;
            while (GetMessageW(&message, 0, 0, 0) > 0)
            {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }

            RevokeDragDrop(window);
        }

        // OLE let go of its references as the target was revoked, whatever is still held is let go of here
        GCHandle.FromIntPtr(target->Self).Free();
        NativeMemory.Free(target);
        target = null;
        OleUninitialize();
    }

    /// <summary>The window no drop target any more, from any thread but the target's own.</summary>
    public void Dispose()
    {
        if (thread.IsAlive)
        {
            started.Wait(5000);
            if (threadId != 0)
                PostThreadMessageW(threadId, WM_QUIT, 0, 0);
            thread.Join(5000);
        }

        started.Dispose();
    }

    /// <summary>Helper method to say what the drag is doing, whatever whoever hears it makes of it. The target's thread.</summary>
    private void Say(FileDrop drop)
    {
        try
        {
            said(drop);
        }
        catch (Exception e)
        {
            Log.Warning($"[{Name}] A drop of files went wrong being heard, {e.Message}");
        }
    }

    private Vector2 InClient(PointL screen)
    {
        PointL point = screen;
        return ScreenToClient(window, &point) ? new Vector2(point.X, point.Y) : Vector2.Zero;
    }

    private uint Entered(nint data, PointL at)
    {
        string[] paths = PathsIn(data);
        holding = paths.Length > 0;
        if (!holding)
            return DROPEFFECT_NONE;

        lastAt = InClient(at);
        Say(new FileDrop(FileDropKind.Enter, paths, lastAt));
        return DROPEFFECT_COPY;
    }

    private uint Moved(PointL at)
    {
        if (!holding)
            return DROPEFFECT_NONE;

        Vector2 now = InClient(at);
        if (now != lastAt)
        {
            lastAt = now;
            Say(new FileDrop(FileDropKind.Over, [], now));
        }

        return DROPEFFECT_COPY;
    }

    private void Left()
    {
        if (!holding)
            return;

        holding = false;
        Say(FileDrop.Left);
    }

    private uint Dropped(nint data, PointL at)
    {
        holding = false;
        string[] paths = PathsIn(data);
        if (paths.Length == 0)
        {
            Say(FileDrop.Left);
            return DROPEFFECT_NONE;
        }

        Say(new FileDrop(FileDropKind.Drop, paths, InClient(at)));
        return DROPEFFECT_COPY;
    }

    /// <summary>
    /// The files and folders in what is dragged, its CF_HDROP, none for anything else (text, a picture out of a browser,
    /// an attachment that isn't a file yet). The target's thread.
    /// </summary>
    internal static string[] PathsIn(nint data)
    {
        if (data == 0)
            return [];

        var format = new FormatEtc { Format = CF_HDROP, Aspect = DVASPECT_CONTENT, Index = -1, Medium = TYMED_HGLOBAL };
        StgMedium medium = default;
        var getData = (delegate* unmanaged[Stdcall]<nint, FormatEtc*, StgMedium*, int>)(*(nint**)data)[3];
        if (getData(data, &format, &medium) < 0)
            return [];

        try
        {
            if (medium.Medium != TYMED_HGLOBAL || medium.Handle == 0)
                return [];

            uint count = DragQueryFileW(medium.Handle, uint.MaxValue, null, 0);
            var paths = new List<string>((int)Math.Min(count, 4096));
            for (uint index = 0; index < count; index++)
            {
                uint length = DragQueryFileW(medium.Handle, index, null, 0);
                if (length == 0)
                    continue;

                char[] buffer = new char[length + 1];
                fixed (char* into = buffer)
                {
                    uint copied = DragQueryFileW(medium.Handle, index, into, length + 1);
                    if (copied > 0)
                        paths.Add(new string(into, 0, (int)copied));
                }
            }

            return [.. paths];
        }
        finally
        {
            ReleaseStgMedium(&medium);
        }
    }

    private static WindowsDropTarget? Of(Target* self) =>
        self == null || self->Self == 0 ? null : GCHandle.FromIntPtr(self->Self).Target as WindowsDropTarget;

    private static nint* MakeVTable()
    {
        var table = (nint*)NativeMemory.Alloc((nuint)(7 * sizeof(nint)));
        table[0] = (nint)(delegate* unmanaged[Stdcall]<Target*, Guid*, void**, int>)&QueryInterface;
        table[1] = (nint)(delegate* unmanaged[Stdcall]<Target*, uint>)&AddRef;
        table[2] = (nint)(delegate* unmanaged[Stdcall]<Target*, uint>)&Release;
        table[3] = (nint)(delegate* unmanaged[Stdcall]<Target*, nint, uint, PointL, uint*, int>)&DragEnter;
        table[4] = (nint)(delegate* unmanaged[Stdcall]<Target*, uint, PointL, uint*, int>)&DragOver;
        table[5] = (nint)(delegate* unmanaged[Stdcall]<Target*, int>)&DragLeave;
        table[6] = (nint)(delegate* unmanaged[Stdcall]<Target*, nint, uint, PointL, uint*, int>)&Drop;
        return table;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(Target* self, Guid* iid, void** into)
    {
        if (into == null)
            return E_NOINTERFACE;

        if (*iid == IID_IUnknown || *iid == IID_IDropTarget)
        {
            Interlocked.Increment(ref self->References);
            *into = self;
            return S_OK;
        }

        *into = null;
        return E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(Target* self) => (uint)Interlocked.Increment(ref self->References);

    // Freed by the target's thread once it is revoked, not here, whatever the count comes to
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(Target* self) => (uint)Math.Max(0, Interlocked.Decrement(ref self->References));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DragEnter(Target* self, nint data, uint keys, PointL at, uint* effect)
    {
        uint answer = DROPEFFECT_NONE;
        try
        {
            answer = Of(self)?.Entered(data, at) ?? DROPEFFECT_NONE;
        }
        catch (Exception)
        {
        }

        if (effect != null)
            *effect = answer;
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DragOver(Target* self, uint keys, PointL at, uint* effect)
    {
        uint answer = DROPEFFECT_NONE;
        try
        {
            answer = Of(self)?.Moved(at) ?? DROPEFFECT_NONE;
        }
        catch (Exception)
        {
        }

        if (effect != null)
            *effect = answer;
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DragLeave(Target* self)
    {
        try
        {
            Of(self)?.Left();
        }
        catch (Exception)
        {
        }

        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Drop(Target* self, nint data, uint keys, PointL at, uint* effect)
    {
        uint answer = DROPEFFECT_NONE;
        try
        {
            answer = Of(self)?.Dropped(data, at) ?? DROPEFFECT_NONE;
        }
        catch (Exception)
        {
        }

        if (effect != null)
            *effect = answer;
        return S_OK;
    }

    [LibraryImport("ole32.dll")]
    private static partial int OleInitialize(nint reserved);

    [LibraryImport("ole32.dll")]
    private static partial void OleUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int RegisterDragDrop(nint window, void* target);

    [LibraryImport("ole32.dll")]
    private static partial int RevokeDragDrop(nint window);

    [LibraryImport("ole32.dll")]
    private static partial void ReleaseStgMedium(StgMedium* medium);

    [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW")]
    private static partial uint DragQueryFileW(nint drop, uint index, char* into, uint length);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(PointL* point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(nint window, PointL* point);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(Message* message, nint window, uint first, uint last);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(Message* message);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(Message* message);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint thread, uint message, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
