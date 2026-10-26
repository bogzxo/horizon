using System.Numerics;

using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.Tests.Scripting;

/// <summary>
/// Semantic highlighting, a server's semantic tokens decoded and mapped onto what a name is, laid over a grammar's
/// colours, and the colours of the kinds with what a skin that never heard of them falls back to.
/// </summary>
public class SemanticTokensTests
{
    private static readonly string[] Types = ["class", "method", "variable", "field name", "static symbol", "keyword", "parameter", "extension method name", "record class name", "enumMember"];
    private static readonly string[] Modifiers = ["declaration", "static", "deprecated", "modification", "readonly"];

    [Fact]
    public void The_five_numbers_a_token_are_counted_from_the_one_before()
    {
        // class Foo { int count; }      Foo, then count on the same line 10 along
        //   Foo.Make(x);                Make two lines down, then x 5 along from it
        int[] data =
        [
            0, 6, 3, 0, 0b1,
            0, 10, 5, 3, 0,
            2, 6, 4, 1, 0,
            0, 5, 1, 2, 0
        ];

        List<SemanticSpan> found = LanguageServerSemantics.Decode(data, Types, Modifiers);

        Assert.Equal(
        [
            new SemanticSpan(0, 6, 3, SyntaxKind.Class, SyntaxModifiers.Declaration),
            new SemanticSpan(0, 16, 5, SyntaxKind.Field),
            new SemanticSpan(2, 6, 4, SyntaxKind.Method),
            new SemanticSpan(2, 11, 1, SyntaxKind.Local)
        ], found);
    }

    [Fact]
    public void A_static_symbol_goes_onto_the_name_it_is_on_and_keywords_are_left_to_the_grammar()
    {
        int[] data =
        [
            0, 0, 6, 5, 0,
            0, 7, 4, 1, 0,
            0, 0, 4, 4, 0,
            0, 5, 2, 4, 0,
            0, 0, 2, 0, 0
        ];

        List<SemanticSpan> found = LanguageServerSemantics.Decode(data, Types, Modifiers);

        Assert.Equal([new SemanticSpan(0, 7, 4, SyntaxKind.Method, SyntaxModifiers.Static), new SemanticSpan(0, 12, 2, SyntaxKind.Class, SyntaxModifiers.Static)], found);
    }

    [Fact]
    public void Modifiers_come_from_their_bits_and_only_a_local_or_a_parameter_is_mutable()
    {
        int[] data =
        [
            0, 0, 1, 2, 0b1000,
            0, 2, 1, 6, 0b1000,
            0, 2, 1, 3, 0b1000,
            0, 2, 3, 1, 0b0100,
            0, 4, 3, 3, 0b10010
        ];

        List<SemanticSpan> found = LanguageServerSemantics.Decode(data, Types, Modifiers);

        Assert.Equal(SyntaxModifiers.Mutable, found[0].Modifiers);
        Assert.Equal(SyntaxModifiers.Mutable, found[1].Modifiers);
        Assert.Equal(SyntaxModifiers.None, found[2].Modifiers);
        Assert.Equal(SyntaxModifiers.Deprecated, found[3].Modifiers);
        Assert.Equal(SyntaxModifiers.Static | SyntaxModifiers.Readonly, found[4].Modifiers);
    }

    [Theory]
    [InlineData("class", SyntaxKind.Class)]
    [InlineData("struct", SyntaxKind.Struct)]
    [InlineData("interface", SyntaxKind.Interface)]
    [InlineData("enum", SyntaxKind.Enum)]
    [InlineData("enumMember", SyntaxKind.EnumMember)]
    [InlineData("enum member name", SyntaxKind.EnumMember)]
    [InlineData("typeParameter", SyntaxKind.TypeParameter)]
    [InlineData("namespace", SyntaxKind.Namespace)]
    [InlineData("function", SyntaxKind.Method)]
    [InlineData("method", SyntaxKind.Method)]
    [InlineData("extensionMethod", SyntaxKind.ExtensionMethod)]
    [InlineData("extension method name", SyntaxKind.ExtensionMethod)]
    [InlineData("property", SyntaxKind.Property)]
    [InlineData("field", SyntaxKind.Field)]
    [InlineData("constant name", SyntaxKind.NamedConstant)]
    [InlineData("event", SyntaxKind.Event)]
    [InlineData("variable", SyntaxKind.Local)]
    [InlineData("local name", SyntaxKind.Local)]
    [InlineData("parameter", SyntaxKind.Parameter)]
    [InlineData("label", SyntaxKind.Label)]
    [InlineData("record", SyntaxKind.Record)]
    [InlineData("record struct name", SyntaxKind.Record)]
    [InlineData("delegate name", SyntaxKind.Delegate)]
    public void Every_kind_of_name_the_servers_say_is_mapped(string type, SyntaxKind kind)
    {
        Assert.Equal(kind, LanguageServerSemantics.KindOf(type).Kind);
    }

