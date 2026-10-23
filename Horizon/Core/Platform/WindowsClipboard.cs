using System.Runtime.InteropServices;

namespace Horizon.Core.Platform;

/// <summary>
/// The clipboard as Windows has it, text in and out, asked for straight. GLFW's said it failed to convert the clipboard
/// to a string, element not found, for code copied out of a browser, and Silk threw that from inside GLFW's own call, where
/// nothing could catch it. A debug Hex pasted nothing and a published one fell over. The clipboard is tried again for a
/// moment while another program has it open, and anything that isn't there is nothing rather than an error.
/// </summary>
internal static partial class WindowsClipboard
{
    // The text of the clipboard as UTF-16, the one Windows makes out of any other text
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    // How many times it's asked for while somebody else has it open, and how long apart, a tenth of a second at most
    private const int TRIES = 10;
    private const int APART = 10;

    /// <summary>The text on the clipboard, empty for none or for one that wouldn't open.</summary>
    public static string Text
    {
        get
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT) || !Open())
                return string.Empty;

            try
            {
                IntPtr data = GetClipboardData(CF_UNICODETEXT);
                if (data == IntPtr.Zero)
                    return string.Empty;

                IntPtr text = GlobalLock(data);
                if (text == IntPtr.Zero)
                    return string.Empty;

                try
                {
                    // To its end or the size of the memory, whichever comes first, a program that left no end on it
                    // isn't read past what it gave
                    int most = (int)Math.Min(int.MaxValue, (long)GlobalSize(data)) / 2;
                    string said = Marshal.PtrToStringUni(text, most);
                    int end = said.IndexOf('\0');
                    return end >= 0 ? said[..end] : said;
                }
                finally
                {
                    GlobalUnlock(data);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }
        set
        {
            if (!Open())
                return;

            try
            {
                EmptyClipboard();
                string text = value ?? string.Empty;
                IntPtr memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)((text.Length + 1) * 2));
                if (memory == IntPtr.Zero)
                    return;

                IntPtr into = GlobalLock(memory);
                if (into == IntPtr.Zero)
                {
                    GlobalFree(memory);
                    return;
                }

                Marshal.Copy(text.ToCharArray(), 0, into, text.Length);
                Marshal.WriteInt16(into, text.Length * 2, 0);
                GlobalUnlock(memory);

                // The clipboard owns it once it's taken, it's ours to let go of only when it wasn't
                if (SetClipboardData(CF_UNICODETEXT, memory) == IntPtr.Zero)
                    GlobalFree(memory);
            }
            finally
            {
                CloseClipboard();
            }
        }
    }

    // Opened for the thread asking, tried again for a moment while another program has it
    private static bool Open()
    {
        for (int i = 0; i < TRIES; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
                return true;
            Thread.Sleep(APART);
        }

        return false;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr GetClipboardData(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetClipboardData(uint format, IntPtr memory);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalLock(IntPtr memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr memory);

    [LibraryImport("kernel32.dll")]
    private static partial nuint GlobalSize(IntPtr memory);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalFree(IntPtr memory);
}
