using System.Globalization;
using System.Text;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Logging;

namespace Horizon.Hex;

/// <summary>
/// What the editor is set to and keeps from one run to the next, the look of itself, how its panels were dragged
/// and how it helps with lining things up. Kept next to the executable as a file anybody can read, hex_settings.hor.
/// The settings dialog (File, Settings) is where they are changed, the panels save their own sizes as they are let
/// go of. A file that is missing or makes no sense is everything at what it comes as.
/// </summary>
internal sealed class HexSettings
{
    public const string FILE = "hex_settings.hor";

    /// <summary>The grids there are to snap to, 0 being none.</summary>
    public static readonly float[] Grids = [0.0f, 2.0f, 4.0f, 8.0f, 16.0f];

    /// <summary>Which theme of the flat skin the editor itself is drawn in.</summary>
    public string EditorTheme = "dark";

    /// <summary>How wide the lists on the left and the properties on the right were dragged, 0 for as their layout has them.</summary>
    public float LeftWidth, RightWidth;

    /// <summary>How tall the canvas and the list of layers were dragged, 0 for what the editor works out by itself.</summary>
    public float CanvasHeight, LayersHeight;

    /// <summary>What positions and sizes land on a multiple of when something is dragged in the canvas, 0 for anywhere.</summary>
    public float Snap;

    /// <summary>Whether what is selected in the canvas can be dragged bigger by its edges.</summary>
    public bool CanvasHandles = true;

    /// <summary>Whether the editor starts with the layout that was worked on last.</summary>
    public bool ReopenLast;

    private readonly string? path;

    /// <param name="path">Where it is kept, null for settings that are never read or written (the editor testing itself).</param>
    public HexSettings(string? path)
    {
        this.path = path;
        if (path is null || !File.Exists(path))
            return;

        try
        {
            HIDLRuntime runtime = new() { Output = null };
            var (success, message) = runtime.Evaluate(File.ReadAllText(path));
            if (!success || runtime.UserScope.Lookup("settings") is not ObjectValue settings)
                throw new Exception(success ? "it has to declare an object called 'settings'" : message);

            var values = settings.Properties;
            if (values.TryGetValue("editor_theme", out var theme) && theme is StringValue themed) EditorTheme = themed.Value;
            LeftWidth = Number(values, "left_width", LeftWidth);
            RightWidth = Number(values, "right_width", RightWidth);
            CanvasHeight = Number(values, "canvas_height", CanvasHeight);
            LayersHeight = Number(values, "layers_height", LayersHeight);
            Snap = Number(values, "snap", Snap);
            if (values.TryGetValue("canvas_handles", out var handles) && handles is BooleanValue handled) CanvasHandles = handled.Value;
            if (values.TryGetValue("reopen_last", out var reopen) && reopen is BooleanValue reopened) ReopenLast = reopened.Value;
        }
        catch (Exception e)
        {
            Log.Warning($"[Hex] {path} makes no sense and is left alone, the editor comes up as it is made: {e.Message}");
        }
    }

    private static float Number(Dictionary<string, IRuntimeValue> values, string key, float otherwise) =>
        values.TryGetValue(key, out var value) && value is NumberValue number ? number.Value : otherwise;

    /// <summary>Writes the settings to their file. Says what went wrong if they couldn't be, null if they could.</summary>
    public string? Save()
    {
        if (path is null)
            return null;

        static string Write(float value) => value.ToString(CultureInfo.InvariantCulture);
        static string Flag(bool value) => value ? "true" : "false";

        var text = new StringBuilder();
        text.AppendLine("// Written by the editor, change these in its settings (File, Settings) instead.");
        text.AppendLine("let settings = {");
        text.AppendLine($"    editor_theme: \"{EditorTheme}\",");
        text.AppendLine($"    left_width: {Write(LeftWidth)},");
        text.AppendLine($"    right_width: {Write(RightWidth)},");
        text.AppendLine($"    canvas_height: {Write(CanvasHeight)},");
        text.AppendLine($"    layers_height: {Write(LayersHeight)},");
        text.AppendLine($"    snap: {Write(Snap)},");
        text.AppendLine($"    canvas_handles: {Flag(CanvasHandles)},");
        text.AppendLine($"    reopen_last: {Flag(ReopenLast)}");
        text.AppendLine("};");

        try
        {
            File.WriteAllText(path, text.ToString());
            return null;
        }
        catch (Exception e)
        {
            return $"couldn't keep the settings for next time: {e.Message}";
        }
    }
}
