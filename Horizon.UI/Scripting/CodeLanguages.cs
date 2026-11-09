using Horizon.HIDL;
using Horizon.UI.Skinning;

using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace Horizon.UI.Scripting;

/// <summary>A language the code editor colours, what it's called, its id and the files of it, see <see cref="CodeLanguages.Languages"/>.</summary>
public sealed record CodeLanguage(string Name, string Id, IReadOnlyList<string> Extensions);

/// <summary>
/// The languages the code editor colours, by the extension of the file. Every language VS Code ships a grammar for
/// comes out of TextMateSharp.Grammars (its grammars are VS Code's own, C#, Python, JSON, Markdown, HLSL and sixty
/// more), and HIDL is one more grammar in the same registry, the language's own (<see cref="HIDLGrammar"/>), with
/// what its lexer knows laid over it (<see cref="HidlHighlighter"/>). Nothing here is made until a file is opened,
/// the registry reads every grammar's package.json the first time it is asked, a grammar is read the first time a
/// file of it is, and each is kept for the run.
/// <para>
/// A Slang shader (.slang) is coloured with the HLSL grammar, which is the language it grew out of, VS Code has no
/// grammar of Slang's own. <see cref="Register"/> is how a game or a tool gives an extension of its own to a
/// grammar, Hex's workspace file is HIDL for one.
/// </para>
/// <para>
/// D (.d and .di) has a grammar of the engine's own, Scripting/Grammars/d.tmLanguage.json, VS Code ships none and
/// neither does TextMateSharp. A grammar of that folder is one more scope in the registry, by its scopeName. TOML
/// (.toml, Cargo's and the rest) has one there too for the same reason.
/// </para>
/// </summary>
public static partial class CodeLanguages
{
    private static readonly Lock gate = new();
    private static Options? options;
    private static Registry? registry;

    // What an extension is coloured as, null for one looked up before that nothing has a grammar for
    private static readonly Dictionary<string, ICodeHighlighter?> byExtension = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ICodeHighlighter> byScope = new(StringComparer.Ordinal);

