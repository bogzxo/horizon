using System.Globalization;
using System.Numerics;

using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// What a file is by its name, the symbol a list of files draws in front of it (one of <see cref="UISymbols"/>') and the
/// colour of the skin's code it's drawn in, so a theme colours them too. The file dialog draws its rows with these, and a
/// program with kinds of its own (Hex's layouts) asks itself first and comes here for the rest.
/// </summary>
public static class UIFileKinds
{
    /// <summary>The colour a folder is drawn in, the blue a folder is on most desktops.</summary>
    public static readonly Vector4 FOLDER = new(0.38f, 0.62f, 1.0f, 1.0f);

    /// <summary>The symbol of a file by its extension, a plain file's for one nobody thought of.</summary>
    public static string IconOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".hor" => "file-code",
        ".png" or ".jpg" or ".jpeg" or ".aseprite" or ".ase" or ".gif" or ".bmp" or ".svg" or ".ico" or ".webp" => "file-media",
        ".ttf" or ".otf" or ".fnt" => "symbol-text",
        ".md" or ".markdown" => "markdown",
        ".txt" or ".log" => "book",
        ".json" or ".tmx" or ".tsx" or ".xml" or ".yaml" or ".yml" or ".toml" => "json",
        ".slang" or ".hlsl" or ".glsl" => "symbol-color",
        ".cs" or ".csproj" or ".sln" or ".props" or ".targets" => "symbol-class",
        ".py" or ".js" or ".ts" or ".mjs" or ".lua" or ".rs" or ".c" or ".h" or ".cpp" or ".hpp" or ".go" or ".d" or ".di" or ".java" or ".kt" or ".swift" => "code",
        ".ps1" or ".bash" or ".sh" or ".cmd" or ".bat" or ".zsh" or ".fish" => "terminal",
        ".zip" or ".7z" or ".gz" or ".tar" or ".rar" or ".xz" => "file-zip",
        ".pdf" => "file-pdf",
        ".exe" or ".dll" or ".pdb" or ".so" or ".dylib" or ".bin" or ".o" or ".obj" or ".lib" or ".a" => "file-binary",
        ".wav" or ".ogg" or ".mp3" or ".flac" => "unmute",
        _ => "file"
    };

    /// <summary>
    /// Whether a file reads as text by its name, anything but what's known to be pictures, sounds, fonts, archives and
    /// what gets built. A file of a kind nobody thought of is taken for text, a look inside it says for sure.
    /// </summary>
    public static bool IsText(string path) => IconOf(path) is not ("file-media" or "unmute" or "symbol-text" or "file-zip" or "file-pdf" or "file-binary")
        || Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>The colour of a file's symbol by its extension, out of the skin's colours for code. Null for the text's own, a file of nothing in particular.</summary>
    public static Vector4? ColourOf(string path, UISyntaxColors syntax) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".hor" => syntax.Declaration,
        ".cs" or ".csproj" or ".sln" or ".props" or ".targets" => syntax.Keyword,
        ".json" or ".yaml" or ".yml" or ".toml" or ".tmx" or ".tsx" or ".xml" => syntax.Call,
        ".md" or ".markdown" or ".txt" => syntax.Property,
        ".png" or ".jpg" or ".jpeg" or ".aseprite" or ".ase" or ".gif" or ".bmp" or ".svg" or ".ico" or ".webp" => syntax.Text,
        ".slang" or ".hlsl" or ".glsl" => syntax.Number,
        ".py" or ".js" or ".ts" or ".mjs" or ".lua" or ".rs" or ".c" or ".h" or ".cpp" or ".hpp" or ".go" or ".d" or ".di" or ".java" or ".kt" or ".swift" => syntax.Type,
        ".ps1" or ".bash" or ".sh" or ".cmd" or ".bat" or ".zsh" or ".fish" => syntax.Builtin,
        _ => null
    };

    /// <summary>How big a file is, the way a person says it ("12 KB", "3.4 MB").</summary>
    public static string SizeOf(long bytes) => bytes switch
    {
        < 1000 => string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes"),
        < 1000 * 1000 => string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (int)Math.Round(bytes / 1000.0))} KB"),
        < 1000L * 1000 * 1000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1000.0 * 1000.0):0.#} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1000.0 * 1000.0 * 1000.0):0.#} GB")
    };

    /// <summary>When something was changed, the way a person says it, today or yesterday and the time, or the day it was.</summary>
    public static string WhenOf(DateTime changed, DateTime now)
    {
        if (changed.Date == now.Date)
            return "Today, " + changed.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (changed.Date == now.Date.AddDays(-1))
            return "Yesterday, " + changed.ToString("HH:mm", CultureInfo.InvariantCulture);

        return changed.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }
}
