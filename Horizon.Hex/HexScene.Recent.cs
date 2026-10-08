using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// The layouts that were worked on last, kept in a file next to the editor between runs.
internal sealed partial class HexScene
{
    /* The layouts that were worked on last */

    private static string RecentPath => Path.Combine(AppContext.BaseDirectory, RECENT_FILE);

    // The editor testing itself or checking layouts starts out the same every time, and leaves nothing behind
    private bool KeepsRecent => !options.SelfTest && options.Check.Length == 0;

    private List<string> ReadRecent()
    {
        if (!KeepsRecent || !File.Exists(RecentPath))
            return [];

        try
        {
            // Only the ones that are still there
            return [.. File.ReadAllLines(RecentPath).Where(line => line.Length > 0 && File.Exists(line)).Distinct().Take(MAX_RECENT)];
        }
        catch (Exception)
        {
            // Not knowing what was open last time is no reason not to start
            return [];
        }
    }

    /// <summary>Helper to note a layout as the one that was worked on last, which puts it at the top of the list.</summary>
    private void Remember(string path)
    {
        if (!KeepsRecent || SameDirectory(path, EDITOR_LAYOUT))
            return;

        string full = Path.GetFullPath(path);
        recent.RemoveAll(known => string.Equals(known, full, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, full);

        if (recent.Count > MAX_RECENT)
            recent.RemoveRange(MAX_RECENT, recent.Count - MAX_RECENT);

        WriteRecent();
    }

    private void ForgetRecent()
    {
        recent.Clear();
        WriteRecent();
    }

    private void WriteRecent()
    {
        RebuildFileMenu();

        try
        {
            File.WriteAllLines(RecentPath, recent);
        }
        catch (Exception e)
        {
            Say($"couldn't keep the list of layouts for next time: {e.Message}", error: true);
        }
    }
}
