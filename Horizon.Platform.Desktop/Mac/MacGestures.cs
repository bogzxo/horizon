using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Diagnostics;

using Horizon.Input;

namespace Horizon.Platform.Desktop;

/// <summary>
/// The trackpad's gestures that AppKit sends to a view and SDL has no event for: pinch (<c>magnifyWithEvent:</c>), double tap
/// with two fingers (smart zoom, <c>smartMagnifyWithEvent:</c>) and the swipe (<c>swipeWithEvent:</c>). The methods are added
/// with <c>class_addMethod</c> to the class of the window's content view and of the views in it (SDL's, and the Metal view
/// Vulkan's surface is), where the responder chain finds them, as <c>MacOpenDocuments</c> does for the delegate.
/// What comes is raised as <see cref="InputEvent"/>s on the main thread.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class MacGestures
{
    private static Action<InputEvent>? raise;
    private static int installed;

    /// <summary>Has the gestures of a window's views raised through <paramref name="said"/>, once.</summary>
    public static void Install(nint window, Action<InputEvent> said)
    {
        if (!OperatingSystem.IsMacOS() || window == 0 || Interlocked.Exchange(ref installed, 1) != 0)
            return;

        raise = said;
        try
        {
            using var pool = MacObjC.Pool();
            nint content = MacObjC.Send(window, MacObjC.Sel("contentView"));
            if (content == 0)
                return;

            AddTo(MacObjC.ClassOf(content));
            nint views = MacObjC.Send(content, MacObjC.Sel("subviews"));
            nuint count = views == 0 ? 0 : MacObjC.SendUInt(views, MacObjC.Sel("count"));
            for (nuint i = 0; i < count; i++)
            {
                nint view = MacObjC.Send(views, MacObjC.Sel("objectAtIndex:"), i);
                if (view != 0)
                    AddTo(MacObjC.ClassOf(view));
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // no Objective-C, no gestures
        }
    }

    private static void AddTo(nint type)
    {
        // Each says false when the class has the method already (a superclass's doesn't count, it is overridden)
        MacObjC.Runtime.AddMethod(type, MacObjC.Sel("magnifyWithEvent:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&Magnify, "v@:@");
        MacObjC.Runtime.AddMethod(type, MacObjC.Sel("smartMagnifyWithEvent:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&SmartMagnify, "v@:@");
        MacObjC.Runtime.AddMethod(type, MacObjC.Sel("swipeWithEvent:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&Swipe, "v@:@");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Magnify(nint self, nint command, nint e)
    {
        try
        {
            // The change of size since the last event, 0.02 for two per cent bigger
            float amount = (float)MacObjC.SendDouble(e, MacObjC.Sel("magnification"));
            raise?.Invoke(new InputEvent { Kind = InputEventKind.Magnify, Magnification = amount, Timestamp = Stopwatch.GetTimestamp() });
        }
        catch (Exception)
        {
            // a gesture that couldn't be read is one that didn't happen
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SmartMagnify(nint self, nint command, nint e)
    {
        try { raise?.Invoke(new InputEvent { Kind = InputEventKind.SmartMagnify, Timestamp = Stopwatch.GetTimestamp() }); }
        catch (Exception) { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Swipe(nint self, nint command, nint e)
    {
        try
        {
            // deltaX is 1 for a swipe to the left and -1 for one to the right (the way a page goes forward and back)
            double x = MacObjC.SendDouble(e, MacObjC.Sel("deltaX"));
            if (x != 0.0)
                raise?.Invoke(new InputEvent { Kind = InputEventKind.Swipe, SwipeX = x > 0.0 ? 1 : -1, Timestamp = Stopwatch.GetTimestamp() });
        }
        catch (Exception) { }
    }
}
