using System.Globalization;

using Horizon.Logging;
using Horizon.Platform;

namespace Horizon.Rendering.Scaling;

/// <summary>
/// The render scale's HORIZON_* settings, which on a phone come in as intent extras the same way (see AndroidSettings).
/// <code>
/// HORIZON_RENDER_SCALE=0.75             a scale of its own, 75%, native (also off, full, 1), auto or 720p (the short edge), density
/// HORIZON_UPSCALER=nearest              cas, gsr1, fsr1, bilinear, nearest or custom
/// HORIZON_UPSCALER_SHARPNESS=0.4        0 to 1
/// HORIZON_RENDER_COMPOSITION=whole      native (the UI at full size, also overlay) or whole (the frame and its UI)
/// </code>
/// They win over the game's configuration, they're what a test or a person running it asks for. A value that
/// can't be made sense of is said in the log and left out, never guessed at.
/// </summary>
public static class RenderScaleEnvironment
{
    public const string SCALE = "HORIZON_RENDER_SCALE";
    public const string UPSCALER = "HORIZON_UPSCALER";
    public const string SHARPNESS = "HORIZON_UPSCALER_SHARPNESS";
    public const string COMPOSITION = "HORIZON_RENDER_COMPOSITION";

    /// <summary>
    /// What a window starts with, the configuration's settings (or the platform's, where it said none) with whatever
    /// the environment says on top.
    /// </summary>
    public static RenderScaleSettings Startup(RenderScaleSettings? configured, PlatformFeatures features, string name)
    {
        RenderScaleSettings settings = configured
            ?? ((features & PlatformFeatures.AutomaticRenderScale) != 0 ? RenderScaleSettings.Auto : RenderScaleSettings.Native);

        var problems = new List<string>();
        settings = Apply(settings, Environment.GetEnvironmentVariable, problems);
        foreach (string problem in problems)
            Log.Warning($"[{name}] {problem}");
        return settings;
    }

    /// <summary>Puts what the settings read say on top of some settings, saying in <paramref name="problems"/> what it couldn't read.</summary>
    public static RenderScaleSettings Apply(RenderScaleSettings settings, Func<string, string?> read, List<string> problems)
    {
        if (Value(read, SCALE) is { } scale)
        {
            if (TryScale(scale, out RenderScaleSettings scaled))
                settings = settings with { Policy = scaled.Policy, Scale = scaled.Scale, ShortEdge = scaled.ShortEdge };
            else
                problems.Add($"{SCALE}={scale} isn't a scale (a number from {RenderScaleSettings.SMALLEST} to 1, a percentage, native, auto, 720p and the like, or density), it's left out.");
        }

        if (Value(read, UPSCALER) is { } upscaler)
        {
            Upscaler? picked = upscaler.ToLowerInvariant() switch
            {
                "cas" or "fsr-cas" => Upscaler.Cas,
                "gsr1" or "gsr" or "sgsr" or "sgsr1" or "snapdragon" => Upscaler.Gsr1,
                "fsr1" or "fsr" or "easu" => Upscaler.Fsr1,
                "bilinear" or "linear" or "smooth" => Upscaler.Bilinear,
                "nearest" or "point" or "pixel" => Upscaler.Nearest,
                "custom" => Upscaler.Custom,
                _ => null
            };
            if (picked is { } chosen)
                settings = settings with { Upscaler = chosen };
            else
                problems.Add($"{UPSCALER}={upscaler} isn't an upscaler (cas, gsr1, fsr1, bilinear, nearest or custom), it's left out.");
        }

        if (Value(read, SHARPNESS) is { } sharpness)
        {
            if (float.TryParse(sharpness, NumberStyles.Float, CultureInfo.InvariantCulture, out float sharp) && float.IsFinite(sharp))
                settings = settings with { Sharpness = Math.Clamp(sharp, 0.0f, 1.0f) };
            else
                problems.Add($"{SHARPNESS}={sharpness} isn't a number from 0 to 1, it's left out.");
        }

        if (Value(read, COMPOSITION) is { } composition)
        {
            RenderComposition? picked = composition.ToLowerInvariant() switch
            {
                "native" or "overlay" or "native-ui" or "nativeoverlay" => RenderComposition.NativeOverlay,
                "whole" or "wholeframe" or "whole-frame" or "frame" => RenderComposition.WholeFrame,
                _ => null
            };
            if (picked is { } chosen)
                settings = settings with { Composition = chosen };
            else
                problems.Add($"{COMPOSITION}={composition} isn't a composition (native or whole), it's left out.");
        }

        return settings;
    }

