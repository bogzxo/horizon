using Horizon.Content;
using Horizon.Logging;
using Horizon.Graphics;


using Silk.NET.Core;

using Horizon.Core;

namespace Horizon.Platform.Desktop;

public sealed partial class DesktopWindow
{
    // The picture the window wants in its corner and on the taskbar, see WindowManagerConfiguration.WindowIcon
    private readonly string? windowIcon;

    // The icon's picture decoded on the pool as soon as the window is asked for, what TrySetIcon waits on. Decoded as the
    // window loaded it was a tenth of a second on the window's thread before the loading screen could show
    private Task<ImagePixels>? iconDecoded;

    private static string IconFile(string path) => Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

    // Starts the decode, from the constructor, for a picture that's there
    private void DecodeIcon()
    {
        if (string.IsNullOrEmpty(windowIcon) || OperatingSystem.IsMacOS())
            return;
        string file = IconFile(windowIcon);
        if (ContentFiles.Exists(file))
            iconDecoded = Task.Run(() => ImageFile.Load(file));
    }

    /// <summary>
    /// Puts the configured picture on the window. False when there isn't one or it can't be read, and the window keeps
    /// the system's. Window thread, once, as it loads.
    /// </summary>
    private bool TrySetIcon(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        string file = IconFile(path);
        if (!ContentFiles.Exists(file))
        {
            Log.Warning($"[{Name}] No window icon at '{file}', the window gets the default one.");
            return false;
        }

        try
        {
            // stb has no .ico decoder, a .png it is, decoded already on the pool where it could be
            var image = iconDecoded is { } decoding && path == windowIcon ? decoding.GetAwaiter().GetResult() : ImageFile.Load(file);
            var pixels = image.Pixels;

            _window.SetWindowIcon([new RawImage(image.Width, image.Height, pixels)]);
            return true;
        }
        catch (Exception e)
        {
            Log.Warning($"[{Name}] The window icon '{file}' couldn't be read ({e.Message}), the window gets the default one.");
            return false;
        }
    }
}