    [Theory]
    [InlineData("keyword")]
    [InlineData("comment")]
    [InlineData("string")]
    [InlineData("number")]
    [InlineData("operator")]
    [InlineData("string - verbatim")]
    [InlineData("keyword - control")]
    public void What_a_grammar_colours_is_not_a_name(string type)
    {
        Assert.Null(LanguageServerSemantics.KindOf(type).Kind);
    }

    [Fact]
    public void Names_are_laid_over_the_grammar_but_never_over_a_text_or_a_comment()
    {
        // var thing = Make("x"); // done
        List<SyntaxSpan> under =
        [
            new(0, 3, SyntaxKind.Keyword),
            new(4, 5, SyntaxKind.Name),
            new(10, 1, SyntaxKind.Operator),
            new(12, 4, SyntaxKind.Call),
            new(16, 1, SyntaxKind.Bracket1),
            new(17, 3, SyntaxKind.Text),
            new(20, 1, SyntaxKind.Bracket1),
            new(21, 1, SyntaxKind.Punctuation),
            new(23, 7, SyntaxKind.Comment)
        ];
        List<SyntaxSpan> over =
        [
            new(4, 5, SyntaxKind.Local, SyntaxModifiers.Declaration),
            new(12, 4, SyntaxKind.Method),
            new(18, 1, SyntaxKind.Local),
            new(26, 4, SyntaxKind.Local)
        ];
        var laid = new List<SyntaxSpan>();

        SemanticOverlay.Overlay(under, over, laid);

        Assert.Equal(
        [
            new(0, 3, SyntaxKind.Keyword),
            new(4, 5, SyntaxKind.Local, SyntaxModifiers.Declaration),
            new(10, 1, SyntaxKind.Operator),
            new(12, 4, SyntaxKind.Method),
            new(16, 1, SyntaxKind.Bracket1),
            new(17, 3, SyntaxKind.Text),
            new(20, 1, SyntaxKind.Bracket1),
            new(21, 1, SyntaxKind.Punctuation),
            new(23, 7, SyntaxKind.Comment)
        ], laid);
    }

    [Fact]
    public void A_name_cuts_a_wider_span_and_one_in_a_gap_of_the_grammar_still_goes_in()
    {
        List<SyntaxSpan> under = [new(0, 10, SyntaxKind.Name), new(14, 2, SyntaxKind.Operator)];
        List<SyntaxSpan> over = [new(3, 4, SyntaxKind.Field), new(11, 2, SyntaxKind.Parameter)];
        var laid = new List<SyntaxSpan>();

        SemanticOverlay.Overlay(under, over, laid);

        Assert.Equal(
        [
            new(0, 3, SyntaxKind.Name),
            new(3, 4, SyntaxKind.Field),
            new(7, 3, SyntaxKind.Name),
            new(11, 2, SyntaxKind.Parameter),
            new(14, 2, SyntaxKind.Operator)
        ], laid);
    }

