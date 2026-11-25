using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Horizon.Platform.Desktop;

/// <summary>
/// A Mac window's own chrome under the UI's drawn title bar: the NSWindow is made a titled, full-size-content one with a
/// transparent, title-less title bar, which gives the real traffic lights, the green button's tiling menu, native full screen,
/// edge resizing, the shadow and the rounded corners, while the UI keeps drawing its own bar across the top and leaves room
/// for the lights (<see cref="LeadingPoints"/>). Everything here is AppKit's and runs on the main thread, the numbers it
/// publishes are read from any.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MacWindowChrome
{
    // NSWindowStyleMask
    private const nuint TITLED = 1 << 0, CLOSABLE = 1 << 1, MINIATURIZABLE = 1 << 2, RESIZABLE = 1 << 3, FULL_SIZE_CONTENT = 1 << 15, FULLSCREEN = 1 << 14;
    private const nuint WANTED = TITLED | CLOSABLE | MINIATURIZABLE | RESIZABLE | FULL_SIZE_CONTENT;

    // NSWindowTitleHidden
    private const long TITLE_HIDDEN = 1;

    // NSTitlebarSeparatorStyleNone
    private const long SEPARATOR_NONE = 1;

    // How far past the last light the title bar's own things start, in points
    private const double LIGHTS_GAP = 8.0;

    // How old a press may be and still be the one dragged by, in seconds of the system's uptime
    private const double PRESS_FRESH = 1.0;

    private static MacWindowChrome? current;

    private readonly nint window;
    private readonly Action changed;

    private volatile float leading, height;
    private volatile bool titled;
    private volatile int screenMaxFps;

    // How tall the UI's title bar is, in points, 0 until it says
    private double barPoints;

    // Dirty tracking to avoid redundant per-tick AppKit/ObjC calls in Maintain()
    private bool ownLightsOn;
    private bool lightsDirty = true;
    private nuint lastMask;
    private double centeredForBar;
    private double centeredWindowHeight;

    private MacWindowChrome(nint window, Action changed)
    {
        this.window = window;
        this.changed = changed;
    }

    /// <summary>Whether the window has been made a titled one, which is when the system rounds and shades it.</summary>
    public bool Titled => titled;

    /// <summary>How far in from the left edge the title bar's own things start, in points, 0 in fullscreen.</summary>
    public float LeadingPoints => leading;

    /// <summary>
    /// How far from the right edge the title bar's things stop, in points, 0 in fullscreen: the margin the lights have on the left
    /// (their gap to the edge), which is clear of the corner's radius (about 10 points on a window with a title bar).
    /// </summary>
    public float TrailingPoints => height > 0 ? Math.Max(leftMargin, 12f) : 0f;

    // How far the first light is from the left edge, in points
    private volatile float leftMargin;

    /// <summary>How tall the system's own title bar is, in points, 0 in fullscreen.</summary>
    public float HeightPoints => height;

    /// <summary>Whether the UI's bar is to be exactly <see cref="HeightPoints"/> tall, in full screen with the own lights up.</summary>
    public bool ExactHeight => exact;

    private volatile bool exact;
    private bool publishedExact;

    // How tall the system's title bar is, read while windowed (the strip that slides down in full screen is as tall)
    private volatile float systemBar = 28.0f;

    /// <summary>Makes the window's chrome, main thread. Null when there is no NSWindow.</summary>
    public static MacWindowChrome? Create(nint window, Action changed)
    {
        if (!OperatingSystem.IsMacOS() || window == 0)
            return null;

        var chrome = new MacWindowChrome(window, changed);
        current = chrome;
        chrome.Apply();
        chrome.Observe();
        return chrome;
    }

    /// <summary>The style mask the window has now, for the log and the checks.</summary>
    public nuint StyleMask => MacObjC.SendUInt(window, MacObjC.Sel("styleMask"));

    /// <summary>
    /// Maximum refresh rate of the screen currently holding this window (120 on ProMotion Macs, 60 on standard displays,
    /// 0 if unavailable). Cached on the main thread during creation and window/screen notifications so any thread may read it.
    /// </summary>
    public int ScreenMaxFramesPerSecond() => screenMaxFps;

    private void RefreshScreenMaxFps()
    {
        nint screen = MacObjC.Send(window, MacObjC.Sel("screen"));
        if (screen != 0 && MacObjC.RespondsTo(screen, MacObjC.Sel("maximumFramesPerSecond")))
        {
            int fps = (int)MacObjC.SendInt(screen, MacObjC.Sel("maximumFramesPerSecond"));
            screenMaxFps = fps > 0 ? fps : 0;
        }
        else
        {
            screenMaxFps = 0;
        }
    }

    private bool InFullscreen => (StyleMask & FULLSCREEN) != 0;

    // SDL's views say a press on them can move the window, which AppKit then takes as a drag of its own where it can (the strip of
    // the title bar), and SDL hears a press at 0,0 or none. They say no: the UI has the system drag the window, see TryDrag
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte NoDragByPress(nint self, nint selector) => 0;

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "class_replaceMethod")]
    private static extern nint ReplaceMethod(nint type, nint selector, nint implementation, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    private bool metalViewPatched;

    private void KeepPressesForTheUi()
    {
        nint view = MacObjC.Send(window, MacObjC.Sel("contentView"));
        if (view == 0)
            return;

        PatchView(view);

        // The Metal view SDL puts in the window once the surface is made, which is later than this
        nint subviews = MacObjC.Send(view, MacObjC.Sel("subviews"));
        long count = subviews == 0 ? 0 : MacObjC.SendInt(subviews, MacObjC.Sel("count"));
        for (long i = 0; i < count; i++)
            PatchView(MacObjC.Send(subviews, MacObjC.Sel("objectAtIndex:"), (nuint)i));
        metalViewPatched = count > 0;
    }

    // The class of a view of SDL's (and what Cocoa made of it for key-value observing, a subclass) answers no
    private static void PatchView(nint view)
    {
        for (nint type = MacObjC.ClassOf(view); type != 0 && MacObjC.Describe(type).Contains("SDL", StringComparison.Ordinal); type = MacObjC.SuperclassOf(type))
            ReplaceMethod(type, MacObjC.Sel("mouseDownCanMoveWindow"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte>)&NoDragByPress, "c@:");
    }

    /// <summary>
    /// SDL hears the mouse through its window listener, which it puts in the responder chain as the content view's next
    /// responder (SDL_cocoawindow.m, listen:). A new style mask has AppKit make the window's frame view again and give the
    /// content view a next responder of its own, which leaves SDL deaf to the mouse's moves and releases (only a press still
    /// came another way, so a click stayed held and nothing hovered). The listener is the window's delegate, and is put back
    /// whenever it isn't there. Main thread, cheap enough for every tick.
    /// </summary>
    private void KeepSdlListening()
    {
        nint content = MacObjC.Send(window, MacObjC.Sel("contentView"));
        nint listener = MacObjC.Send(window, MacObjC.Sel("delegate"));
        if (content == 0 || listener == 0 || listener == content)
            return;

        nint next = MacObjC.Send(content, MacObjC.Sel("nextResponder"));
        if (next == listener)
            return;

        // As SDL itself does it: the view's next responder is the listener, and the listener's stays as SDL left it
        MacObjC.SendVoid(content, MacObjC.Sel("setNextResponder:"), listener);
    }

    /// <summary>Gives the window the mask and the transparent title bar, then reads where the lights are. Main thread.</summary>
    public void Apply()
    {
        KeepPressesForTheUi();
        nuint mask = StyleMask;
        if ((mask & FULLSCREEN) == 0 && (mask & WANTED) != WANTED)
            MacObjC.SendVoid(window, MacObjC.Sel("setStyleMask:"), mask | WANTED);

        // The strip of the title bar is the system's to drag by, and takes the press (and a double click, which it zooms by)
        // before SDL hears it, so none of the UI's bar could be clicked. The UI has the system drag the window itself, with the
        // press, see TryDrag
        MacObjC.SendVoid(window, MacObjC.Sel("setMovable:"), false);
        MacObjC.SendVoid(window, MacObjC.Sel("setTitlebarAppearsTransparent:"), true);
        MacObjC.SendVoid(window, MacObjC.Sel("setTitleVisibility:"), TITLE_HIDDEN);

        // No line or shadow under the system's bar (NSTitlebarSeparatorStyleNone): in full screen the strip that slides down
        // drew its shadow over the UI's bar
        if (MacObjC.RespondsTo(window, MacObjC.Sel("setTitlebarSeparatorStyle:")))
            MacObjC.SendVoid(window, MacObjC.Sel("setTitlebarSeparatorStyle:"), SEPARATOR_NONE);
        KeepSdlListening();
        titled = true;
        lastMask = StyleMask;
        lightsDirty = true;
        Refresh();
    }

    /// <summary>
    /// Puts the mask back when SDL took it (it rebuilds the style from its own flags when it is asked something of the window,
    /// resizable, fullscreen, and leaves the frameless one it knows). Cheap enough to ask every tick. Main thread.
    /// </summary>
    public void Maintain()
    {
        KeepSdlListening();
        nuint mask = StyleMask;
        if (mask != lastMask)
        {
            lastMask = mask;
            lightsDirty = true;
        }

        if ((mask & FULLSCREEN) == 0)
            HideOwnLights();
        if ((mask & FULLSCREEN) == 0 && (mask & WANTED) != WANTED)
            Apply();
        else if ((mask & FULLSCREEN) == 0)
        {
            if (lightsDirty || Math.Abs(barPoints - centeredForBar) >= 0.01)
                CenterLights();
        }
        else
        {
            HideRevealStrip();
            ShowOwnLights();
        }
        if (!metalViewPatched)
            KeepPressesForTheUi();
    }

    private long stripChecked;

    /// <summary>
    /// In full screen the pointer at the top of the screen slides the system's title bar down over the window, with its own
    /// material, separator and the lights: unskinned, animating over the UI's bar. The UI has the whole screen in full screen
    /// (the menu bar still comes down by itself, and the Window menu and the keys leave full screen), so the strip's container
    /// is hidden. AppKit builds it again for each full screen, so this is asked a few times a second while in it. Main thread.
    /// </summary>
    private void HideRevealStrip()
    {
        long now = Environment.TickCount64;
        if (now - stripChecked < 100)
            return;

        stripChecked = now;
        nint strip = Button(0) is var light && light != 0 ? MacObjC.Send(light, MacObjC.Sel("superview")) : 0;
        if (strip == 0 || !MacObjC.Describe(MacObjC.ClassOf(strip)).Contains("NSTitlebarView", StringComparison.Ordinal))
            return;

        nint container = MacObjC.Send(strip, MacObjC.Sel("superview"));
        if (container != 0 && !MacObjC.SendBool(container, MacObjC.Sel("isHidden")))
            MacObjC.SendVoid(container, MacObjC.Sel("setHidden:"), true);

        // The strip is a window of its own in full screen (NSToolbarFullScreenWindow), and with what's in it hidden its
        // shadow still slid down over the UI's bar. Nothing of it is seen
        nint holder = container == 0 ? 0 : MacObjC.Send(container, MacObjC.Sel("window"));
        if (holder != 0 && holder != window)
            Unseen(holder);

        // It may not be in there yet when this looks: AppKit's full screen bar windows are found by their class as well
        nint windows = MacObjC.Send(MacObjC.SharedApplication, MacObjC.Sel("windows"));
        long count = windows == 0 ? 0 : MacObjC.SendInt(windows, MacObjC.Sel("count"));
        for (long i = 0; i < count; i++)
        {
            nint other = MacObjC.Send(windows, MacObjC.Sel("objectAtIndex:"), (nuint)i);
            if (other != 0 && other != window && MacObjC.Describe(MacObjC.ClassOf(other)).Contains("ToolbarFullScreen", StringComparison.Ordinal))
                Unseen(other);
        }
    }

    // A window of AppKit's that's to show nothing, not even its shadow
    private static void Unseen(nint other)
    {
        if (MacObjC.SendBool(other, MacObjC.Sel("hasShadow")))
            MacObjC.SendVoid(other, MacObjC.Sel("setHasShadow:"), false);
        if (MacObjC.SendDouble(other, MacObjC.Sel("alphaValue")) != 0.0)
            MacObjC.SendVoid(other, MacObjC.Sel("setAlphaValue:"), 0.0);

        // Unseen it still took the pointer, over the lights in the UI's bar as it slid down
        if (!MacObjC.SendBool(other, MacObjC.Sel("ignoresMouseEvents")))
            MacObjC.SendVoid(other, MacObjC.Sel("setIgnoresMouseEvents:"), true);
    }

    /* The lights in full screen. The system's are in the strip that's hidden, and nothing else closed the window or took
       it out of full screen. Lights of the window's own kind (standardWindowButton:forStyleMask:, which act on the window
       they're in: the green one leaves full screen) are put in the content view where the windowed ones are, over the
       UI's bar, which leaves room for them as it does for the windowed ones */

    private readonly nint[] ownLights = new nint[3];

    // Where the windowed lights are across, from the left edge, read while windowed
    private readonly double[] lightX = [7.0, 27.0, 47.0];

    // Where the own lights were put last, and in what size of view
    private MacRect ownPlaced;
    private double ownPlacedBar;

    private void ShowOwnLights()
    {
        nint content = MacObjC.Send(window, MacObjC.Sel("contentView"));
        if (content == 0 || barPoints <= 0)
            return;

        MacRect bounds = MacObjC.SendRect(content, MacObjC.Sel("bounds"));
        bool flipped = MacObjC.SendBool(content, MacObjC.Sel("isFlipped"));
        bool placed = bounds.Width == ownPlaced.Width && bounds.Height == ownPlaced.Height && Math.Abs(barPoints - ownPlacedBar) < 0.01;
        for (int which = 0; which < 3; which++)
        {
            if (ownLights[which] == 0)
            {
                ownLights[which] = MacObjC.Send(MacObjC.Class("NSWindow"), MacObjC.Sel("standardWindowButton:forStyleMask:"), (nint)which, (nint)WANTED);
                if (ownLights[which] == 0)
                    return;

                MacObjC.Send(ownLights[which], MacObjC.Sel("retain"));
            }

            nint light = ownLights[which];
            if (MacObjC.Send(light, MacObjC.Sel("superview")) != content)
            {
                MacObjC.SendVoid(content, MacObjC.Sel("addSubview:"), light);
                placed = false;
            }

            if (placed)
                continue;

            // In the middle of the UI's bar, as the windowed ones are put
            MacRect frame = MacObjC.SendRect(light, MacObjC.Sel("frame"));
            double fromTop = barPoints * 0.5 - frame.Height * 0.5;
            double y = flipped ? fromTop : bounds.Height - fromTop - frame.Height;
            MacObjC.SendVoid(light, MacObjC.Sel("setFrameOrigin:"), new MacPoint(lightX[which], y));
        }

        ownLightsOn = true;

        if (!placed)
        {
            ownPlaced = bounds;
            ownPlacedBar = barPoints;
            Refresh();
        }
    }

    private void HideOwnLights()
    {
        if (!ownLightsOn)
            return;

        foreach (nint light in ownLights)
        {
            if (light != 0 && MacObjC.Send(light, MacObjC.Sel("superview")) != 0)
                MacObjC.SendVoid(light, MacObjC.Sel("removeFromSuperview"));
        }

        ownPlaced = default;
        ownLightsOn = false;
    }

    // How far right the own lights reach, 0 while they aren't up
    private float OwnLightsRight()
    {
        nint last = ownLights[2];
        if (last == 0 || MacObjC.Send(last, MacObjC.Sel("superview")) == 0)
            return 0.0f;

        MacRect frame = MacObjC.SendRect(last, MacObjC.Sel("frame"));
        return (float)(frame.X + frame.Width);
    }

    /// <summary>Reads where the lights are and puts them in the middle of the UI's bar. Main thread.</summary>
    public void Refresh()
    {
        lightsDirty = true;
        RefreshScreenMaxFps();

        if (InFullscreen)
        {
            // Room for the own lights, as for the windowed ones
            // and the UI's bar exactly as tall as the system's, whose strip slides down over it with a shadow that can't be
            // taken off it: the two line up
            float own = OwnLightsRight();
            exact = own > 0;
            Publish(own > 0 ? own + (float)LIGHTS_GAP : 0, own > 0 ? systemBar : 0);
            return;
        }

        exact = false;
        CenterLights();
        ReadLights(out float right, out float bar);
        if (bar > 0)
            systemBar = bar;
        Publish(right > 0 ? right + (float)LIGHTS_GAP : 0, bar);
    }

    private void Publish(float newLeading, float newHeight)
    {
        if (leading == newLeading && height == newHeight && publishedExact == exact)
            return;

        leading = newLeading;
        height = newHeight;
        publishedExact = exact;
        changed();
    }

    /// <summary>The UI's title bar is so tall, in points: the lights are centred in it. Main thread.</summary>
    public void SetBarHeight(double points)
    {
        if (Math.Abs(points - barPoints) < 0.01)
            return;

        barPoints = points;
        lightsDirty = true;
        Refresh();
    }

    private nint Button(int which) => MacObjC.Send(window, MacObjC.Sel("standardWindowButton:"), (nint)which);

    // Where a view is in the window, bottom left origin (the frames of its ancestors added up, the lights' have no scaling or
    // flipping above them), and the height of the topmost ancestor, which is the window's
    private static MacRect InWindow(nint view, out double windowHeight)
    {
        MacRect frame = MacObjC.SendRect(view, MacObjC.Sel("frame"));
        double x = frame.X, y = frame.Y, top = frame.Height;
        for (nint above = MacObjC.Send(view, MacObjC.Sel("superview")); above != 0; above = MacObjC.Send(above, MacObjC.Sel("superview")))
        {
            MacRect rect = MacObjC.SendRect(above, MacObjC.Sel("frame"));
            x += rect.X;
            y += rect.Y;
            top = rect.Height;
        }

        windowHeight = top;
        return new MacRect(x, y, frame.Width, frame.Height);
    }

    private void ReadLights(out float right, out float bar)
    {
        right = 0;
        bar = 0;
        for (int which = 0; which < 3; which++)
        {
            nint button = Button(which);
            if (button == 0 || MacObjC.SendBool(button, MacObjC.Sel("isHidden")))
                continue;

            MacRect rect = InWindow(button, out _);
            right = Math.Max(right, (float)(rect.X + rect.Width));
            lightX[which] = rect.X;
            if (which == 0)
                leftMargin = (float)rect.X;

            // The system's title bar is the view they sit in
            nint holder = MacObjC.Send(button, MacObjC.Sel("superview"));
            if (holder != 0)
                bar = Math.Max(bar, (float)MacObjC.SendRect(holder, MacObjC.Sel("frame")).Height);
        }
    }

    // The lights sit in the middle of the system's title bar, which is a little shorter than the UI's: all three are moved down
    // by what is missing, so they are in the middle of the UI's. Where they would be moved out of the view they are in (which
    // the system would not let a press through) they stay as far down as they go. AppKit puts them back whenever it lays the
    // window out, which Refresh is asked to again after
    private void CenterLights()
    {
        if (barPoints <= 0)
        {
            lightsDirty = false;
            return;
        }

        nint first = Button(0);
        if (first == 0)
            return;

        MacRect rect = InWindow(first, out double windowHeight);
        if (!lightsDirty && Math.Abs(barPoints - centeredForBar) < 0.01 && Math.Abs(windowHeight - centeredWindowHeight) < 0.01)
            return;

        double centreFromTop = windowHeight - (rect.Y + rect.Height * 0.5);
        double move = barPoints * 0.5 - centreFromTop;
        // Mid-transition (fullscreen coming or going) the views are somewhere else, nothing to centre then
        if (Math.Abs(move) > 40)
            return;
        if (Math.Abs(move) < 0.01)
        {
            centeredForBar = barPoints;
            centeredWindowHeight = windowHeight;
            lightsDirty = false;
            return;
        }

        // Down is less of Y, and no light goes below the bottom of what holds it
        double lowest = double.MaxValue;
        for (int which = 0; which < 3; which++)
        {
            if (Button(which) is var button && button != 0)
                lowest = Math.Min(lowest, MacObjC.SendRect(button, MacObjC.Sel("frame")).Y);
        }

        if (lowest - move < 0)
            move = lowest;

        for (int which = 0; which < 3; which++)
        {
            nint button = Button(which);
            if (button == 0)
                continue;

            MacRect frame = MacObjC.SendRect(button, MacObjC.Sel("frame"));
            MacObjC.SendVoid(button, MacObjC.Sel("setFrameOrigin:"), new MacPoint(frame.X, frame.Y - move));
        }

        centeredForBar = barPoints;
        centeredWindowHeight = windowHeight;
        lightsDirty = false;
    }

    /* Drag */

    /// <summary>
    /// Has the system drag the window with the press that is down now, the way it drags any window (tiling, Stage Manager, the
    /// edges of the screen), and returns when the button is up. False when there is no such press: the event is not the
    /// window's, is old, or the button is not down any more. Main thread.
    /// </summary>
    public bool TryDrag()
    {
        nint press = MacMouseDownWatch.Last;
        if (press == 0 || MacObjC.Send(press, MacObjC.Sel("window")) != window)
            return false;

        if ((MacObjC.SendUInt(MacObjC.Class("NSEvent"), MacObjC.Sel("pressedMouseButtons")) & 1) == 0)
            return false;

        double now = MacObjC.SendDouble(MacObjC.Send(MacObjC.Class("NSProcessInfo"), MacObjC.Sel("processInfo")), MacObjC.Sel("systemUptime"));
        double then = MacObjC.SendDouble(press, MacObjC.Sel("timestamp"));
        if (now - then > PRESS_FRESH)
            return false;

        MacObjC.SendVoid(window, MacObjC.Sel("performWindowDragWithEvent:"), press);
        return true;
    }

    /// <summary>Whether the left button is down by the system's count. Main thread, any.</summary>
    public static bool LeftIsDown() =>
        (MacObjC.SendUInt(MacObjC.Class("NSEvent"), MacObjC.Sel("pressedMouseButtons")) & 1) != 0;

    /* Double click */

    /// <summary>What a double click on the caption does, by the person's setting in System Settings.</summary>
    public static MacCaptionAction ReadCaptionAction()
    {
        nint defaults = MacObjC.Send(MacObjC.Class("NSUserDefaults"), MacObjC.Sel("standardUserDefaults"));
        nint value = MacObjC.Send(defaults, MacObjC.Sel("stringForKey:"), MacObjC.NSString(MacCaptionActions.DefaultsKey));
        return MacCaptionActions.FromDefault(MacObjC.ToManaged(value));
    }

    /// <summary>Does what a double click on the caption is set to do. Main thread.</summary>
    public void CaptionDoubleClick()
    {
        if (InFullscreen)
            return;

        switch (ReadCaptionAction())
        {
            case MacCaptionAction.Zoom:
                MacObjC.SendVoid(window, MacObjC.Sel("performZoom:"), (nint)0);
                break;
            case MacCaptionAction.Miniaturize:
                MacObjC.SendVoid(window, MacObjC.Sel("performMiniaturize:"), (nint)0);
                break;
        }
    }

    /* Notifications */

    private static nint observer;

    private void Observe()
    {
        if (observer == 0)
        {
            var builder = new MacClassBuilder("HexWindowChromeObserver");
            builder.AddMethod("windowChanged:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&Notified, "v@:@");
            observer = builder.NewInstance();
        }

        nint centre = MacObjC.Send(MacObjC.Class("NSNotificationCenter"), MacObjC.Sel("defaultCenter"));
        foreach (string name in (ReadOnlySpan<string>)["NSWindowDidEnterFullScreenNotification", "NSWindowDidExitFullScreenNotification",
                     "NSWindowDidResizeNotification", "NSWindowDidEndLiveResizeNotification", "NSWindowDidChangeScreenNotification",
                     "NSWindowDidChangeBackingPropertiesNotification"])
            AddObserver(centre, observer, MacObjC.Sel("windowChanged:"), MacObjC.NSString(name), window);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Notified(nint self, nint selector, nint notification)
    {
        try
        {
            // SDL has its own say when it is told fullscreen is over, the mask is put back after it
            if (current is { } chrome)
            {
                chrome.lightsDirty = true;
                chrome.Maintain();
                chrome.Refresh();
            }
        }
        catch (Exception)
        {
            // a notification that couldn't be read is lights where AppKit put them
        }
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void AddObserver(nint centre, nint selector, nint observer, nint action, nint name, nint source);

    private static void AddObserver(nint centre, nint target, nint action, nint name, nint source) =>
        AddObserver(centre, MacObjC.Sel("addObserver:selector:name:object:"), target, action, name, source);
}
