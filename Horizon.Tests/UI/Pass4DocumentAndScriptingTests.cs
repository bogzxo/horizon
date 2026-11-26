using System.Numerics;
using System.Reflection;

using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Horizon.Tests.UI;

public sealed class Pass4DocumentAndScriptingTests
{
    private static UITextDocument Doc(string text, int line = 0, int column = 0)
    {
        var doc = new UITextDocument { Text = text };
        doc.MoveTo(new UITextPosition(line, column));
        return doc;
    }

    private static void TypeText(UITextDocument doc, string text)
    {
        foreach (char c in text)
            doc.Type(c);
    }

    [Fact]
    public void Multi_cursor_undo_and_redo_save_and_restore_extra_carets_and_selections()
    {
        var doc = Doc("alpha = 1;\nbeta = 2;\ngamma = 3;", 0, 5);
        doc.AddCaret(new UITextPosition(1, 4));
        doc.AddCaret(new UITextPosition(2, 5));
        Assert.Equal(3, doc.CaretCount);

        // Calling Undo when history is empty must not discard extra carets
        doc.Undo();
        Assert.Equal(3, doc.CaretCount);

        UITextPosition primaryBefore = doc.Caret;
        UITextSelection[] extrasBefore = [.. doc.ExtraCarets];

        TypeText(doc, "_v2");
        Assert.Equal("alpha_v2 = 1;\nbeta_v2 = 2;\ngamma_v2 = 3;", doc.Text);
        Assert.Equal(3, doc.CaretCount);

        UITextPosition primaryAfter = doc.Caret;
        UITextSelection[] extrasAfter = [.. doc.ExtraCarets];

        doc.Undo();
        Assert.Equal("alpha = 1;\nbeta = 2;\ngamma = 3;", doc.Text);
        Assert.Equal(3, doc.CaretCount);
        Assert.Equal(primaryBefore, doc.Caret);
        Assert.Equal(extrasBefore, doc.ExtraCarets);

        doc.Redo();
        Assert.Equal("alpha_v2 = 1;\nbeta_v2 = 2;\ngamma_v2 = 3;", doc.Text);
        Assert.Equal(3, doc.CaretCount);
        Assert.Equal(primaryAfter, doc.Caret);
        Assert.Equal(extrasAfter, doc.ExtraCarets);

        // Non-empty multi-caret selections also survive undo/redo
        doc.Select(new UITextPosition(0, 0), new UITextPosition(0, 5));
        doc.SetExtraCarets([
            new UITextSelection(new UITextPosition(1, 4), new UITextPosition(1, 0)),
            new UITextSelection(new UITextPosition(2, 5), new UITextPosition(2, 0))
        ]);
        doc.Insert("x");
        Assert.Equal("x_v2 = 1;\nx_v2 = 2;\nx_v2 = 3;", doc.Text);

        doc.Undo();
        Assert.Equal("alpha_v2 = 1;\nbeta_v2 = 2;\ngamma_v2 = 3;", doc.Text);
        Assert.Equal(3, doc.CaretCount);
        Assert.Equal(new UITextPosition(0, 5), doc.Caret);
        Assert.Equal(new UITextPosition(0, 0), doc.Anchor);
        Assert.Equal(new UITextSelection(new UITextPosition(1, 4), new UITextPosition(1, 0)), doc.ExtraCarets[0]);
        Assert.Equal(new UITextSelection(new UITextPosition(2, 5), new UITextPosition(2, 0)), doc.ExtraCarets[1]);
    }

