namespace Horizon.Input;

/// <summary>
/// The picture on the system's clipboard, for a box that takes pictures as well as text (a message to an agent with a
/// screenshot pasted in). The platform says how it's read, nothing on a platform that can't, and asking is never an error.
/// </summary>
public static class ClipboardPicture
{
    /// <summary>How the platform reads it, as the bytes of a PNG, null for none. Set once by the platform.</summary>
    public static Func<byte[]?>? Reader { get; set; }

    /// <summary>The picture on the clipboard as a PNG, null when there's none or it couldn't be read. On the thread asking.</summary>
    public static byte[]? Png()
    {
        try
        {
            return Reader?.Invoke();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
