using TextMateSharp.Grammars;

namespace Horizon.UI.Scripting;

// Which language a file is, by more than its last extension. First what somebody set it to by hand (VS Code's
// Change Language Mode), the associations of the settings (files.associations, "*.weird" is python), its name for the
// files that have no extension worth the name (Dockerfile, Makefile, .gitignore), and its extension. A backup's suffix
// is taken off before the name and the extension are looked at, a.cs.orig and m.go.REMOTE_1234 are what they were
// before git or an editor left them behind.
public static partial class CodeLanguages
{
    /// <summary>The id a file set to plain text by hand has, which no grammar has.</summary>
    public const string PLAIN_TEXT = "plaintext";

    // What files were set to by hand, by their full path, a language id each
    private static readonly Dictionary<string, string> overrides = new(StringComparer.OrdinalIgnoreCase);

    // The associations as they were given, the most specific first, each with what it matches split out of its braces
    private static List<(string Glob, string LanguageId, string[] Patterns, bool OnPath)> associations = [];

    // What a language id (or an alias of one) is coloured with, null for one no grammar has
    private static readonly Dictionary<string, ICodeHighlighter?> byLanguageId = new(StringComparer.OrdinalIgnoreCase);

    // Files known by their name whatever their extension, each the languages to try in order, the first with a grammar wins
    private static readonly Dictionary<string, string[]> byName = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Dockerfile", ["dockerfile"] },
        { "Containerfile", ["dockerfile"] },
        { "Makefile", ["makefile"] },
        { "GNUmakefile", ["makefile"] },
        { "CMakeLists.txt", ["cmake"] },
        { "Jenkinsfile", ["groovy"] },
        { "Gemfile", ["ruby"] },
        { "Rakefile", ["ruby"] },
        { "Vagrantfile", ["ruby"] },
        { ".gitignore", ["ignore", "properties"] },
        { ".gitattributes", ["ignore", "properties"] },
        { ".dockerignore", ["ignore", "properties"] },
        { ".editorconfig", ["ini", "properties"] },
        { ".env", ["dotenv", "properties", "shellscript"] },
        { ".bashrc", ["shellscript"] },
        { ".bash_profile", ["shellscript"] },
        { ".zshrc", ["shellscript"] },
        { ".profile", ["shellscript"] }
    };

    // Extensions that are another language's files, MSBuild's projects are XML whatever the grammar's own list says
    private static readonly Dictionary<string, string[]> byOwnExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".csproj", ["xml"] },
        { ".fsproj", ["xml"] },
        { ".vbproj", ["xml"] },
        { ".props", ["xml"] },
        { ".targets", ["xml"] },
        // Components of the web frameworks are HTML round their script and styles, the nearest there's a grammar for
        { ".svelte", ["svelte", "html"] },
        { ".vue", ["vue", "html"] },
        // TOML is sections and keys the way an ini is, Cargo.toml and pyproject.toml read well enough as one
        { ".toml", ["toml", "ini", "properties"] },
        // What patch couldn't put in, and a patch, are diffs whatever they were made from
        { ".rej", ["diff"] },
        { ".patch", ["diff"] },
        // Terraform and HCL have no grammar of their own here, a grammar registered for them later is picked up
        { ".tf", ["terraform", "hcl"] },
        { ".tfvars", ["terraform", "hcl"] },
        { ".hcl", ["hcl", "terraform"] }
    };

    // The suffixes an editor, a merge or a patch leaves on a copy of a file. A .rej isn't one of them, it's the diff
    // patch couldn't put in, not a copy of the file
    private static readonly string[] backupSuffixes =
        [".orig", ".bak", ".tmp", ".old", ".swp", ".swo", ".copy", ".new", ".save", ".dist", ".sample"];

    // What git's mergetool calls the sides of a conflict, a number after each
    private static readonly string[] mergeSuffixes = [".BACKUP", ".BASE", ".LOCAL", ".REMOTE"];

    // The program a shebang names and the language it runs, its version left off first (python3 is python)
    private static readonly Dictionary<string, string> byInterpreter = new(StringComparer.OrdinalIgnoreCase)
    {
        { "bash", "shellscript" }, { "sh", "shellscript" }, { "zsh", "shellscript" }, { "ksh", "shellscript" }, { "dash", "shellscript" },
        { "python", "python" }, { "node", "javascript" }, { "nodejs", "javascript" }, { "deno", "typescript" }, { "bun", "javascript" },
        { "ruby", "ruby" }, { "perl", "perl" }, { "php", "php" }, { "lua", "lua" }, { "pwsh", "powershell" }, { "Rscript", "r" }
    };

    /// <summary>How many times what decides a file's language changed, an override, the associations or a registered extension, for whoever colours open files to know to look again.</summary>
    public static int Version { get; private set; }

    /// <summary>
    /// Sets the language of one file by hand, by its full path (any case), over everything else. Plain text
    /// (<see cref="PLAIN_TEXT"/>) makes it uncoloured, null lets it go back to being worked out.
    /// </summary>
    public static void SetOverride(string fullPath, string? languageId)
    {
        string key = FullPathOf(fullPath);
        lock (gate)
        {
            if (languageId is null)
                overrides.Remove(key);
            else
                overrides[key] = languageId;
            Version++;
        }
    }

    /// <summary>The language a file was set to by hand, null when it's worked out.</summary>
    public static string? OverrideOf(string fullPath)
    {
        lock (gate)
            return overrides.Count == 0 ? null : overrides.GetValueOrDefault(FullPathOf(fullPath));
    }

    /// <summary>
    /// The associations of a glob with a language, files.associations, every one there was replaced. The most specific
    /// wins whatever the order, a whole name over *.ext over anything wilder. A glob without a slash matches the file's
    /// name, one with a slash its path.
    /// </summary>
    public static void SetAssociations(IEnumerable<(string Glob, string LanguageId)> given)
    {
        var made = new List<(string Glob, string LanguageId, string[] Patterns, bool OnPath)>();
        foreach (var (glob, languageId) in given)
        {
            string trimmed = glob.Trim().Replace('\\', '/');
            if (trimmed.Length == 0 || languageId.Length == 0)
                continue;
            made.Add((trimmed, languageId, ExpandBraces(trimmed), trimmed.Contains('/')));
        }

        made.Sort((a, b) => Specificity(a.Glob) != Specificity(b.Glob) ? Specificity(b.Glob).CompareTo(Specificity(a.Glob)) : b.Glob.Length.CompareTo(a.Glob.Length));
        lock (gate)
        {
            associations = made;
            Version++;
        }
    }

    /// <summary>
    /// What a file would be coloured as with nothing set by hand, the associations, its name and its extension, for
    /// saying what Auto Detect would pick.
    /// </summary>
    public static ICodeHighlighter? Detect(string path, string? firstLine = null) => Resolve(path, firstLine, useOverride: false);

    /// <summary>
    /// A language by its id or any of its aliases (csharp, C#, python, hidl), null for one no grammar has. Plain text is
    /// null too, it has none.
    /// </summary>
    public static ICodeHighlighter? ForLanguageId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Equals(PLAIN_TEXT, StringComparison.OrdinalIgnoreCase))
            return null;
        if (id.Equals("hidl", StringComparison.OrdinalIgnoreCase))
            return Hidl;

        lock (gate)
        {
            if (byLanguageId.TryGetValue(id, out ICodeHighlighter? known))
                return known;

            ICodeHighlighter? found = null;
            if (id.Equals("d", StringComparison.OrdinalIgnoreCase))
                found = ForScopeLocked(D_SCOPE, "D");
            else if (id.Equals("toml", StringComparison.OrdinalIgnoreCase))
                found = ForScopeLocked(TOML_SCOPE, "TOML");
            else if (id.Equals("slang", StringComparison.OrdinalIgnoreCase))
                found = ForScopeLocked("source.hlsl", null);
            else if (LanguageLocked(id) is { } language && OptionsLocked().GetScopeByLanguageId(language.Id) is { } scope)
                found = ForScopeLocked(scope, language.Aliases is { Count: > 0 } aliases ? aliases[0] : language.Id);

            byLanguageId[id] = found;
            return found;
        }
    }

    /// <summary>The language id of what a file is coloured with, hidl, csharp, <see cref="PLAIN_TEXT"/> for nothing.</summary>
    public static string LanguageIdOf(ICodeHighlighter? highlighter)
    {
        switch (highlighter)
        {
            case null:
                return PLAIN_TEXT;
            case HidlHighlighter:
                return "hidl";
            case TextMateHighlighter textMate:
                string scope = textMate.ScopeName;
                if (scope == D_SCOPE)
                    return "d";
                if (scope == TOML_SCOPE)
                    return "toml";
                lock (gate)
                {
                    foreach (Language language in OptionsLocked().GetAvailableLanguages())
                    {
                        if (language.Extensions is { Count: > 0 } && OptionsLocked().GetScopeByLanguageId(language.Id) == scope)
                            return language.Id;
                    }
                }

                return scope;
            default:
                return highlighter.Name.ToLowerInvariant();
        }
    }

    /// <summary>
    /// A file's name or path with whatever backup suffixes were left on it taken off, every one of them, so
    /// x.ts.orig.bak is x.ts and m.go.REMOTE_1234 is m.go. One that is nothing but a suffix is left as it is.
    /// </summary>
    public static string WithoutBackupSuffix(string name)
    {
        int nameStart = name.LastIndexOfAny(['/', '\\']) + 1;
        string now = name;
        while (true)
        {
            string next = StripOnce(now);
            if (next.Length == now.Length || next.Length <= nameStart || next.Length == nameStart + 1 && next[^1] == '.')
                return now;
            now = next;
        }
    }

    // Helper method to take one backup suffix off the end, the same text back when there's none
    private static string StripOnce(string name)
    {
        if (name.EndsWith('~'))
        {
            // Emacs's numbered backups, name.~1~, and the plain name~
            int dot = name.LastIndexOf(".~", StringComparison.Ordinal);
            if (dot > 0 && name.Length - dot > 3 && name.AsSpan(dot + 2, name.Length - dot - 3).IndexOfAnyExceptInRange('0', '9') < 0)
                return name[..dot];
            return name[..^1];
        }

        foreach (string suffix in backupSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name[..^suffix.Length];
        }

        foreach (string side in mergeSuffixes)
        {
            int at = name.LastIndexOf(side, StringComparison.Ordinal);
            if (at < 0)
                continue;

            ReadOnlySpan<char> rest = name.AsSpan(at + side.Length);
            if (rest.Length == 0 || rest[0] == '_' && rest.Length > 1 && rest[1..].IndexOfAnyExceptInRange('0', '9') < 0)
                return name[..at];
        }

        return name;
    }

    // Helper method to work out a file's language in the order the summary of this file says
    private static ICodeHighlighter? Resolve(string path, string? firstLine, bool useOverride)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        string? associated = null;
        lock (gate)
        {
            if (useOverride && overrides.Count > 0 && overrides.TryGetValue(FullPathOf(path), out string? set))
            {
                if (set.Equals(PLAIN_TEXT, StringComparison.OrdinalIgnoreCase))
                    return null;
                associated = set;
            }

            if (associated is null && associations.Count > 0)
                associated = AssociationLocked(path) ?? (WithoutBackupSuffix(path) is var stripped && stripped.Length != path.Length ? AssociationLocked(stripped) : null);
        }

        if (associated is not null)
        {
            if (associated.Equals(PLAIN_TEXT, StringComparison.OrdinalIgnoreCase))
                return null;
            if (ForLanguageId(associated) is { } chosen)
                return chosen;
        }

        string name = Path.GetFileName(path);
        string bare = WithoutBackupSuffix(name);
        if (ByNameOrExtension(bare) is { } found)
            return found;

        // A file whose suffix is a language of its own when nothing was under it
        if (bare.Length != name.Length && ByNameOrExtension(name) is { } whole)
            return whole;

        return Path.GetExtension(bare).Length == 0 && firstLine is not null ? ByShebang(firstLine) : null;
    }

    // Helper method to look a file's name up, then its extension
    private static ICodeHighlighter? ByNameOrExtension(string name)
    {
        if (byName.TryGetValue(name, out string[]? ids) || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) && byName.TryGetValue(".env", out ids))
        {
            if (FirstOf(ids) is { } named)
                return named;
        }

        string extension = Path.GetExtension(name);
        if (byOwnExtension.TryGetValue(extension, out string[]? own) && FirstOf(own) is { } owned)
            return owned;
        return ForExtension(extension);
    }

    // Helper method to find the first of a list of languages that has a grammar
    private static ICodeHighlighter? FirstOf(string[] ids)
    {
        foreach (string id in ids)
        {
            if (ForLanguageId(id) is { } found)
                return found;
        }

        return null;
    }

    // Helper method to read the language a shebang runs, #!/usr/bin/env python3 or #!/bin/bash
    private static ICodeHighlighter? ByShebang(string firstLine)
    {
        if (!firstLine.StartsWith("#!", StringComparison.Ordinal))
            return null;

        string[] words = firstLine[2..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return null;

        string program = words[0][(words[0].LastIndexOf('/') + 1)..];
        if (program == "env")
            program = words.Skip(1).FirstOrDefault(word => !word.StartsWith('-') && !word.Contains('=')) ?? string.Empty;

        program = program.TrimEnd("0123456789.".ToCharArray());
        return byInterpreter.TryGetValue(program, out string? id) ? ForLanguageId(id) : null;
    }

    // Helper method to find the association a file falls under, the first of them in their order, the most specific. A
    // backup is tried again with its suffix off, a.weird.bak is what a.weird is
    private static string? AssociationLocked(string path)
    {
        string forward = path.Replace('\\', '/');
        string name = Path.GetFileName(forward);
        foreach (var (_, languageId, patterns, onPath) in associations)
        {
            foreach (string pattern in patterns)
            {
                if (onPath ? MatchesPath(pattern, forward) : Glob(pattern, name))
                    return languageId;
            }
        }

        return null;
    }

    // Whether a glob with a slash matches a path, the whole of it or the end of it from a folder on (src/*.x matches /work/src/b.x)
    private static bool MatchesPath(string pattern, string path)
    {
        if (Glob(pattern, path))
            return true;
        for (int at = path.IndexOf('/'); at >= 0; at = path.IndexOf('/', at + 1))
        {
            if (Glob(pattern, path.AsSpan(at + 1)))
                return true;
        }

        return false;
    }

    // How specific a glob is, a whole name over *.ext over anything else
    private static int Specificity(string glob)
    {
        if (glob.IndexOfAny(['*', '?', '[', '{']) < 0)
            return 3;
        if (glob.StartsWith("*.", StringComparison.Ordinal) && glob.AsSpan(2).IndexOfAny(['*', '?', '[', '{', '/']) < 0)
            return 2;
        return 1;
    }

    // Helper method to split a glob's braces into the globs they stand for, *.{js,ts} is *.js and *.ts
    private static string[] ExpandBraces(string glob)
    {
        int open = glob.IndexOf('{');
        int close = open < 0 ? -1 : glob.IndexOf('}', open);
        if (close < 0)
            return [glob];

        string before = glob[..open], after = glob[(close + 1)..];
        return [.. glob[(open + 1)..close].Split(',').SelectMany(part => ExpandBraces(before + part + after))];
    }

    // Whether a glob matches a text, any case. * is anything but a slash, ** anything at all, ? one character, [ab] one of them
    private static bool Glob(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        while (pattern.Length > 0)
        {
            char next = pattern[0];
            if (next == '*')
            {
                bool any = pattern.Length > 1 && pattern[1] == '*';
                ReadOnlySpan<char> rest = pattern[(any ? 2 : 1)..];

                // **/ matches no folder at all too
                if (any && rest.Length > 0 && rest[0] == '/' && Glob(rest[1..], text))
                    return true;

                for (int take = 0; take <= text.Length; take++)
                {
                    if (Glob(rest, text[take..]))
                        return true;
                    if (take < text.Length && !any && text[take] == '/')
                        return false;
                }

                return false;
            }

            if (text.Length == 0)
                return false;

            if (next == '[' && pattern.IndexOf(']') is var end and > 1)
            {
                ReadOnlySpan<char> set = pattern[1..end];
                bool negate = set[0] is '!' or '^';
                bool inside = false;
                for (int i = negate ? 1 : 0; i < set.Length; i++)
                {
                    if (i + 2 < set.Length && set[i + 1] == '-')
                    {
                        inside |= char.ToLowerInvariant(text[0]) >= char.ToLowerInvariant(set[i]) && char.ToLowerInvariant(text[0]) <= char.ToLowerInvariant(set[i + 2]);
                        i += 2;
                    }
                    else
                    {
                        inside |= char.ToLowerInvariant(text[0]) == char.ToLowerInvariant(set[i]);
                    }
                }

                if (inside == negate)
                    return false;
                pattern = pattern[(end + 1)..];
                text = text[1..];
                continue;
            }

            if (next == '?' ? text[0] == '/' : char.ToLowerInvariant(next) != char.ToLowerInvariant(text[0]))
                return false;
            pattern = pattern[1..];
            text = text[1..];
        }

        return text.Length == 0;
    }

    // Helper method to find a language by its id or an alias
    private static Language? LanguageLocked(string id)
    {
        Language? byAlias = null;
        foreach (Language language in OptionsLocked().GetAvailableLanguages())
        {
            if (language.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                return language;
            if (byAlias is null && language.Aliases is { } aliases && aliases.Any(alias => alias.Equals(id, StringComparison.OrdinalIgnoreCase)))
                byAlias = language;
        }

        return byAlias;
    }

    // Helper method to make a path the key overrides are kept by, the same whichever way round its slashes are
    private static string FullPathOf(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }
}