    // Extensions that aren't in any grammar's package.json, the engine's own and whatever Register adds
    private static readonly Dictionary<string, string> ownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".hor", HIDLGrammar.ScopeName },
        { ".jab", HIDLGrammar.ScopeName },
        { ".hex-workspace", HIDLGrammar.ScopeName },
        { ".slang", "source.hlsl" },
        { ".d", D_SCOPE },
        { ".di", D_SCOPE },
        { ".toml", TOML_SCOPE }
    };

    // The grammars of the engine's own by their scope, and the resource each is in (Scripting/Grammars)
    private const string D_SCOPE = "source.d";
    private const string TOML_SCOPE = "source.toml";
    private static readonly Dictionary<string, string> ownGrammars = new(StringComparer.Ordinal)
    {
        { D_SCOPE, "Horizon.UI.Grammars.d.tmLanguage.json" },
        { TOML_SCOPE, "Horizon.UI.Grammars.toml.tmLanguage.json" }
    };

    // The colours a language has of its own over the skin's, by its grammar's scope, for a dark field and a light one
    private static readonly Dictionary<string, (UISyntaxColors Dark, UISyntaxColors Light)> ownColors = new(StringComparer.Ordinal);

    /// <summary>
    /// Gives a language colours of its own (source.cs in Visual Studio's, <see cref="UISyntaxColors.VisualStudioDark"/>),
    /// the dark ones on a skin whose field is dark and the light ones on a light one. Every other language keeps the skin's.
    /// </summary>
    public static void SetColors(string scopeName, UISyntaxColors dark, UISyntaxColors light)
    {
        lock (gate)
            ownColors[scopeName] = (dark, light);
    }

    /// <summary>Takes the colours a language was given of its own away, it's the skin's again.</summary>
    public static void ForgetColors(string scopeName)
    {
        lock (gate)
            ownColors.Remove(scopeName);
    }

    /// <summary>The colours code of a language is painted in on a skin's palette, its own when it was given some, the palette's otherwise.</summary>
    public static UISyntaxColors ColorsFor(ICodeHighlighter? highlighter, UIPalette palette)
    {
        string? scope = highlighter switch
        {
            TextMateHighlighter textMate => textMate.ScopeName,
            HidlHighlighter => HIDLGrammar.ScopeName,
            _ => null
        };

        lock (gate)
        {
            if (scope is null || ownColors.Count == 0 || !ownColors.TryGetValue(scope, out var own))
                return palette.Syntax;
            return palette.LightField ? own.Light : own.Dark;
        }
    }

    /// <summary>HIDL, through its grammar and its lexer. What a code editor colours with unless told otherwise.</summary>
    public static ICodeHighlighter Hidl => HidlHighlighter.Instance;

    /// <summary>Says that files with an extension (".hor") are coloured with a grammar's scope ("source.hidl").</summary>
    public static void Register(string extension, string scopeName)
    {
        lock (gate)
        {
            ownExtensions[extension] = scopeName;
            byExtension.Remove(extension);
            Version++;
        }
    }

    /// <summary>
    /// The highlighter for a file, null for one nothing has a grammar for, which is shown as plain text. What it was set
    /// to by hand comes first, then the associations, then its name (Dockerfile) and its extension, both with a backup's
    /// suffix taken off (a.cs.orig is C#), see CodeLanguages.Files.
    /// </summary>
    public static ICodeHighlighter? ForFile(string path) => Resolve(path, null, useOverride: true);

    /// <summary>
    /// The same, with the file's first line to go on for a file with no extension, a shebang (#!/usr/bin/env bash) says
    /// which language it is when nothing else does.
    /// </summary>
    public static ICodeHighlighter? ForFile(string path, string? firstLine) => Resolve(path, firstLine, useOverride: true);

    /// <summary>The highlighter for files with an extension (".cs"), null for one nothing has a grammar for.</summary>
    public static ICodeHighlighter? ForExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return null;

        lock (gate)
        {
            if (byExtension.TryGetValue(extension, out ICodeHighlighter? known))
                return known;

            ICodeHighlighter? found = null;
            if (ownExtensions.TryGetValue(extension, out string? own))
                found = ForScopeLocked(own, null);
            else if (OptionsLocked().GetLanguageByExtension(extension) is { } language && OptionsLocked().GetScopeByLanguageId(language.Id) is { } scope)
                found = ForScopeLocked(scope, language.Aliases is { Count: > 0 } aliases ? aliases[0] : language.Id);

            byExtension[extension] = found;
            return found;
        }
    }

    /// <summary>The highlighter for a grammar by its scope ("source.cs"), null for a scope no grammar has.</summary>
    public static ICodeHighlighter? ForScope(string scopeName)
    {
        lock (gate)
            return ForScopeLocked(scopeName, null);
    }

    /// <summary>
    /// Every language there's a grammar for, by name, D's among them and HIDL left out (Hex is its server), for a list to
    /// pick one from. The id is the grammar's language id, which is the one the language server protocol knows it by for
    /// nearly all of them (csharp, python, rust, d).
    /// </summary>
    public static IReadOnlyList<CodeLanguage> Languages()
    {
        lock (gate)
        {
            var found = new Dictionary<string, CodeLanguage>(StringComparer.OrdinalIgnoreCase)
            {
                ["d"] = new CodeLanguage("D", "d", [".d", ".di"]),
                ["slang"] = new CodeLanguage("Slang", "slang", [".slang"]),
                ["toml"] = new CodeLanguage("TOML", "toml", [".toml"])
            };

            foreach (Language language in OptionsLocked().GetAvailableLanguages())
            {
                if (language.Extensions is not { Count: > 0 } extensions || found.ContainsKey(language.Id))
                    continue;

                string name = language.Aliases is { Count: > 0 } aliases ? aliases[0] : language.Id;
                found[language.Id] = new CodeLanguage(name, language.Id, [.. extensions.Distinct(StringComparer.OrdinalIgnoreCase)]);
            }

            return [.. found.Values.OrderBy(language => language.Name, StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>Every extension a grammar is known for, for a file dialog or a test.</summary>
    public static IEnumerable<string> KnownExtensions()
    {
        lock (gate)
        {
            var all = new List<string>(ownExtensions.Keys);
            foreach (Language language in OptionsLocked().GetAvailableLanguages())
            {
                if (language.Extensions is not null)
                    all.AddRange(language.Extensions);
            }

            return all;
        }
    }

    /// <summary>
    /// The grammar of a scope loaded, for a highlighter that wants to read lines itself (HIDL's does). Null when no
    /// grammar has that scope.
    /// </summary>
    internal static IGrammar? LoadGrammar(string scopeName)
    {
        lock (gate)
        {
            try
            {
                return RegistryLocked().LoadGrammar(scopeName);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private static ICodeHighlighter? ForScopeLocked(string scopeName, string? name)
    {
        if (byScope.TryGetValue(scopeName, out ICodeHighlighter? known))
            return known;

        ICodeHighlighter? made;
        if (scopeName == HIDLGrammar.ScopeName)
        {
            made = HidlHighlighter.Instance;
        }
        else
        {
            IGrammar? grammar = LoadGrammarLocked(scopeName);
            made = grammar is null ? null : new TextMateHighlighter(grammar, name ?? grammar.GetName() ?? scopeName);
        }

        if (made is not null)
            byScope[scopeName] = made;
        return made;
    }

    private static IGrammar? LoadGrammarLocked(string scopeName)
    {
        try
        {
            return RegistryLocked().LoadGrammar(scopeName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Options OptionsLocked() => options ??= new Options();

    private static Registry RegistryLocked() => registry ??= new Registry(OptionsLocked());

    /// <summary>
    /// The registry's idea of where grammars are. TextMateSharp.Grammars has VS Code's, built into it, and the
    /// language's own comes out of the HIDL assembly. The theme is never used for a colour (the skin colours the kinds),
    /// a registry only wants one to exist.
    /// </summary>
    private sealed class Options : IRegistryOptions
    {
        private readonly RegistryOptions bundled = new(ThemeName.DarkPlus);

        public Language? GetLanguageByExtension(string extension) => bundled.GetLanguageByExtension(extension);

        public string? GetScopeByLanguageId(string languageId) => bundled.GetScopeByLanguageId(languageId);

        public List<Language> GetAvailableLanguages() => bundled.GetAvailableLanguages();

        public IRawGrammar? GetGrammar(string scopeName)
        {
            if (scopeName == HIDLGrammar.ScopeName)
            {
                using var reader = new StreamReader(HIDLGrammar.Open());
                return GrammarReader.ReadGrammarSync(reader);
            }

            if (ownGrammars.TryGetValue(scopeName, out string? resource)
                && typeof(CodeLanguages).Assembly.GetManifestResourceStream(resource) is { } stream)
            {
                using var reader = new StreamReader(stream);
                return GrammarReader.ReadGrammarSync(reader);
            }

            return Mended(scopeName, bundled.GetGrammar(scopeName));
        }

        public ICollection<string>? GetInjections(string scopeName) =>
            scopeName == HIDLGrammar.ScopeName || ownGrammars.ContainsKey(scopeName) ? null : bundled.GetInjections(scopeName);

        public IRawTheme? GetTheme(string scopeName) => bundled.GetTheme(scopeName);

        public IRawTheme GetDefaultTheme() => bundled.GetDefaultTheme();
    }
}
