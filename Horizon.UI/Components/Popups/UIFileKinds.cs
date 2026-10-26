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

    /// <summary>
    /// What a file is by its whole name when that says more than its extension, a Dockerfile, a Makefile, a .env or a
    /// .gitignore, null for the rest. Compose files are YAML by their extension but a container's thing first.
    /// </summary>
    private static string? KindByName(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower is "dockerfile" or "containerfile" || lower.StartsWith("dockerfile.", StringComparison.Ordinal) || lower.EndsWith(".dockerfile", StringComparison.Ordinal)
            || (lower.EndsWith(".yml", StringComparison.Ordinal) || lower.EndsWith(".yaml", StringComparison.Ordinal)) && (lower.StartsWith("docker-compose", StringComparison.Ordinal) || lower.StartsWith("compose.", StringComparison.Ordinal)))
            return "docker";
        if (lower is "makefile" or "gnumakefile" || lower.EndsWith(".mk", StringComparison.Ordinal))
            return "make";
        if (lower == ".env" || lower.StartsWith(".env.", StringComparison.Ordinal) || lower.EndsWith(".env", StringComparison.Ordinal))
            return "env";
        if (lower is ".gitignore" or ".gitattributes" or ".gitmodules" or ".gitkeep" or ".dockerignore")
            return "git";
        return null;
    }

    /// <summary>The symbol of a file by its name and extension, a plain file's for one nobody thought of.</summary>
    public static string IconOf(string path) => KindByName(Path.GetFileName(path)) switch
    {
        "docker" => "package",
        "make" => "tools",
        "env" => "key",
        "git" => "source-control",
        _ => IconOfExtension(Path.GetExtension(path).ToLowerInvariant())
    };

    // The symbol by the extension alone. Config files each have their own, YAML, TOML, XML and JSON all came out as braces
    private static string IconOfExtension(string extension) => extension switch
    {
        ".hor" => "file-code",
        ".png" or ".jpg" or ".jpeg" or ".aseprite" or ".ase" or ".gif" or ".bmp" or ".svg" or ".ico" or ".webp" => "file-media",
        ".ttf" or ".otf" or ".fnt" => "symbol-text",
        ".md" or ".markdown" => "markdown",
        ".txt" => "book",
        ".log" => "output",
        ".json" or ".tmx" or ".tsx" or ".jsonc" => "json",
        ".yaml" or ".yml" => "list-tree",
        ".toml" or ".ini" or ".cfg" or ".conf" or ".editorconfig" => "settings",
        ".xml" or ".xaml" or ".axaml" or ".resx" or ".plist" => "code",
        ".tf" or ".tfvars" or ".hcl" => "cloud",
        ".sql" => "database",
        ".vue" => "triangle-down",
        ".svelte" => "flame",
        ".patch" or ".diff" or ".rej" => "diff",
        ".slang" or ".hlsl" or ".glsl" => "symbol-color",
        // Code of every language the one file of code, its colour says which (a C# file had a class's icon, the one the
        // outline gives a class in it). What a project is made of gets a project's
        ".csproj" or ".sln" or ".props" or ".targets" => "project",
        ".cs" or ".py" or ".js" or ".ts" or ".mjs" or ".lua" or ".rs" or ".c" or ".h" or ".cpp" or ".hpp" or ".go" or ".d" or ".di" or ".java" or ".kt" or ".swift" => "file-code",
        ".ps1" or ".psm1" or ".psd1" => "terminal-powershell",
        ".bash" or ".sh" or ".cmd" or ".bat" or ".zsh" or ".fish" => "terminal",
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
    public static Vector4? ColourOf(string path, UISyntaxColors syntax) => KindByName(Path.GetFileName(path)) switch
    {
        "docker" => syntax.Constant,
        "make" => syntax.Builtin,
        "env" => syntax.Call,
        "git" => syntax.Comment,
        _ => ColourOfExtension(Path.GetExtension(path).ToLowerInvariant(), syntax)
    };

    private static Vector4? ColourOfExtension(string extension, UISyntaxColors syntax) => extension switch
    {
        ".hor" => syntax.Declaration,
        ".cs" or ".csproj" or ".sln" or ".props" or ".targets" => syntax.Keyword,
        ".json" or ".tmx" or ".tsx" or ".jsonc" => syntax.Call,
        ".yaml" or ".yml" => syntax.Directive,
        ".toml" or ".ini" or ".cfg" or ".conf" or ".editorconfig" => syntax.Number,
        ".xml" or ".xaml" or ".axaml" or ".resx" or ".plist" => syntax.Builtin,
        ".tf" or ".tfvars" or ".hcl" => syntax.Reference,
        ".sql" => syntax.Constant,
        ".vue" => syntax.Text,
        ".svelte" => syntax.Number,
        ".log" => syntax.Comment,
        ".patch" or ".diff" or ".rej" => syntax.Directive,
        ".md" or ".markdown" or ".txt" => syntax.Property,
        ".png" or ".jpg" or ".jpeg" or ".aseprite" or ".ase" or ".gif" or ".bmp" or ".svg" or ".ico" or ".webp" => syntax.Text,
        ".slang" or ".hlsl" or ".glsl" => syntax.Number,
        ".py" or ".js" or ".ts" or ".mjs" or ".lua" or ".rs" or ".c" or ".h" or ".cpp" or ".hpp" or ".go" or ".d" or ".di" or ".java" or ".kt" or ".swift" => syntax.Type,
        // PowerShell blue the way its own console is, the Unix shells the colour of a builtin
        ".ps1" or ".psm1" or ".psd1" => syntax.Constant,
        ".bash" or ".sh" or ".cmd" or ".bat" or ".zsh" or ".fish" => syntax.Builtin,
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
