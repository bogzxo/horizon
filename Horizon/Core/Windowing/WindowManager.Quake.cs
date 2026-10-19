using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Horizon.Logging;

using Silk.NET.Maths;

namespace Horizon.Core;

// Two things a terminal wants of its window, a key that works whatever is in front (a hotkey, Windows', registered on a
// window without the system's frame, whose procedure is ours to hear WM_HOTKEY in) and the window slid down from the
// top of the screen and back up out of sight, the quake drop down. Both done on the window's thread, the slide a step
// every time round its loop, which looks at the system a thousand times a second while there is anything to slide.
public partial class WindowManager
{
    private readonly ConcurrentDictionary<int, int> hotKeysPressed = new();
    private int lastHotKey;

    /// <summary>
    /// Registers a key that is heard whatever window is in front, "Ctrl+`", "F12", "Ctrl+Alt+Space". Its id, which
    /// <see cref="TakeHotKey"/> asks after, or -1 where there are no such keys (anywhere but Windows, a window with the
    /// system's frame) or the key makes no sense. Another program holding the key already is logged once it is known.
    /// From any thread.
    /// </summary>
    public int RegisterHotKey(string shortcut)
    {
        if (!OperatingSystem.IsWindows() || !CustomFrame || !ParseHotKey(shortcut, out uint modifiers, out uint key))
            return -1;

        int id = Interlocked.Increment(ref lastHotKey);
        PostToWindow(() =>
        {
            if (frameHandle == 0 || !RegisterHotKey(frameHandle, id, modifiers | MOD_NOREPEAT, key))
                Log.Warning($"[Window] {shortcut} can't be had as a key for everywhere, something else has it");
        });
        return id;
    }

    /// <summary>A key registered with <see cref="RegisterHotKey"/> let go of. From any thread.</summary>
    public void UnregisterHotKey(int id)
    {
        if (id <= 0)
            return;
        hotKeysPressed.TryRemove(id, out _);
        PostToWindow(() =>
        {
            if (frameHandle != 0)
                UnregisterHotKey(frameHandle, id);
        });
    }

    /// <summary>Whether a registered key was pressed since it was last asked, once a press. From any thread.</summary>
    public bool TakeHotKey(int id)
    {
        while (hotKeysPressed.TryGetValue(id, out int count) && count > 0)
        {
            if (hotKeysPressed.TryUpdate(id, count - 1, count))
                return true;
        }

        return false;
    }

    // WM_HOTKEY, heard in the frame's procedure
    private void HotKeyHeard(int id)
    {
        hotKeysPressed.AddOrUpdate(id, 1, (_, count) => count + 1);
        Wake();
    }

    /// <summary>"Ctrl+Shift+F12" as Windows' modifiers and virtual key.</summary>
    public static bool ParseHotKey(string shortcut, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        string[] parts = shortcut.Split('+', StringSplitOptions.TrimEntries);
        // a plus as the key is written Ctrl++, which splits into an empty last part
        if (shortcut.EndsWith("++", StringComparison.Ordinal))
            parts = [.. parts[..^2], "+"];

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            bool last = i == parts.Length - 1;
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control" when !last: modifiers |= MOD_CONTROL; continue;
                case "alt" when !last: modifiers |= MOD_ALT; continue;
                case "shift" when !last: modifiers |= MOD_SHIFT; continue;
                case "win" or "super" when !last: modifiers |= MOD_WIN; continue;
            }

