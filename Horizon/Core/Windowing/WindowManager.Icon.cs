using Horizon.Logging;
using Horizon.Graphics;


using Silk.NET.Core;

namespace Horizon.Core;

public partial class WindowManager
{
    // The picture the window wants in its corner and on the taskbar, see WindowManagerConfiguration.WindowIcon
    private readonly string? windowIcon;

    /// <summary>
    /// Puts the configured picture on the window. False when there isn't one or it can't be read, and the window keeps
    /// the system's. Window thread, once, as it loads.
    /// </summary>
    private bool TrySetIcon(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        string file = Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        if (!File.Exists(file))
        {
            Log.Warning($"[{Name}] No window icon at '{file}', the window gets the default one.");
            return false;
        }

        try
        {
            // stb has no .ico decoder, a .png it is
            var image = ImageFile.Load(file);
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