    /// <summary>
    /// The settings as NAME=value lines, the same names and values <see cref="Apply"/> reads, for a program that
    /// keeps what the player picked in a file. A custom upscaler and the automatic policy's numbers are code, not
    /// text, and aren't in it.
    /// </summary>
    public static string Format(in RenderScaleSettings settings)
    {
        RenderScaleSettings s = settings.Sanitized();
        string scale = s.Policy switch
        {
            RenderScalePolicy.Automatic => $"{s.ShortEdge}p",
            RenderScalePolicy.Density => "density",
            RenderScalePolicy.Manual when s.Scale < 1.0f => s.Scale.ToString("0.###", CultureInfo.InvariantCulture),
            _ => "native"
        };

        return $"{SCALE}={scale}\n"
            + $"{UPSCALER}={s.Upscaler.ToString().ToLowerInvariant()}\n"
            + $"{SHARPNESS}={s.Sharpness.ToString("0.###", CultureInfo.InvariantCulture)}\n"
            + $"{COMPOSITION}={(s.Composition == RenderComposition.WholeFrame ? "whole" : "native")}\n";
    }

    /// <summary>Reads what <see cref="Format"/> wrote on top of some settings, saying in <paramref name="problems"/> what it couldn't read.</summary>
    public static RenderScaleSettings Parse(RenderScaleSettings settings, string text, List<string> problems)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int equals = line.IndexOf('=');
            if (line.Length == 0 || line[0] == '#' || equals <= 0)
                continue;
            values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        return Apply(settings, name => values.GetValueOrDefault(name), problems);
    }

    /// <summary>Whether any of the settings is in this process's environment, which wins over anything kept in a file.</summary>
    public static bool InEnvironment =>
        new[] { SCALE, UPSCALER, SHARPNESS, COMPOSITION }.Any(name => Environment.GetEnvironmentVariable(name) is { Length: > 0 });

    /// <summary>Reads a scale the way the setting takes one, true if it is one.</summary>
    public static bool TryScale(string text, out RenderScaleSettings settings)
    {
        settings = RenderScaleSettings.Native;
        string value = text.Trim().ToLowerInvariant();

        switch (value)
        {
            case "native" or "off" or "full" or "none":
                return true;
            case "auto" or "automatic":
                settings = RenderScaleSettings.Auto;
                return true;
            case "density":
                settings = new RenderScaleSettings { Policy = RenderScalePolicy.Density };
                return true;
        }

        // 720p, 1080p and so on, the scene's short edge in pixels
        if (value.EndsWith('p') && uint.TryParse(value[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out uint edge) && edge > 0)
        {
            settings = RenderScaleSettings.ShortEdgeOf(edge);
            return true;
        }

        bool percent = value.EndsWith('%');
        if (percent)
            value = value[..^1];

        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) || !float.IsFinite(number) || number <= 0.0f)
            return false;

        if (percent)
            number /= 100.0f;

        // Past 1 is drawing bigger than the window, which this doesn't do, and 75 could be 75% or a typo, so neither is guessed
        if (number > 1.0f)
            return false;

        settings = number >= 1.0f ? RenderScaleSettings.Native : RenderScaleSettings.At(number);
        return true;
    }

    private static string? Value(Func<string, string?> read, string name) => read(name) is { Length: > 0 } value ? value.Trim() : null;
}