    [Fact]
    public void Multi_caret_paste_preserves_intentional_trailing_newlines()
    {
        // Standard whole-line copy (single trailing newline) still distributes one line per caret
        var wholeLineCopy = Doc("x = ;\ny = ;", 0, 4);
        wholeLineCopy.AddCaret(new UITextPosition(1, 4));
        wholeLineCopy.Insert("1\n2\n");
        Assert.Equal("x = 1;\ny = 2;", wholeLineCopy.Text);

        // Multiple intentional trailing newlines must NOT be stripped to match caret count
        var intentionalTrailing = Doc("a\nb", 0, 1);
        intentionalTrailing.AddCaret(new UITextPosition(1, 1));
        intentionalTrailing.Insert("1\n2\n\n");
        Assert.Equal("a1\n2\n\n\nb1\n2\n\n", intentionalTrailing.Text);

        // Pure newline snippets must NOT be stripped to empty insertions
        var pureNewlines = Doc("a\nb", 0, 1);
        pureNewlines.AddCaret(new UITextPosition(1, 1));
        pureNewlines.Insert("\n\n");
        Assert.Equal("a\n\n\nb\n\n", pureNewlines.Text);

        // Single line with trailing newline pasted across two carets keeps the newline at each caret
        var singleWithNewline = Doc("a\nb", 0, 1);
        singleWithNewline.AddCaret(new UITextPosition(1, 1));
        singleWithNewline.Insert("!\n");
        Assert.Equal("a!\n\nb!\n", singleWithNewline.Text);
    }

    [Fact]
    public void EditAtEvery_handles_over_200_carets_accurately_across_edits()
    {
        const int lineCount = 240;
        var initialLines = new string[lineCount];
        for (int i = 0; i < lineCount; i++)
            initialLines[i] = $"item_{i:D3} = val;";

        var doc = new UITextDocument { Text = string.Join('\n', initialLines) };
        doc.MoveTo(new UITextPosition(0, 8));
        var extra = new List<UITextSelection>(219);
        for (int i = 1; i < 220; i++)
        {
            var pos = new UITextPosition(i, 8);
            extra.Add(new UITextSelection(pos, pos));
        }
        doc.SetExtraCarets(extra);
        Assert.Equal(220, doc.CaretCount);

        TypeText(doc, "_ok");
        for (int i = 0; i < 220; i++)
            Assert.Equal($"item_{i:D3}_ok = val;", doc.LineAt(i));
        for (int i = 220; i < lineCount; i++)
            Assert.Equal($"item_{i:D3} = val;", doc.LineAt(i));
        Assert.Equal(220, doc.CaretCount);
        Assert.Equal(new UITextPosition(0, 11), doc.Caret);

        doc.Backspace();
        doc.Backspace();
        doc.Backspace();
        for (int i = 0; i < 220; i++)
            Assert.Equal($"item_{i:D3} = val;", doc.LineAt(i));

        doc.NewLine();
        Assert.Equal(lineCount + 220, doc.LineCount);
        Assert.Equal(220, doc.CaretCount);

        doc.Undo();
        Assert.Equal(lineCount, doc.LineCount);
        Assert.Equal(220, doc.CaretCount);
    }

    [Fact]
    public void TextMateHighlighter_ReadAgain_clamps_line_indices_on_rapid_deletions()
    {
        ICodeHighlighter? highlighter = CodeLanguages.ForExtension(".cs");
        Assert.NotNull(highlighter);

        string[] before =
        [
            "using System;",
            "namespace Sample;",
            "public sealed class Demo",
            "{",
            "    public int A => 1;",
            "    public int B => 2;",
            "    public int C => 3;",
            "    public int D => 4;",
            "    public int E => 5;",
            "}"
        ];

        var startsBefore = new CodeLineStart[before.Length];
        List<SyntaxSpan>[] spansBefore = highlighter.Read(string.Join('\n', before), before.Length, startsBefore);
        var last = new CodeColouring(before, spansBefore, startsBefore);

        // Rapid tail deletion: 10 lines -> 3 lines
        string[] tailDeleted = before[..3];
        var tailStarts = new CodeLineStart[tailDeleted.Length];
        List<SyntaxSpan>[] tailAgain = highlighter.ReadAgain(string.Join('\n', tailDeleted), tailDeleted, tailStarts, last);
        Assert.Equal(3, tailAgain.Length);
        Assert.Same(spansBefore[0], tailAgain[0]);
        Assert.Same(spansBefore[2], tailAgain[2]);

        // Rapid middle deletion + shorter starts buffer (must clamp without throwing)
        string[] middleDeleted = [before[0], before[^1]];
        var shortStarts = new CodeLineStart[1];
        List<SyntaxSpan>[] middleAgain = highlighter.ReadAgain(string.Join('\n', middleDeleted), middleDeleted, shortStarts, last);
        Assert.Equal(2, middleAgain.Length);

        // Rapid deletion down to empty array (lines.Length == 0, starts.Length == 0)
        List<SyntaxSpan>[] emptyAgain = highlighter.ReadAgain(string.Empty, [], [], last);
        Assert.Single(emptyAgain);
    }

