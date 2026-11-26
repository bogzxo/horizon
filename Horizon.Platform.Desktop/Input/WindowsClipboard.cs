using System.Runtime.InteropServices;

namespace Horizon.Platform.Desktop;

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

    // Pure Win32 user32 clipboard APIs (no COM/OLE STA requirement). Retried with short progressive backoff while
    // another process briefly holds the clipboard, without sleeping after the final attempt.
    private const int TRIES = 8;

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
                    int most = (int)Math.Min((ulong)int.MaxValue, (ulong)GlobalSize(data)) / 2;
                    if (most <= 0)
                        return string.Empty;

                    unsafe
                    {
                        ReadOnlySpan<char> span = new((void*)text, most);
                        int end = span.IndexOf('\0');
                        return end >= 0 ? new string(span[..end]) : new string(span);
                    }
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
                if (!EmptyClipboard())
                    return;

                string text = value ?? string.Empty;
                IntPtr memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)((text.Length + 1) * 2));
                if (memory == IntPtr.Zero)
                    return;

                bool transferred = false;
                try
                {
                    IntPtr into = GlobalLock(memory);
                    if (into == IntPtr.Zero)
                        return;

                    try
                    {
                        unsafe
                        {
                            fixed (char* src = text)
                            {
                                nuint byteCount = (nuint)(text.Length * sizeof(char));
                                if (byteCount > 0)
                                    Buffer.MemoryCopy(src, (void*)into, byteCount, byteCount);
                                *((char*)into + text.Length) = '\0';
                            }
                        }
                    }
                    finally
                    {
                        GlobalUnlock(memory);
                    }

                    // The clipboard owns it once it's taken, it's ours to let go of only when it wasn't
                    transferred = SetClipboardData(CF_UNICODETEXT, memory) != IntPtr.Zero;
                }
                finally
                {
                    if (!transferred)
                        GlobalFree(memory);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }
    }

    // A picture as a bitmap with its header in front, the one Windows makes out of any other picture (a screenshot, an
    // image copied in a browser), and the PNG some programs put up beside it with its transparency kept
    private const uint CF_DIB = 8;
    private static uint pngFormat;

    /// <summary>
    /// The picture on the clipboard as the bytes of a PNG, null when there's none or it couldn't be read. A PNG put up
    /// as one is handed over as it is, a bitmap is turned into one.
    /// </summary>
    public static byte[]? Png()
    {
        if (pngFormat == 0)
            pngFormat = RegisterClipboardFormatW("PNG");
        bool png = pngFormat != 0 && IsClipboardFormatAvailable(pngFormat);
        if (!png && !IsClipboardFormatAvailable(CF_DIB))
            return null;
        if (!Open())
            return null;

        try
        {
            byte[]? bytes = Bytes(png ? pngFormat : CF_DIB);
            if (bytes is null)
                return null;
            return png ? bytes : PngOfBitmap(bytes);
        }
        finally
        {
            CloseClipboard();
        }
    }

    // What's on the clipboard in a format, as bytes. The clipboard is open
    private static byte[]? Bytes(uint format)
    {
        IntPtr data = GetClipboardData(format);
        if (data == IntPtr.Zero)
            return null;
        IntPtr at = GlobalLock(data);
        if (at == IntPtr.Zero)
            return null;
        try
        {
            long size = (long)GlobalSize(data);
            if (size <= 0 || size > int.MaxValue)
                return null;
            byte[] bytes = new byte[size];
            Marshal.Copy(at, bytes, 0, (int)size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(data);
        }
    }

    /// <summary>
    /// A bitmap with its header in front (a BITMAPINFOHEADER or the bigger ones after it) as a PNG, for 24 and 32 bits
    /// a pixel, plain or with masks, which is what a screenshot and a copied image are. Null for anything else.
    /// </summary>
    internal static byte[]? PngOfBitmap(byte[] dib)
    {
        if (dib.Length < 40)
            return null;
        int header = BitConverter.ToInt32(dib, 0), width = BitConverter.ToInt32(dib, 4), height = BitConverter.ToInt32(dib, 8);
        int bits = BitConverter.ToUInt16(dib, 14), compression = BitConverter.ToInt32(dib, 16);
        const int BI_RGB = 0, BI_BITFIELDS = 3;
        if (header < 40 || width <= 0 || height == 0 || (bits != 24 && bits != 32) || (compression != BI_RGB && compression != BI_BITFIELDS))
            return null;

        // A plain header with masks has them after it, the bigger headers have them inside
        int pixelsAt = header + (compression == BI_BITFIELDS && header == 40 ? 12 : 0);
        bool bottomUp = height > 0;
        int rows = Math.Abs(height), stride = (width * bits / 8 + 3) & ~3;
        if (pixelsAt + (long)stride * rows > dib.Length)
            return null;

        // A 32 bit picture whose alpha is nothing everywhere had none, and is drawn opaque rather than not at all
        bool alpha = false;
        if (bits == 32)
        {
            for (int y = 0; y < rows && !alpha; y++)
            {
                for (int x = 0, at = pixelsAt + y * stride + 3; x < width; x++, at += 4)
                {
                    if (dib[at] != 0)
                    {
                        alpha = true;
                        break;
                    }
                }
            }
        }

        byte[] rgba = new byte[width * rows * 4];
        for (int y = 0; y < rows; y++)
        {
            int from = pixelsAt + (bottomUp ? rows - 1 - y : y) * stride, to = y * width * 4;
            for (int x = 0; x < width; x++, to += 4)
            {
                int at = from + x * (bits / 8);
                rgba[to] = dib[at + 2];
                rgba[to + 1] = dib[at + 1];
                rgba[to + 2] = dib[at];
                rgba[to + 3] = bits == 32 && alpha ? dib[at + 3] : (byte)255;
            }
        }

        return Horizon.Graphics.ImageFile.EncodePng(rgba, width, rows);
    }

    /// <summary>Progressive retry delay in milliseconds after a failed <c>OpenClipboard</c> attempt, or 0 on the final attempt.</summary>
    internal static int RetryDelayMs(int attempt, int maxAttempts = TRIES)
    {
        if (attempt < 0 || attempt + 1 >= maxAttempts)
            return 0;

        return attempt switch
        {
            0 => 1,
            1 or 2 => 2,
            _ => 5
        };
    }

    // Opened for the thread asking, tried again briefly while another program holds it
    private static bool Open()
    {
        for (int i = 0; i < TRIES; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
                return true;

            int delay = RetryDelayMs(i, TRIES);
            if (delay > 0)
                Thread.Sleep(delay);
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

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string name);
}