            if (!last)
                return false;
            key = VirtualKeyOf(part);
        }

        return key != 0;
    }

    private static uint VirtualKeyOf(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Length >= 2 && lower[0] == 'f' && int.TryParse(lower[1..], out int f) && f is >= 1 and <= 24)
            return (uint)(0x70 + f - 1);

        switch (lower)
        {
            case "space": return 0x20;
            case "tab": return 0x09;
            case "enter" or "return": return 0x0D;
            case "esc" or "escape": return 0x1B;
            case "pgup" or "pageup": return 0x21;
            case "pgdn" or "pagedown": return 0x22;
            case "end": return 0x23;
            case "home": return 0x24;
            case "left": return 0x25;
            case "up": return 0x26;
            case "right": return 0x27;
            case "down": return 0x28;
            case "ins" or "insert": return 0x2D;
            case "del" or "delete": return 0x2E;
            case "pause": return 0x13;
        }

        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (char.IsAsciiLetterOrDigit(c))
                return c;
            // the rest by what the keyboard has the character on, ` is under escape on one and elsewhere on another
            if (OperatingSystem.IsWindows())
            {
                short scanned = VkKeyScanW(name[0]);
                return scanned == -1 ? 0 : (uint)(scanned & 0xFF);
            }
        }

        return 0;
    }

    /* The drop down */

    private volatile bool dropped;
    private bool sliding, slidingDown;
    private long slideStarted;
    private float slideSeconds;
    private int slideFrom, slideTo, slideHeight;

    /// <summary>Whether the window is down from the top of the screen, as far as the last slide went.</summary>
    public bool IsDropped => dropped;

    /// <summary>
    /// The window slid down from the top of the screen it is on, as wide as that and a share of it high, shown and in
    /// front, or back up out of sight and hidden, in so many seconds (0 at once). From any thread.
    /// </summary>
    public void Drop(bool down, float share = 0.45f, float seconds = 0.18f) => PostToWindow(() => StartSlide(down, share, seconds));

    private void StartSlide(bool down, float share, float seconds)
    {
        if (down == dropped && !sliding)
            return;

        if (IsWindowMaximized())
            SetMaximized(false);

        var area = ScreenAreaOfWindow();
        int height = Math.Max(MinimumSize.Y, (int)(area.Size.Y * Math.Clamp(share, 0.1f, 1.0f)));
        if (down)
        {
            _window.Size = new Vector2D<int>(area.Size.X, height);
            _window.Position = new Vector2D<int>(area.Origin.X, area.Origin.Y - height);
            _window.IsVisible = true;
            Foreground();
        }

        slideHeight = height;
        slideFrom = _window.Position.Y;
        slideTo = down ? area.Origin.Y : area.Origin.Y - height;
        slidingDown = down;
        slideSeconds = Math.Max(0.0f, seconds);
        slideStarted = Stopwatch.GetTimestamp();
        sliding = true;
        dropped = down;
        StepSlide();
    }

    // A step of the slide, every time round the window's loop while it goes
    private void StepSlide()
    {
        if (!sliding)
            return;

        float t = slideSeconds <= 0.0f ? 1.0f : (float)Stopwatch.GetElapsedTime(slideStarted).TotalSeconds / slideSeconds;
        t = Math.Clamp(t, 0.0f, 1.0f);
        float eased = 1.0f - MathF.Pow(1.0f - t, 3.0f);
        int y = (int)MathF.Round(slideFrom + (slideTo - slideFrom) * eased);
        if (_window.Position.Y != y)
            _window.Position = new Vector2D<int>(_window.Position.X, y);

        if (t < 1.0f)
            return;

        sliding = false;
        if (!slidingDown)
            _window.IsVisible = false;
    }

    // The room on the screen the window is on, the taskbar's left out on Windows
    private Rectangle<int> ScreenAreaOfWindow()
    {
        if (OperatingSystem.IsWindows() && frameHandle != 0 && GetWindowRect(frameHandle, out NativeRect rect) && WorkAreaOf(rect) is { } work)
            return new Rectangle<int>(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
        return _window.Monitor?.Bounds ?? new Rectangle<int>(0, 0, _window.Size.X, _window.Size.Y);
    }

    private void Foreground()
    {
        if (OperatingSystem.IsWindows() && frameHandle != 0)
            SetForegroundWindow(frameHandle);
    }

    /// <summary>
    /// The window shown, put back from the taskbar if it was down there, and in front, for a program asked to by another
    /// start of itself (which lets it, AllowSetForegroundWindow, or Windows only flashes the button). From any thread.
    /// </summary>
    public void BringToFront() => PostToWindow(() =>
    {
        if (_window.WindowState == Silk.NET.Windowing.WindowState.Minimized)
            _window.WindowState = Silk.NET.Windowing.WindowState.Normal;
        _window.IsVisible = true;
        Foreground();
    });

    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000, WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char character);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);
}