    [Fact]
    public void HidlHighlighter_ReadAgain_preserves_untouched_lines_and_matches_full_read()
    {
        HidlHighlighter hidl = HidlHighlighter.Instance;
        string[] before =
        [
            "#define layout",
            "let theme = { color: vec(0.2, 0.4, 0.8, 1.0) };",
            "let btn = compositor.button({ icon: \"save\", text: \"$(play) Run\" });",
            "func first(a, b) {",
            "    let sum = a + b;",
            "    return sum;",
            "}",
            "func middle(x) {",
            "    let value = x + 1;",
            "    return value;",
            "}",
            "let footer = compositor.label({ color: vec(0.9, 0.1, 0.1, 1.0), text: \"[icon:pad_a]\" });"
        ];

        var startsBefore = new CodeLineStart[before.Length];
        var swatchesBefore = new List<HidlSwatch>();
        var artBefore = new List<HidlArt>();
        List<SyntaxSpan>[] spansBefore = hidl.Read(string.Join('\n', before), before.Length, startsBefore, swatchesBefore, artBefore);
        var last = new CodeColouring(before, spansBefore, startsBefore);

        // Localized edit inside middle() — prefix and suffix lines must keep the exact same List<SyntaxSpan> instances
        string[] after = [.. before];
        after[8] = "    let value = x + 42; let tint = vec(0.1, 0.8, 0.2, 1.0);";

        var startsAgain = new CodeLineStart[after.Length];
        var swatchesAgain = new List<HidlSwatch>();
        var artAgain = new List<HidlArt>();
        List<SyntaxSpan>[] spansAgain = hidl.ReadAgain(string.Join('\n', after), after, startsAgain, last, swatchesAgain, artAgain);

        var startsWhole = new CodeLineStart[after.Length];
        var swatchesWhole = new List<HidlSwatch>();
        var artWhole = new List<HidlArt>();
        List<SyntaxSpan>[] spansWhole = hidl.Read(string.Join('\n', after), after.Length, startsWhole, swatchesWhole, artWhole);

        // Untouched prefix and suffix span lists are preserved by reference, not copied
        Assert.Same(spansBefore[0], spansAgain[0]);
        Assert.Same(spansBefore[1], spansAgain[1]);
        Assert.Same(spansBefore[2], spansAgain[2]);
        Assert.Same(spansBefore[^1], spansAgain[^1]);

        // Full parity with Read() on spans, depths, swatches, and art
        Assert.Equal(spansWhole.Length, spansAgain.Length);
        for (int i = 0; i < spansWhole.Length; i++)
        {
            Assert.Equal(spansWhole[i], spansAgain[i]);
            Assert.Equal(startsWhole[i].Depth, startsAgain[i].Depth);
        }
        Assert.Equal(swatchesWhole, swatchesAgain);
        Assert.Equal(artWhole, artAgain);

        // Rapid tail deletion on HidlHighlighter
        string[] shrunk = before[..3];
        var shrunkStarts = new CodeLineStart[shrunk.Length];
        var shrunkSwatches = new List<HidlSwatch>();
        var shrunkArt = new List<HidlArt>();
        List<SyntaxSpan>[] shrunkAgain = hidl.ReadAgain(string.Join('\n', shrunk), shrunk, shrunkStarts, last, shrunkSwatches, shrunkArt);
        Assert.Equal(3, shrunkAgain.Length);
        Assert.Same(spansBefore[0], shrunkAgain[0]);
        Assert.Single(shrunkSwatches);
        Assert.Equal(2, shrunkArt.Count);
    }

    private sealed class RecordingOwner : IUIScriptOwner
    {
        public readonly List<(string Fn, int ArgCount)> Calls = [];
        public string Name => "test_owner";
        public string? Failed => null;

