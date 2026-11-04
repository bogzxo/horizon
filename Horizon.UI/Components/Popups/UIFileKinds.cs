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

    /// <summary>Well-known manifests and dotfiles whose names identify their language better than their suffix does.</summary>
    private static string? IconByName(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower is "package.json" or "package-lock.json" or "npm-shrinkwrap.json" or "pnpm-lock.yaml" or "yarn.lock"
            || lower.StartsWith("tsconfig.", StringComparison.Ordinal) || lower.StartsWith("jsconfig.", StringComparison.Ordinal))
            return "filetype_nodejs";
        if (lower is "cargo.toml" or "cargo.lock") return "filetype_rust";
        if (lower is "go.mod" or "go.sum" or "go.work" or "go.work.sum") return "filetype_go";
        if (lower is "pyproject.toml" or "pipfile" or "pipfile.lock" or "requirements.txt" or "poetry.lock") return "filetype_python";
        if (lower is "gemfile" or "gemfile.lock") return "filetype_ruby";
        if (lower is "composer.json" or "composer.lock") return "filetype_php";
        if (lower is "dockerfile" or "containerfile" || lower.StartsWith("dockerfile.", StringComparison.Ordinal) || lower.EndsWith(".dockerfile", StringComparison.Ordinal)
            || (lower.EndsWith(".yml", StringComparison.Ordinal) || lower.EndsWith(".yaml", StringComparison.Ordinal)) && (lower.StartsWith("docker-compose", StringComparison.Ordinal) || lower.StartsWith("compose.", StringComparison.Ordinal)))
            return "filetype_docker";
        if (lower is ".gitignore" or ".gitattributes" or ".gitmodules" or ".gitkeep") return "filetype_git";
        return null;
    }

    /// <summary>Compound suffixes whose tail alone loses useful file-kind context.</summary>
    private static readonly (string Suffix, string Icon)[] CompoundIcons =
    [
        (".module.scss", "filetype_sass"), (".module.sass", "filetype_sass"), (".module.css", "filetype_css"),
        (".test.tsx", "filetype_react"), (".spec.tsx", "filetype_react"),
        (".test.ts", "filetype_typescript"), (".spec.ts", "filetype_typescript"), (".d.ts", "filetype_typescript"),
        (".test.js", "filetype_javascript"), (".spec.js", "filetype_javascript")
    ];

    private static string? IconByCompoundSuffix(string name)
    {
        string lower = name.ToLowerInvariant();
        foreach (var (suffix, icon) in CompoundIcons)
            if (lower.EndsWith(suffix, StringComparison.Ordinal))
                return icon;
        return null;
    }

    // Longer aliases are useful for nonstandard extensions and are also the only vocabulary used for fuzzy matching.
    // Short extensions such as .c, .go, .ts and .rs are deliberately excluded from similarity guesses.
    private static readonly (string Alias, string Icon)[] LongExtensionAliases =
    [
        ("python", "filetype_python"), ("python3", "filetype_python"), ("javascript", "filetype_javascript"),
        ("typescript", "filetype_typescript"), ("csharp", "filetype_csharp"), ("cplusplus", "filetype_cpp"),
        ("kotlin", "filetype_kotlin"), ("markdown", "markdown"), ("powershell", "filetype_powershell"),
        ("docker", "filetype_docker"), ("yaml", "filetype_yaml"), ("svelte", "filetype_svelte"),
        ("clojure", "filetype_clojure"), ("elixir", "filetype_elixir"), ("erlang", "filetype_erlang"),
        ("haskell", "filetype_haskell"), ("golang", "filetype_go"), ("rust", "filetype_rust"),
        ("swift", "filetype_swift"), ("ruby", "filetype_ruby"), ("scala", "filetype_scala"),
        ("perl", "filetype_perl"), ("fsharp", "filetype_fsharp"), ("postgresql", "filetype_postgresql"),
        ("mongodb", "filetype_mongodb"), ("mysql", "filetype_mysql"), ("sqlite", "filetype_sqlite")
    ];

    private static string? IconByLongExtensionAlias(string extension)
    {
        string name = extension.TrimStart('.');
        foreach (var (alias, icon) in LongExtensionAliases)
            if (name.Equals(alias, StringComparison.OrdinalIgnoreCase))
                return icon;

        if (name.Length < 5)
            return null;

        string? best = null;
        int bestDistance = 2;
        bool tied = false;
        foreach (var (alias, icon) in LongExtensionAliases)
        {
            if (alias.Length < 5 || Math.Abs(name.Length - alias.Length) > 1 || !WithinOneEdit(name, alias))
                continue;
            int distance = name.Length == alias.Length ? (name.Equals(alias, StringComparison.OrdinalIgnoreCase) ? 0 : 1) : 1;
            if (distance < bestDistance)
            {
                best = icon;
                bestDistance = distance;
                tied = false;
            }
            else if (distance == bestDistance && !string.Equals(best, icon, StringComparison.Ordinal))
            {
                tied = true;
            }
        }
        return tied ? null : best;
    }

    private static bool WithinOneEdit(string left, string right)
    {
        int longer = left.Length >= right.Length ? left.Length : right.Length;
        int i = 0, j = 0, edits = 0;
        while (i < left.Length && j < right.Length)
        {
            if (char.ToLowerInvariant(left[i]) == char.ToLowerInvariant(right[j]))
            {
                i++;
                j++;
                continue;
            }

            if (++edits > 1)
                return false;
            if (left.Length > right.Length) i++;
            else if (right.Length > left.Length) j++;
            else { i++; j++; }
        }

        edits += left.Length - i + right.Length - j;
        return edits <= 1 && longer >= 5;
    }

    /// <summary>The symbol of a file by its name and extension, a plain file's for one nobody thought of.</summary>
    public static string IconOf(string path)
    {
        string name = Path.GetFileName(path);
        string exactExtension = IconOfExtension(Path.GetExtension(path).ToLowerInvariant());
        return IconByName(name) ?? IconByCompoundSuffix(name) ?? (KindByName(name) switch
        {
            "docker" => "filetype_docker",
            "make" => "tools",
            "env" => "key",
            "git" => "filetype_git",
            _ => exactExtension
        });
    }

    // The symbol by the extension alone. Config files each have their own, YAML, TOML, XML and JSON all came out as braces
    private static string IconOfExtension(string extension) => extension switch
    {
        ".hor" or ".d" or ".di" => "file-code",
        ".h" => "filetype_c",
        ".png" or ".jpg" or ".jpeg" or ".aseprite" or ".ase" or ".gif" or ".bmp" or ".svg" or ".ico" or ".webp" => "file-media",
        ".ttf" or ".otf" or ".fnt" => "symbol-text",
        ".md" or ".markdown" => "markdown",
        ".txt" => "book",
        ".log" => "output",
        ".json" or ".tmx" or ".jsonc" => "json",
        ".yaml" or ".yml" => "filetype_yaml",
        ".toml" or ".ini" or ".cfg" or ".conf" or ".editorconfig" => "settings",
        ".xml" or ".xaml" or ".axaml" or ".resx" or ".plist" => "code",
        ".tf" or ".tfvars" or ".hcl" => "cloud",
        ".sql" => "database",
        ".vue" => "filetype_vue",
        ".svelte" => "filetype_svelte",
        ".patch" or ".diff" or ".rej" => "diff",
        ".slang" or ".hlsl" or ".glsl" => "symbol-color",
        // Code of every language the one file of code, its colour says which (a C# file had a class's icon, the one the
        // outline gives a class in it). What a project is made of gets a project's
        ".csproj" or ".sln" or ".props" or ".targets" => "project",
        ".cs" or ".csx" => "filetype_csharp",
        ".py" or ".pyw" or ".pyi" => "filetype_python",
        ".js" or ".mjs" or ".cjs" => "filetype_javascript",
        ".jsx" => "filetype_react",
        ".ts" or ".mts" or ".cts" => "filetype_typescript",
        ".tsx" => "filetype_react",
        ".lua" => "filetype_lua",
        ".rs" => "filetype_rust",
        ".c" => "filetype_c",
        ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hh" or ".hxx" => "filetype_cpp",
        ".go" => "filetype_go",
        ".java" => "filetype_java",
        ".kt" or ".kts" => "filetype_kotlin",
        ".swift" => "filetype_swift",
        ".rb" => "filetype_ruby",
        ".php" => "filetype_php",
        ".dart" => "filetype_dart",
        ".pl" => "filetype_perl",
        ".hs" => "filetype_haskell",
        ".scala" => "filetype_scala",
        ".r" => "filetype_r",
        ".ex" or ".exs" => "filetype_elixir",
        ".erl" or ".hrl" => "filetype_erlang",
        ".clj" or ".cljs" or ".cljc" => "filetype_clojure",
        ".fs" or ".fsx" => "filetype_fsharp",
        ".ps1" or ".psm1" or ".psd1" => "filetype_powershell",
        ".bash" or ".sh" or ".zsh" or ".fish" => "filetype_bash",
        ".cmd" or ".bat" => "terminal",
        ".html" or ".htm" => "filetype_html",
        ".css" => "filetype_css",
        ".scss" or ".sass" => "filetype_sass",
        ".zip" or ".7z" or ".gz" or ".tar" or ".rar" or ".xz" => "file-zip",
        ".pdf" => "file-pdf",
        ".exe" or ".dll" or ".pdb" or ".so" or ".dylib" or ".bin" or ".o" or ".obj" or ".lib" or ".a" => "file-binary",
        ".wav" or ".ogg" or ".mp3" or ".flac" => "unmute",
        _ => IconByLongExtensionAlias(extension) ?? "file"
    };

    /// <summary>
    /// Whether a file reads as text by its name, anything but what's known to be pictures, sounds, fonts, archives and
    /// what gets built. A file of a kind nobody thought of is taken for text, a look inside it says for sure.
    /// </summary>
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".aseprite", ".ase", ".avif", ".heic",
        ".ttf", ".otf", ".fnt", ".woff", ".woff2", ".wav", ".ogg", ".mp3", ".flac", ".mp4", ".mov", ".avi", ".mkv",
        ".zip", ".7z", ".gz", ".tar", ".rar", ".xz", ".bz2", ".pdf",
        ".exe", ".dll", ".pdb", ".so", ".dylib", ".bin", ".o", ".obj", ".lib", ".a", ".class", ".jar", ".sqlite", ".db"
    };

    /// <summary>Whether a file reads as text, based on known binary formats rather than its icon name.</summary>
    public static bool IsText(string path) => Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase)
        || !BinaryExtensions.Contains(Path.GetExtension(path));

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
        ".json" or ".tmx" or ".jsonc" => syntax.Call,
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
        ".py" or ".pyw" or ".pyi" or ".js" or ".mjs" or ".cjs" or ".jsx" or ".ts" or ".mts" or ".cts" or ".tsx" or ".lua" or ".rs" or ".c" or ".h" or ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hh" or ".hxx" or ".go" or ".d" or ".di" or ".java" or ".kt" or ".kts" or ".swift" or ".rb" or ".php" or ".dart" or ".pl" or ".hs" or ".scala" or ".r" or ".ex" or ".exs" or ".erl" or ".hrl" or ".clj" or ".cljs" or ".cljc" or ".fs" or ".fsx" or ".html" or ".htm" or ".css" or ".scss" or ".sass" => syntax.Type,
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
