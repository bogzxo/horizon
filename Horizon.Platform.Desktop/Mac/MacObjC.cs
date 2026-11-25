using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Horizon.Platform.Desktop;

/// <summary>A point of Cocoa's, in its points (<c>CGPoint</c>, two doubles on a Mac).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MacPoint(double x, double y)
{
    public double X = x, Y = y;
}

/// <summary>A size of Cocoa's, in its points (<c>CGSize</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MacSize(double width, double height)
{
    public double Width = width, Height = height;
}

/// <summary>A rectangle of Cocoa's, in its points (<c>CGRect</c>, an origin and a size, four doubles).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MacRect(double x, double y, double width, double height)
{
    public double X = x, Y = y, Width = width, Height = height;
}

/// <summary>
/// The Objective-C runtime for the parts of the engine and its programs that talk to a Mac's own frameworks, one place for
/// what was copied into every file that did (an <c>objc_msgSend</c> declared again for each call it makes). Classes and
/// selectors are looked up once and kept, strings go to and from <c>NSString</c>, and there is a function for each shape
/// a call has: what <c>objc_msgSend</c> is given and what it returns. A call is a <c>Send</c> with the receiver, the
/// selector (<see cref="Sel"/>) and the arguments, <c>SendVoid</c> when nothing comes back, <c>SendBool</c>,
/// <c>SendUInt</c>, <c>SendInt</c> and <c>SendDouble</c> for what does.
/// <para>
/// It is for the main thread of the process, which is where AppKit lives and the thread SDL listens to the system on
/// (see <see cref="MacMain"/>). Everything is blittable and every callback a program gives it is an
/// <c>[UnmanagedCallersOnly]</c> function pointer (<see cref="MacClassBuilder"/>), so it works as it is in a trimmed
/// native executable: no reflection, no delegates made at run time, no blocks. Nothing here is to be called on a
/// platform but a Mac, a program asks <see cref="OperatingSystem.IsMacOS"/> first.
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacObjC
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    private static readonly ConcurrentDictionary<string, nint> selectors = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, nint> classes = new(StringComparer.Ordinal);

    /* Selectors and classes */

    /// <summary>The selector of a name ("setTitle:"), looked up once.</summary>
    public static nint Sel(string name) => selectors.GetOrAdd(name, static name => sel_registerName(name));

    /// <summary>The class of a name ("NSMenu"), looked up once, 0 for one there isn't (AppKit is loaded first, see <see cref="LoadAppKit"/>).</summary>
    public static nint Class(string name)
    {
        if (classes.TryGetValue(name, out nint known))
            return known;

        nint found = objc_getClass(name);
        if (found == 0)
        {
            LoadAppKit();
            found = objc_getClass(name);
        }

        if (found != 0)
            classes[name] = found;
        return found;
    }

    /// <summary>The class an object is of.</summary>
    public static nint ClassOf(nint instance) => object_getClass(instance);

    /// <summary>The class a class is a subclass of, 0 for a root.</summary>
    public static nint SuperclassOf(nint type) => class_getSuperclass(type);

    /// <summary>Whether an object has a method of a selector.</summary>
    public static bool RespondsTo(nint instance, nint selector) => SendBool(instance, Sel("respondsToSelector:"), selector);

    private static int appKitLoaded;

    /// <summary>Has AppKit loaded, for a class of it asked for before the window made it (nothing but the first call does anything).</summary>
    public static void LoadAppKit()
    {
        if (Interlocked.Exchange(ref appKitLoaded, 1) == 0)
            _ = NativeLibrary.TryLoad(AppKit, out _);
    }

    /* Objects */

    /// <summary>The shared <c>NSApplication</c>.</summary>
    public static nint SharedApplication => Send(Class("NSApplication"), Sel("sharedApplication"));

    /// <summary><c>[[cls alloc] init]</c>, an object you own (<see cref="Release"/> it).</summary>
    public static nint New(nint type) => Send(Send(type, Sel("alloc")), Sel("init"));

    /// <summary><c>[[cls alloc] init]</c> of a class by name.</summary>
    public static nint New(string className) => New(Class(className));

    public static nint Retain(nint instance) => instance == 0 ? 0 : Send(instance, Sel("retain"));

    public static void Release(nint instance)
    {
        if (instance != 0)
            SendVoid(instance, Sel("release"));
    }

    /// <summary>An <c>NSAutoreleasePool</c> the object you make in it that the system will release are held by, until it's disposed.</summary>
    public readonly struct AutoreleasePool : IDisposable
    {
        private readonly nint pool;
        internal AutoreleasePool(nint pool) => this.pool = pool;
        public void Dispose() => objc_autoreleasePoolPop(pool);
    }

    /// <summary>Starts a pool for the objects the calls that follow make, <c>using var pool = MacObjC.Pool();</c>.</summary>
    public static AutoreleasePool Pool() => new(objc_autoreleasePoolPush());

    /* Strings */

    /// <summary>An <c>NSString</c> of a string, autoreleased (so make it in a <see cref="Pool"/>, and retain it to keep it).</summary>
    public static nint NSString(string? text)
    {
        text ??= string.Empty;
        int length = Encoding.UTF8.GetByteCount(text);
        Span<byte> bytes = length < 512 ? stackalloc byte[length + 1] : new byte[length + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        bytes[length] = 0;
        fixed (byte* pointer = bytes)
            return SendBytes(Class("NSString"), Sel("stringWithUTF8String:"), pointer);
    }

    /// <summary>The text of an <c>NSString</c>, null for nil.</summary>
    public static string? ToManaged(nint nsString) =>
        nsString == 0 ? null : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String")));

    /// <summary>The <c>localizedDescription</c> or <c>description</c> of any object, for a log.</summary>
    public static string Describe(nint instance) => instance == 0 ? "nil" : ToManaged(Send(instance, Sel("description"))) ?? "";

    /* Send: returns an object (or any pointer-sized value) */

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern nint Send(nint receiver, nint selector, nint a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern nint Send(nint receiver, nint selector, nint a, nint b);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern nint Send(nint receiver, nint selector, nint a, nint b, nint c);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern nint Send(nint receiver, nint selector, nuint a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern nint Send(nint receiver, nint selector, long a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern nint Send(nint receiver, nint selector, double a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendBytes(nint receiver, nint selector, byte* a);

    /* SendVoid: returns nothing */

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid(nint receiver, nint selector, nint a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid(nint receiver, nint selector, nint a, nint b);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid(nint receiver, nint selector, nuint a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid(nint receiver, nint selector, long a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid(nint receiver, nint selector, double a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid(nint receiver, nint selector, MacRect a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid(nint receiver, nint selector, MacPoint a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid(nint receiver, nint selector, MacSize a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidByte(nint receiver, nint selector, byte a);

    /// <summary>A call that takes a <c>BOOL</c> and returns nothing, <c>[window setOpaque:NO]</c>.</summary>
    public static void SendVoid(nint receiver, nint selector, bool a) => SendVoidByte(receiver, selector, a ? (byte)1 : (byte)0);

    /* Values back */

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern byte SendByte(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern byte SendByte(nint receiver, nint selector, nint a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern byte SendByte(nint receiver, nint selector, nuint a);

    /// <summary>A call that returns a <c>BOOL</c>.</summary>
    public static bool SendBool(nint receiver, nint selector) => SendByte(receiver, selector) != 0;

    public static bool SendBool(nint receiver, nint selector, nint a) => SendByte(receiver, selector, a) != 0;

    public static bool SendBool(nint receiver, nint selector, nuint a) => SendByte(receiver, selector, a) != 0;

    /// <summary>A call that returns an <c>NSUInteger</c> (a style mask, a count).</summary>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern nuint SendUInt(nint receiver, nint selector);

    /// <summary>A call that returns an <c>NSInteger</c> (a tag, a window level).</summary>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern nint SendInt(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern nuint SendUInt(nint receiver, nint selector, nuint a);

    /// <summary>A call that returns a <c>CGFloat</c>.</summary>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern double SendDouble(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern MacPoint SendPointRaw(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern MacSize SendSizeRaw(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern MacRect SendRectRaw(nint receiver, nint selector);

    // x64 returns a structure bigger than two registers through memory, by way of its own entry point. arm64 doesn't
    // have one, objc_msgSend does for every structure there
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")]
    private static extern void SendRectStret(MacRect* result, nint receiver, nint selector);

    private static bool UsesStret => RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>A call that returns a <c>CGRect</c>, <c>[window frame]</c>.</summary>
    public static MacRect SendRect(nint receiver, nint selector)
    {
        if (!UsesStret)
            return SendRectRaw(receiver, selector);

        MacRect result = default;
        SendRectStret(&result, receiver, selector);
        return result;
    }

    /// <summary>A call that returns a <c>CGPoint</c> (two doubles come back in registers on both architectures).</summary>
    public static MacPoint SendPoint(nint receiver, nint selector) => SendPointRaw(receiver, selector);

    /// <summary>A call that returns a <c>CGSize</c>.</summary>
    public static MacSize SendSize(nint receiver, nint selector) => SendSizeRaw(receiver, selector);

    /* The runtime */

    [DllImport(ObjC, EntryPoint = "sel_registerName", CharSet = CharSet.Ansi)]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC, EntryPoint = "objc_getClass", CharSet = CharSet.Ansi)]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern nint object_getClass(nint instance);

    [DllImport(ObjC)]
    private static extern nint class_getSuperclass(nint type);

    [DllImport(ObjC)]
    private static extern nint objc_autoreleasePoolPush();

    [DllImport(ObjC)]
    private static extern void objc_autoreleasePoolPop(nint pool);

    /// <summary>The Objective-C runtime's own functions, for <see cref="MacClassBuilder"/>.</summary>
    internal static class Runtime
    {
        [DllImport(ObjC, EntryPoint = "objc_allocateClassPair")]
        public static extern nint AllocateClassPair(nint superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint extraBytes);

        [DllImport(ObjC, EntryPoint = "objc_registerClassPair")]
        public static extern void RegisterClassPair(nint type);

        [DllImport(ObjC, EntryPoint = "objc_disposeClassPair")]
        public static extern void DisposeClassPair(nint type);

        [DllImport(ObjC, EntryPoint = "class_addMethod")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool AddMethod(nint type, nint selector, nint implementation, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

        [DllImport(ObjC, EntryPoint = "class_addProtocol")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool AddProtocol(nint type, nint protocol);

        [DllImport(ObjC, EntryPoint = "objc_getProtocol")]
        public static extern nint GetProtocol([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    }
}