        public bool Invoke(IRuntimeValue function, IRuntimeValue[] arguments, out IRuntimeValue returned)
        {
            Calls.Add((((StringValue)function).Value, arguments.Length));
            returned = Values.Bool(false);
            return true;
        }
    }

    [Fact]
    public void UIScript_snake_case_enum_and_scripted_layout_evaluation_parity()
    {
        Assert.Equal(string.Empty, UIScript.ToSnakeCase(string.Empty));
        Assert.Equal("a", UIScript.ToSnakeCase("A"));
        Assert.Equal("top_left", UIScript.ToSnakeCase("TopLeft"));
        Assert.Equal("bottom_right", UIScript.ToSnakeCase("BottomRight"));
        Assert.Equal("center", UIScript.ToSnakeCase("Center"));

        // Enum conversion caches StringValue instances and parses both snake_case and PascalCase
        StringValue first = UIScript.FromEnum(Origin.TopLeft);
        StringValue second = UIScript.FromEnum(Origin.TopLeft);
        Assert.Same(first.Value, second.Value);
        Assert.Equal("top_left", first.Value);
        Assert.Equal(Origin.TopLeft, UIScript.ToEnum<Origin>(new StringValue("top_left"), "anchor"));
        Assert.Equal(Origin.BottomRight, UIScript.ToEnum<Origin>(new StringValue("BOTTOM_RIGHT"), "anchor"));
        Assert.Equal(Origin.Center, UIScript.ToEnum<Origin>(new StringValue("Center"), "anchor"));

        Exception invalid = Assert.Throws<Exception>(() => UIScript.ToEnum<Origin>(new StringValue("invalid_origin"), "anchor"));
        Assert.Contains("top_left", invalid.Message);

        // Text list conversion roundtrip
        ListValue list = UIScript.FromTexts(["one", "two"]);
        Assert.Equal(["one", "two"], UIScript.ToTexts(list, "items"));
        Assert.Equal(["a", "b"], UIScript.ToTexts(new StringValue("a, b"), "items"));

        // Property help lookup parity without allocating kind + "." + name
        Assert.Equal("where the handle is, between min and max", UIPropertyHelp.Describe("slider", "value"));
        Assert.Equal("what it is set to", UIPropertyHelp.Describe("panel", "value"));
        Assert.Equal(string.Empty, UIPropertyHelp.Describe("slider", "nonexistent_prop"));

        // UIScriptedLayout caches native member wrappers and invokes resize/tick/key
        var owner = new RecordingOwner();
        Vector2 room = new(320.0f, 180.0f);
        var layout = new UIScriptedLayout(owner, "view", null!, new Panel(), "sample.hor", null, () => room);
        Assert.Same(layout.Get("get"), layout.Get("get"));
        Assert.Same(layout.Get("close"), layout.Get("close"));
        Assert.Same(layout.Get("file"), layout.Get("file"));

        layout.Set("on_resize", new StringValue("resize"));
        layout.Set("on_tick", new StringValue("tick"));
        layout.Set("on_key", new StringValue("key"));

        NumberValue? number = null;
        layout.Update(0.016f, ref number, _ => { });
        layout.Update(0.016f, ref number, _ => { });
        layout.Press("left", _ => { });

        Assert.Equal([("resize", 2), ("tick", 2), ("tick", 2), ("key", 2)], owner.Calls);

        // SemanticOverlay binary-search Kept parity with kept spans
        var under = new List<SyntaxSpan>
        {
            new(0, 5, SyntaxKind.Comment),
            new(6, 4, SyntaxKind.Name),
            new(12, 8, SyntaxKind.Text),
            new(22, 4, SyntaxKind.Name)
        };
        var over = new List<SyntaxSpan>
        {
            new(0, 5, SyntaxKind.Local),   // skipped (comment)
            new(12, 8, SyntaxKind.Local),  // skipped (text)
            new(22, 4, SyntaxKind.Method)  // applied
        };
        var merged = new List<SyntaxSpan>();
        SemanticOverlay.Overlay(under, over, merged);
        Assert.Equal(SyntaxKind.Comment, merged[0].Kind);
        Assert.Equal(SyntaxKind.Name, merged[1].Kind);
        Assert.Equal(SyntaxKind.Text, merged[2].Kind);
        Assert.Equal(SyntaxKind.Method, merged[3].Kind);

        // CodeComments span-based scope lookup parity
        Assert.Equal("//", CodeComments.For("source.cs").Line);
        Assert.Equal("<!--", CodeComments.For("text.html.markdown").BlockStart);
    }

