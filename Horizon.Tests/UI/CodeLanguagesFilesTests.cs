using Horizon.UI.Scripting;

namespace Horizon.Tests.UI;

/// <summary>
/// Which language a file is by more than its last extension, a backup's suffix taken off, the files known by their
/// name, the associations of the settings and a language set by hand.
/// </summary>
public class CodeLanguagesFilesTests
{
    private static string? ScopeOf(string path) => (CodeLanguages.ForFile(path) as TextMateHighlighter)?.ScopeName;

    [Fact]
    public void A_backup_is_the_language_of_the_file_it_was_a_copy_of()
    {
        Assert.Equal("source.cs", ScopeOf("a.cs.orig"));
        Assert.Equal("source.cs", ScopeOf("a.cs.bak"));
        Assert.Equal("source.python", ScopeOf("a.py~"));
        Assert.Equal("source.ts", ScopeOf("x.ts.orig.bak"));
        Assert.Equal("source.go", ScopeOf("m.go.REMOTE_1234"));
        Assert.Equal("source.go", ScopeOf("m.go.LOCAL"));
        Assert.Equal("source.rust", ScopeOf("lib.rs.~1~"));
        Assert.Same(CodeLanguages.Hidl, CodeLanguages.ForFile("menu.hor.orig"));
        Assert.Equal("source.cs", ScopeOf(Path.Combine("some", "folder", "Thing.cs.orig")));
    }

    [Fact]
    public void Suffixes_come_off_every_one_and_only_from_the_end()
    {
        Assert.Equal("x.ts", CodeLanguages.WithoutBackupSuffix("x.ts.orig.bak"));
        Assert.Equal("m.go", CodeLanguages.WithoutBackupSuffix("m.go.REMOTE_1234"));
        Assert.Equal("a.py", CodeLanguages.WithoutBackupSuffix("a.py~"));
        Assert.Equal("lib.rs", CodeLanguages.WithoutBackupSuffix("lib.rs.~12~"));
        Assert.Equal("notes.txt", CodeLanguages.WithoutBackupSuffix("notes.txt"));
        Assert.Equal("orig.cs", CodeLanguages.WithoutBackupSuffix("orig.cs"));
        Assert.Equal("dir/Foo.cs", CodeLanguages.WithoutBackupSuffix("dir/Foo.cs.orig"));
        Assert.Equal("a.REMOTE_x", CodeLanguages.WithoutBackupSuffix("a.REMOTE_x"));

        // Nothing but a suffix is left as it is, there'd be no name
        Assert.Equal(".bak", CodeLanguages.WithoutBackupSuffix(".bak"));
        Assert.Equal("dir/.orig", CodeLanguages.WithoutBackupSuffix("dir/.orig"));
    }

    [Fact]
    public void Files_known_by_their_name_have_their_language()
    {
        Assert.NotNull(CodeLanguages.ForFile("Dockerfile"));
        Assert.NotNull(CodeLanguages.ForFile("Makefile"));
        Assert.NotNull(CodeLanguages.ForFile(Path.Combine("build", "GNUmakefile")));
        Assert.Same(CodeLanguages.ForFile("Dockerfile"), CodeLanguages.ForFile("Dockerfile.orig"));
        Assert.Same(CodeLanguages.ForLanguageId("groovy"), CodeLanguages.ForFile("Jenkinsfile"));
        Assert.Same(CodeLanguages.ForLanguageId("xml"), CodeLanguages.ForFile("Hex.csproj"));
        Assert.Same(CodeLanguages.ForLanguageId("xml"), CodeLanguages.ForFile("Directory.Build.props"));
        Assert.NotNull(CodeLanguages.ForFile(".env.local"));
        Assert.Null(CodeLanguages.ForFile("notes.txt"));

        // A suffix that's a language of its own still is when there's nothing under it
        Assert.Same(CodeLanguages.ForExtension(".rej"), CodeLanguages.ForFile("change.rej"));
    }

    [Fact]
    public void Languages_with_no_grammar_get_the_nearest_one()
    {
        Assert.Same(CodeLanguages.ForLanguageId("html"), CodeLanguages.ForFile("App.svelte"));
        Assert.Same(CodeLanguages.ForLanguageId("html"), CodeLanguages.ForFile("App.vue"));
        Assert.Same(CodeLanguages.ForLanguageId("ini"), CodeLanguages.ForFile("Cargo.toml"));
        Assert.Same(CodeLanguages.ForLanguageId("diff"), CodeLanguages.ForFile("x.rej"));
        Assert.Same(CodeLanguages.ForLanguageId("diff"), CodeLanguages.ForFile("x.cs.rej"));
        Assert.Same(CodeLanguages.ForLanguageId("diff"), CodeLanguages.ForFile("fix.patch"));
        Assert.Same(CodeLanguages.ForFile("main.tf"), CodeLanguages.ForFile("main.tf.bak"));
        Assert.Equal("x.rej", CodeLanguages.WithoutBackupSuffix("x.rej"));
    }