    [Fact]
    public void A_skin_without_the_new_kinds_falls_back_to_the_closest_old_one()
    {
        var colors = new UISyntaxColors { Type = new Vector4(1, 0, 0, 1), Call = new Vector4(0, 1, 0, 1), Declaration = new Vector4(0, 0, 1, 1) };

        Assert.Equal(colors.Type, colors.Of(SyntaxKind.Class));
        Assert.Equal(colors.Type, colors.Of(SyntaxKind.Record));
        Assert.Equal(colors.Type, colors.Of(SyntaxKind.Interface));
        Assert.Equal(colors.Call, colors.Of(SyntaxKind.Method));
        Assert.Equal(colors.Call, colors.Of(SyntaxKind.ExtensionMethod));
        Assert.Equal(colors.Property, colors.Of(SyntaxKind.Field));
        Assert.Equal(colors.Constant, colors.Of(SyntaxKind.NamedConstant));
        Assert.Equal(colors.Declaration, colors.Of(SyntaxKind.Parameter));
        Assert.Equal(colors.Name, colors.Of(SyntaxKind.Local));
        Assert.Equal(colors.Name, colors.Of(SyntaxKind.Namespace));

        // A name being made with no colour of its own is a declaration, the way it always was
        Assert.Equal(colors.Declaration, colors.Of(SyntaxKind.Local, SyntaxModifiers.Declaration));
        Assert.Equal(colors.Declaration, colors.Of(SyntaxKind.Field, SyntaxModifiers.Declaration));
        Assert.Equal(colors.Call, colors.Of(SyntaxKind.Method, SyntaxModifiers.Declaration));
        Assert.Equal(colors.Type, colors.Of(SyntaxKind.Class, SyntaxModifiers.Declaration));

        // Set, and a skin file can set it by its key
        Assert.True(colors.Set("record", new Vector4(1, 1, 0, 1)));
        Assert.Equal(new Vector4(1, 1, 0, 1), colors.Of(SyntaxKind.Record));
        Assert.Equal(new Vector4(1, 1, 0, 1), colors.Of(SyntaxKind.Record, SyntaxModifiers.Declaration));
        Assert.Equal(colors.Type, colors.Of(SyntaxKind.Class));

        var copy = new UISyntaxColors();
        colors.CopyTo(copy);
        Assert.Equal(new Vector4(1, 1, 0, 1), copy.Record);
    }

    [Fact]
    public void ReSharpers_colours_are_rider_dark_and_resharper_light()
    {
        static Vector4 Rgb(uint rgb) => new(((rgb >> 16) & 0xFF) / 255.0f, ((rgb >> 8) & 0xFF) / 255.0f, (rgb & 0xFF) / 255.0f, 1.0f);
        UISyntaxColors dark = UISyntaxColors.ReSharperDark(), light = UISyntaxColors.ReSharperLight();

        Assert.Equal(Rgb(0xC191FF), dark.Of(SyntaxKind.Class));
        Assert.Equal(Rgb(0xE1BFFF), dark.Of(SyntaxKind.Struct));
        Assert.Equal(Rgb(0x39CC9B), dark.Of(SyntaxKind.Method));
        Assert.Equal(Rgb(0x66C3CC), dark.Of(SyntaxKind.Field));
        Assert.Equal(Rgb(0xED94C0), dark.Of(SyntaxKind.Event));
        Assert.Equal(Rgb(0xBDBDBD), dark.Of(SyntaxKind.Local, SyntaxModifiers.Declaration));

        Assert.Equal(Rgb(0x008B8B), light.Of(SyntaxKind.Method));
        Assert.Equal(Rgb(0x800080), light.Of(SyntaxKind.Field));
        Assert.Equal(Rgb(0xFF00FF), light.Of(SyntaxKind.Event));
        Assert.Equal(Rgb(0x0000FF), light.Of(SyntaxKind.Keyword));
    }