    private const BindingFlags ALL = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private const byte NEWOBJ = 0x73;

    public static TheoryData<Type, string> ModifiedHotPaths => new()
    {
        { typeof(UITextDocument), "Type" },
        { typeof(UITextDocument), "Insert" },
        { typeof(UITextDocument), "NewLine" },
        { typeof(UITextDocument), "Backspace" },
        { typeof(UITextDocument), "Delete" },
        { typeof(UITextDocument), "DeleteWord" },
        { typeof(UITextDocument), "MoveHorizontally" },
        { typeof(UITextDocument), "MoveVertically" },
        { typeof(UITextDocument), "MoveWord" },
        { typeof(UITextDocument), "MoveHome" },
        { typeof(UITextDocument), "MoveEnd" },
        { typeof(UITextDocument), "IndentLines" },
        { typeof(UITextDocument), "Outdent" },
        { typeof(UITextDocument), "Duplicate" },
        { typeof(UITextDocument), "Undo" },
        { typeof(UITextDocument), "Redo" },
        { typeof(UITextDocument), "EditAtEvery" },
        { typeof(UITextDocument), "SetLines" },
        { typeof(UITextDocument), "InsertRaw" },
        { typeof(UITextDocument), "DeleteRaw" },
        { typeof(UITextDocument), "CopyLines" },
        { typeof(UITextDocument), "ExpandLineSelection" },
        { typeof(TextMateHighlighter), "ReadAgain" },
        { typeof(TextMateHighlighter), "ReadFrom" },
        { typeof(HidlHighlighter), "ReadAgain" },
        { typeof(HidlHighlighter), "ScanLinesFrom" },
        { typeof(HidlHighlighter), "Place" },
        { typeof(SemanticOverlay), "Overlay" },
        { typeof(SemanticOverlay), "Kept" },
        { typeof(CodeComments), "For" },
        { typeof(CodeComments), "FromScope" },
        { typeof(CodeLanguages), "ColorsFor" },
        { typeof(CodeLanguages), "ByShebang" },
        { typeof(CodeLanguages), "AssociationOf" },
        { typeof(CodeLanguages), "Specificity" },
        { typeof(UIScript), "ToTexts" },
        { typeof(UIScript), "FromTexts" },
        { typeof(UIScript), "ToEnum" },
        { typeof(UIScript), "FromEnum" },
        { typeof(UIScript), "ToSnakeCase" },
        { typeof(UIScriptedLayout), "Get" },
        { typeof(UIScriptedLayout), "Update" },
        { typeof(UIScriptedLayout), "Press" },
        { typeof(UIPropertyHelp), "Describe" },
    };

    [Theory]
    [MemberData(nameof(ModifiedHotPaths))]
    public void Modified_hot_paths_make_no_closure(Type type, string name)
    {
        MethodInfo[] methods = [.. type.GetMethods(ALL).Where(method => method.Name == name)];
        Assert.True(methods.Length > 0, $"{type.Name} has no method called {name}.");

        foreach (MethodInfo method in methods)
        {
            string? closure = ClosureMadeIn(method);
            Assert.True(closure is null, $"{type.Name}.{name} allocates closure {closure}.");
        }
    }

    private static string? ClosureMadeIn(MethodBase method)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
            return null;

        Type[]? ofType = method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null;
        Type[]? ofMethod = method.IsGenericMethod ? method.GetGenericArguments() : null;

        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != NEWOBJ)
                continue;

            MethodBase? made;
            try
            {
                made = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1), ofType, ofMethod);
            }
            catch (Exception)
            {
                continue;
            }

            if (made?.DeclaringType is { } declared && declared.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal))
                return declared.Name;
        }

        return null;
    }
}