    [Fact]
    public void A_shebang_says_what_a_file_with_no_extension_is()
    {
        Assert.Same(CodeLanguages.ForLanguageId("shellscript"), CodeLanguages.ForFile("build", "#!/usr/bin/env bash"));
        Assert.Same(CodeLanguages.ForLanguageId("python"), CodeLanguages.ForFile("tool", "#!/usr/bin/python3"));
        Assert.Same(CodeLanguages.ForLanguageId("javascript"), CodeLanguages.ForFile("serve", "#!/usr/bin/env -S node --no-warnings"));
        Assert.Null(CodeLanguages.ForFile("tool", "just text"));
        Assert.Null(CodeLanguages.ForFile("notes.txt", "#!/bin/bash"));
    }

    [Fact]
    public void Languages_are_found_by_id_or_alias_and_say_their_id()
    {
        Assert.Same(CodeLanguages.Hidl, CodeLanguages.ForLanguageId("hidl"));
        Assert.Same(CodeLanguages.ForFile("a.cs"), CodeLanguages.ForLanguageId("csharp"));
        Assert.Same(CodeLanguages.ForFile("a.cs"), CodeLanguages.ForLanguageId("C#"));
        Assert.Null(CodeLanguages.ForLanguageId(CodeLanguages.PLAIN_TEXT));
        Assert.Null(CodeLanguages.ForLanguageId("no-such-language"));

        Assert.Equal("csharp", CodeLanguages.LanguageIdOf(CodeLanguages.ForFile("a.cs")));
        Assert.Equal("python", CodeLanguages.LanguageIdOf(CodeLanguages.ForFile("a.py")));
        Assert.Equal("hidl", CodeLanguages.LanguageIdOf(CodeLanguages.Hidl));
        Assert.Equal("d", CodeLanguages.LanguageIdOf(CodeLanguages.ForFile("a.d")));
        Assert.Equal(CodeLanguages.PLAIN_TEXT, CodeLanguages.LanguageIdOf(null));
    }

    [Fact]
    public void Associations_go_over_the_extension_the_most_specific_first()
    {
        try
        {
            CodeLanguages.SetAssociations([("*.weird", "json")]);
            Assert.Equal("source.json", ScopeOf("a.weird"));
            Assert.Equal("source.json", ScopeOf("a.weird.bak"));

            CodeLanguages.SetAssociations([("**/strange/*", "python"), ("*.weird", "json"), ("special.weird", "csharp"), ("*.{odd,queer}", "rust")]);
            Assert.Equal("source.cs", ScopeOf(Path.Combine("x", "special.weird")));
            Assert.Equal("source.json", ScopeOf(Path.Combine("x", "strange", "other.weird")));
            Assert.Equal("source.python", ScopeOf(Path.Combine("x", "strange", "other.cs")));
            Assert.Equal("source.rust", ScopeOf("a.odd"));
            Assert.Equal("source.rust", ScopeOf("a.QUEER"));
            Assert.Equal("source.cs", ScopeOf("a.cs"));

            CodeLanguages.SetAssociations([("*.cs", CodeLanguages.PLAIN_TEXT)]);
            Assert.Null(CodeLanguages.ForFile("plain.cs"));
        }
        finally
        {
            CodeLanguages.SetAssociations([]);
        }

        Assert.Null(CodeLanguages.ForFile("a.weird"));
        Assert.Equal("source.cs", ScopeOf("plain.cs"));
    }

    [Fact]
    public void A_language_set_by_hand_goes_over_everything_and_clearing_it_detects_again()
    {
        string path = Path.Combine(Path.GetTempPath(), "override-test", "Thing.cs");
        int was = CodeLanguages.Version;
        try
        {
            CodeLanguages.SetOverride(path, CodeLanguages.PLAIN_TEXT);
            Assert.Null(CodeLanguages.ForFile(path));
            Assert.Null(CodeLanguages.ForFile(path.ToUpperInvariant()));
            Assert.Equal("source.cs", (CodeLanguages.Detect(path) as TextMateHighlighter)?.ScopeName);
            Assert.True(CodeLanguages.Version > was);

            CodeLanguages.SetOverride(path, "python");
            Assert.Equal("source.python", ScopeOf(path));
            Assert.Equal("python", CodeLanguages.OverrideOf(path));

            // Another file of the same name elsewhere is its own
            Assert.Equal("source.cs", ScopeOf(Path.Combine(Path.GetTempPath(), "elsewhere", "Thing.cs")));
        }
        finally
        {
            CodeLanguages.SetOverride(path, null);
        }

        Assert.Equal("source.cs", ScopeOf(path));
        Assert.Null(CodeLanguages.OverrideOf(path));
    }
}