    [Fact]
    public void A_local_written_to_again_is_mutable_everywhere_for_a_server_that_does_not_say()
    {
        string[] lines =
        [
            "int Sum(int by, int times)",
            "{",
            "    int total = 0;",
            "    int once = by;",
            "    total += once;",
            "    times--;",
            "    return total == once ? total : by;",
            "}",
            "int Other() { int total = 1; return total; }"
        ];
        List<SemanticSpan> spans =
        [
            new(0, 4, 3, SyntaxKind.Method), new(0, 12, 2, SyntaxKind.Parameter), new(0, 20, 5, SyntaxKind.Parameter),
            new(2, 8, 5, SyntaxKind.Local), new(3, 8, 4, SyntaxKind.Local), new(3, 15, 2, SyntaxKind.Parameter),
            new(4, 4, 5, SyntaxKind.Local), new(4, 13, 4, SyntaxKind.Local), new(5, 4, 5, SyntaxKind.Parameter),
            new(6, 11, 5, SyntaxKind.Local), new(6, 20, 4, SyntaxKind.Local), new(6, 27, 5, SyntaxKind.Local), new(6, 35, 2, SyntaxKind.Parameter),
            new(8, 4, 5, SyntaxKind.Method), new(8, 18, 5, SyntaxKind.Local), new(8, 36, 5, SyntaxKind.Local)
        ];

        List<SemanticSpan> marked = SemanticWrites.Mark(spans, lines);
        bool Mutable(int i) => marked[i].Modifiers.HasFlag(SyntaxModifiers.Mutable);

        Assert.True(Mutable(3) && Mutable(6) && Mutable(9) && Mutable(11));
        Assert.True(Mutable(2) && Mutable(8));
        Assert.False(Mutable(4) || Mutable(7) || Mutable(10));
        Assert.False(Mutable(1) || Mutable(5) || Mutable(12));

        // Another method's total of the same name is its own, written once where it's made
        Assert.False(Mutable(14) || Mutable(15));
    }

    [Fact]
    public void Delegates_records_and_extension_methods_are_made_out_from_their_declarations()
    {
        string[] lines =
        [
            "public delegate void Changed(int by);",
            "public record class Size(int Width);",
            "public static int Twice(this int value) => value * 2;",
            "Changed handler = null; var s = new Size(1); 2.Twice();"
        ];
        List<SemanticSpan> spans =
        [
            new(0, 21, 7, SyntaxKind.Class), new(1, 20, 4, SyntaxKind.Class), new(2, 18, 5, SyntaxKind.Method),
            new(3, 0, 7, SyntaxKind.Class), new(3, 36, 4, SyntaxKind.Class), new(3, 47, 5, SyntaxKind.Method)
        ];

        List<SemanticSpan> refined = SemanticDeclarations.Refine(spans, lines);

        Assert.Equal(
        [
            SyntaxKind.Delegate, SyntaxKind.Record, SyntaxKind.ExtensionMethod, SyntaxKind.Delegate, SyntaxKind.Record, SyntaxKind.ExtensionMethod
        ], refined.Select(span => span.Kind));
    }

    [Fact]
    public void An_obsolete_diagnostic_is_deprecated_by_its_tag_or_by_csharps_code()
    {
        var toServer = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
        var toClient = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
        var toClientRead = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, toClient.ClientSafePipeHandle);

        // What the client says is read and dropped, initialize is bigger than a pipe holds and would wait on it for good
        var toServerRead = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, toServer.ClientSafePipeHandle);
        _ = Task.Run(() => toServerRead.CopyTo(Stream.Null));
        using LanguageServer server = LanguageServer.Over("obsolete", "csharp", Path.GetTempPath(), toClientRead, toServer);

        string uri = new Uri(Path.Combine(Path.GetTempPath(), "old.cs")).AbsoluteUri;
        string json = $$$$"""{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":"{{{{uri}}}}","diagnostics":[{"range":{"start":{"line":1,"character":4},"end":{"line":1,"character":7}},"severity":2,"message":"tagged","tags":[2]},{"range":{"start":{"line":2,"character":4},"end":{"line":2,"character":7}},"severity":2,"message":"coded","code":"CS0618"},{"range":{"start":{"line":3,"character":4},"end":{"line":3,"character":7}},"severity":2,"message":"plain","code":"CS0168"}]}}""";
        byte[] body = System.Text.Encoding.UTF8.GetBytes(json);
        toClient.Write(System.Text.Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        toClient.Write(body);
        toClient.Flush();

        IReadOnlyList<Horizon.UI.Components.UICodeDiagnostic> found = [];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!server.TryTake(out _, out found) && watch.ElapsedMilliseconds < 5000)
            Thread.Sleep(10);

        Assert.Equal([true, true, false], found.Select(diagnostic => diagnostic.Deprecated));
        toClient.Dispose();
    }
}
